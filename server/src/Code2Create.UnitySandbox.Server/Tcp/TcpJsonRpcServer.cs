using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.Server;
using Code2Create.UnitySandbox.Server.Rpc;
using Code2Create.UnitySandbox.Server.Sync;
using Code2Create.UnitySandbox.World;
using Code2Create.UnitySandbox.World.Transport;

namespace Code2Create.UnitySandbox.Server.Tcp
{
    public sealed class TcpLineTooLongException : IOException
    {
        public TcpLineTooLongException(int maxLineBytes)
            : base($"TCP JSON-RPC line exceeded maximum length of {maxLineBytes} bytes.")
        {
        }
    }

    /// <summary>
    /// Newline-delimited UTF-8 JSON-RPC 2.0 transport over a TCP NetworkStream with bounded line buffering.
    /// </summary>
    public sealed class TcpLineTransport : IJsonRpcLineTransport, IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly Stream stream;
        private readonly int maxLineBytes;
        private readonly byte[] readBuffer = new byte[8192];
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);

        private int readPos;
        private int readLen;

        public TcpLineTransport(Stream stream, int maxLineBytes = 65_536)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (maxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
            this.stream = stream;
            this.maxLineBytes = maxLineBytes;
            writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 8192, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var lineBuffer = new MemoryStream();
            while (true)
            {
                if (readPos < readLen)
                {
                    int nlIndex = Array.IndexOf(readBuffer, (byte)'\n', readPos, readLen - readPos);
                    if (nlIndex >= 0)
                    {
                        int count = nlIndex - readPos;
                        if (lineBuffer.Length + count > maxLineBytes + 1)
                        {
                            throw new TcpLineTooLongException(maxLineBytes);
                        }
                        if (count > 0)
                        {
                            lineBuffer.Write(readBuffer, readPos, count);
                        }
                        readPos = nlIndex + 1;

                        int finalLength = (int)lineBuffer.Length;
                        byte[] raw = lineBuffer.GetBuffer();
                        if (finalLength > 0 && raw[finalLength - 1] == (byte)'\r')
                        {
                            finalLength--;
                        }
                        if (finalLength > maxLineBytes)
                        {
                            throw new TcpLineTooLongException(maxLineBytes);
                        }
                        return Utf8NoBom.GetString(raw, 0, finalLength);
                    }

                    int available = readLen - readPos;
                    int limitBeforeNl = maxLineBytes + (available > 0 && readBuffer[readLen - 1] == (byte)'\r' ? 1 : 0);
                    if (lineBuffer.Length + available > limitBeforeNl)
                    {
                        throw new TcpLineTooLongException(maxLineBytes);
                    }
                    lineBuffer.Write(readBuffer, readPos, available);
                    readPos = 0;
                    readLen = 0;
                }

                int bytesRead = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return null;
                }
                readPos = 0;
                readLen = bytesRead;
            }
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        public void Dispose()
        {
            writeGate.Dispose();
            writer.Dispose();
        }
    }

    /// <summary>
    /// TCP listener accepting McRemote-compatible newline-delimited JSON-RPC 2.0 connections (Bridge / Python / direct TCP clients).
    /// Enforces pre-hello connection caps, timeouts, post-hello TCP keepalive, pairBegin rate limits, and max line size.
    /// </summary>
    public sealed class TcpJsonRpcServer : IDisposable
    {
        private readonly SandboxWorld world;
        private readonly IViewerStreamBindingStore bindingStore;
        private readonly IPairingApprovalStore pairingStore;
        private readonly ISandboxTokenStore tokenStore;
        private readonly DisplaySyncHub displaySyncHub;
        private readonly Func<DateTimeOffset> clock;
        private readonly bool authEnforcement;
        private readonly string serverName;
        private readonly int buildRange;
        private readonly string serverVersion;
        private readonly SandboxConnectionLimits limits;
        private readonly PairBeginRateLimiter pairBeginRateLimiter;
        private readonly TcpListener listener;
        private readonly ConcurrentDictionary<string, TcpClient> activeClients = new ConcurrentDictionary<string, TcpClient>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> activePerIpCount = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object admissionGate = new object();
        private readonly object lifecycleGate = new object();

        private int activeTotalCount;
        private int activePreHelloCount;
        private CancellationTokenSource? cts;
        private Task? acceptLoopTask;
        private int started;

        public TcpJsonRpcServer(
            SandboxWorld world,
            IViewerStreamBindingStore bindingStore,
            IPairingApprovalStore pairingStore,
            ISandboxTokenStore tokenStore,
            DisplaySyncHub displaySyncHub,
            IPAddress listenAddress,
            int listenPort,
            Func<DateTimeOffset>? clock = null,
            bool authEnforcement = true,
            string serverName = WorldConstants.DefaultServerName,
            int buildRange = WorldConstants.DefaultBuildRange,
            string serverVersion = WorldConstants.DefaultServerVersion,
            SandboxConnectionLimits? limits = null,
            PairBeginRateLimiter? pairBeginRateLimiter = null)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.bindingStore = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
            this.pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
            this.tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
            this.displaySyncHub = displaySyncHub ?? throw new ArgumentNullException(nameof(displaySyncHub));
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            this.authEnforcement = authEnforcement;
            this.serverName = string.IsNullOrEmpty(serverName) ? WorldConstants.DefaultServerName : serverName;
            this.buildRange = buildRange;
            this.serverVersion = string.IsNullOrWhiteSpace(serverVersion) ? WorldConstants.DefaultServerVersion : serverVersion;
            this.limits = limits ?? new SandboxConnectionLimits();
            this.pairBeginRateLimiter = pairBeginRateLimiter ?? new PairBeginRateLimiter(
                this.limits.MaxPairBeginPerIpPerMinute,
                this.limits.MaxPairBeginGlobalPerSecond,
                this.limits.MaxPairBeginGlobalPerMinute);

            listener = new TcpListener(listenAddress ?? IPAddress.Loopback, listenPort);
        }

        public int BoundPort => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int ActiveClientCount
        {
            get
            {
                lock (admissionGate) return activeTotalCount;
            }
        }

        public int ActivePreHelloCount
        {
            get
            {
                lock (admissionGate) return activePreHelloCount;
            }
        }

        public bool TryGetAnyClientKeepAliveOption(
            out bool enabled,
            out int? timeSeconds,
            out int? intervalSeconds,
            out int? retryCount)
        {
            enabled = false;
            timeSeconds = null;
            intervalSeconds = null;
            retryCount = null;

            foreach (var client in activeClients.Values)
            {
                try
                {
                    Socket s = client.Client;
                    object? raw = s.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
                    enabled = raw != null && Convert.ToInt32(raw) != 0;
                    try
                    {
                        object? t = s.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime);
                        if (t != null) timeSeconds = Convert.ToInt32(t);
                    }
                    catch { }
                    try
                    {
                        object? iv = s.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval);
                        if (iv != null) intervalSeconds = Convert.ToInt32(iv);
                    }
                    catch { }
                    try
                    {
                        object? rc = s.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount);
                        if (rc != null) retryCount = Convert.ToInt32(rc);
                    }
                    catch { }
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        public void Start()
        {
            lock (lifecycleGate)
            {
                if (started != 0) return;
                started = 1;
                cts = new CancellationTokenSource();
                listener.Start();
                acceptLoopTask = Task.Run(() => AcceptLoopAsync(cts.Token));
            }
        }

        public async Task StopAsync()
        {
            Task? loop;
            lock (lifecycleGate)
            {
                if (started == 0) return;
                started = 0;
                cts?.Cancel();
                try { listener.Stop(); } catch { }
                loop = acceptLoopTask;
            }

            foreach (var kv in activeClients)
            {
                try { kv.Value.Close(); } catch { }
            }

            if (loop != null)
            {
                try { await loop.ConfigureAwait(false); } catch { }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                client.NoDelay = true;
                string ipKey = NormalizeAddressKey(client.Client.RemoteEndPoint);
                string clientId = Guid.NewGuid().ToString("N");
                if (!TryAcquireConnectionLease(ipKey, clientId, client, out var lease))
                {
                    try { client.Close(); } catch { }
                    try { client.Dispose(); } catch { }
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client, lease!, ipKey, cancellationToken));
            }
        }

        private async Task HandleClientAsync(TcpClient client, ConnectionLease lease, string ipKey, CancellationToken cancellationToken)
        {
            try
            {
                using (lease)
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var transport = new TcpLineTransport(stream, limits.MaxTcpLineBytes))
                using (var session = new SandboxRpcSession(
                    world,
                    bindingStore,
                    pairingStore,
                    tokenStore,
                    displaySyncHub,
                    clock,
                    authEnforcement,
                    serverName,
                    buildRange,
                    serverVersion,
                    pairBeginRateLimiter,
                    ipKey))
                {
                    await RunSessionWithLimitsAsync(client, lease, transport, session, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private async Task RunSessionWithLimitsAsync(
            TcpClient client,
            ConnectionLease lease,
            TcpLineTransport transport,
            SandboxRpcSession session,
            CancellationToken cancellationToken)
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!session.IsHelloComplete &&
                limits.PreHelloDeadline > TimeSpan.Zero &&
                limits.PreHelloDeadline != Timeout.InfiniteTimeSpan)
            {
                deadlineCts.CancelAfter(limits.PreHelloDeadline);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                if (!session.IsHelloComplete)
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(deadlineCts.Token);
                    if (limits.PreHelloIdleTimeout > TimeSpan.Zero &&
                        limits.PreHelloIdleTimeout != Timeout.InfiniteTimeSpan)
                    {
                        idleCts.CancelAfter(limits.PreHelloIdleTimeout);
                    }
                    line = await transport.ReadLineAsync(idleCts.Token).ConfigureAwait(false);
                }
                else
                {
                    line = await transport.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }

                if (line == null) break;

                bool wasHelloComplete = session.IsHelloComplete;
                SandboxRpcStepResult step = session.ProcessLine(line);

                if (!wasHelloComplete && session.IsHelloComplete)
                {
                    PromoteLease(lease);
                    deadlineCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    TryConfigureTcpKeepAlive(client.Client);
                }

                if (step.ResponseLine != null)
                {
                    await transport.WriteLineAsync(step.ResponseLine, cancellationToken).ConfigureAwait(false);
                }

                if (step.ShouldClose)
                {
                    break;
                }
            }
        }

        private bool TryAcquireConnectionLease(string ipKey, string clientId, TcpClient client, out ConnectionLease? lease)
        {
            lock (admissionGate)
            {
                activePerIpCount.TryGetValue(ipKey, out int ipCount);
                if (activeTotalCount >= limits.MaxTotalTcpConnections ||
                    activePreHelloCount >= limits.MaxPreHelloTcpConnections ||
                    ipCount >= limits.MaxPerIpTcpConnections)
                {
                    lease = null;
                    return false;
                }

                activeTotalCount++;
                activePreHelloCount++;
                activePerIpCount[ipKey] = ipCount + 1;
                activeClients[clientId] = client;
                lease = new ConnectionLease(this, ipKey, clientId);
                return true;
            }
        }

        private void PromoteLease(ConnectionLease lease)
        {
            lock (admissionGate)
            {
                if (!lease.Promoted && !lease.Disposed)
                {
                    lease.Promoted = true;
                    activePreHelloCount--;
                }
            }
        }

        private void ReleaseLease(ConnectionLease lease)
        {
            lock (admissionGate)
            {
                if (lease.Disposed) return;
                lease.Disposed = true;
                activeTotalCount--;
                if (!lease.Promoted)
                {
                    activePreHelloCount--;
                }
                if (activePerIpCount.TryGetValue(lease.IpKey, out int count))
                {
                    if (count <= 1) activePerIpCount.Remove(lease.IpKey);
                    else activePerIpCount[lease.IpKey] = count - 1;
                }
                activeClients.TryRemove(lease.ClientId, out _);
            }
        }

        private void TryConfigureTcpKeepAlive(Socket socket)
        {
            if (!limits.EnableTcpKeepAlive) return;
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                if (limits.TcpKeepAliveTimeSeconds > 0)
                {
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, limits.TcpKeepAliveTimeSeconds);
                }
                if (limits.TcpKeepAliveIntervalSeconds > 0)
                {
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, limits.TcpKeepAliveIntervalSeconds);
                }
                if (limits.TcpKeepAliveRetryCount > 0)
                {
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, limits.TcpKeepAliveRetryCount);
                }
            }
            catch
            {
            }
        }

        private static string NormalizeAddressKey(EndPoint? remoteEndPoint)
        {
            if (remoteEndPoint is IPEndPoint ipEndPoint)
            {
                IPAddress addr = ipEndPoint.Address;
                if (addr.IsIPv4MappedToIPv6)
                {
                    addr = addr.MapToIPv4();
                }
                return addr.ToString();
            }
            return remoteEndPoint?.ToString() ?? "unknown";
        }

        public void Dispose()
        {
            try { cts?.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            foreach (var kv in activeClients)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            cts?.Dispose();
        }

        private sealed class ConnectionLease : IDisposable
        {
            private readonly TcpJsonRpcServer owner;

            public string IpKey { get; }
            public string ClientId { get; }
            public bool Promoted { get; set; }
            public bool Disposed { get; set; }

            public ConnectionLease(TcpJsonRpcServer owner, string ipKey, string clientId)
            {
                this.owner = owner;
                IpKey = ipKey;
                ClientId = clientId;
            }

            public void Dispose() => owner.ReleaseLease(this);
        }
    }
}

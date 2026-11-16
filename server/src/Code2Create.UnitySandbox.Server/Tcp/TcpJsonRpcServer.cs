using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.Server.Rpc;
using Code2Create.UnitySandbox.Server.Sync;
using Code2Create.UnitySandbox.World;
using Code2Create.UnitySandbox.World.Transport;

namespace Code2Create.UnitySandbox.Server.Tcp
{
    /// <summary>
    /// Newline-delimited UTF-8 JSON-RPC 2.0 transport over a TCP NetworkStream.
    /// </summary>
    public sealed class TcpLineTransport : IJsonRpcLineTransport, IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);

        public TcpLineTransport(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 8192, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
            reader.Dispose();
            writer.Dispose();
        }
    }

    /// <summary>
    /// TCP listener accepting McRemote-compatible newline-delimited JSON-RPC 2.0 connections (Bridge / Python / direct TCP clients).
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
        private readonly TcpListener listener;
        private readonly ConcurrentDictionary<string, TcpClient> activeClients = new ConcurrentDictionary<string, TcpClient>(StringComparer.Ordinal);
        private readonly object lifecycleGate = new object();

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
            string serverVersion = WorldConstants.DefaultServerVersion)
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

            listener = new TcpListener(listenAddress ?? IPAddress.Loopback, listenPort);
        }

        public int BoundPort => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int ActiveClientCount => activeClients.Count;

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
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            string clientId = Guid.NewGuid().ToString("N");
            activeClients[clientId] = client;

            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var transport = new TcpLineTransport(stream))
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
                    serverVersion))
                {
                    await session.RunAsync(transport, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                activeClients.TryRemove(clientId, out _);
            }
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
    }
}

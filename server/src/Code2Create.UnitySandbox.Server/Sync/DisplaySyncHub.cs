using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.Server.Sync
{
    /// <summary>
    /// Manages WebSocket display sync connections (/ws/sync), sending snapshots, box-based blockDelta,
    /// chat messages, streamUpdated notifications, and handling /mcremote pair NNN-NNN approvals.
    /// Broadcast messages intentionally omit viewerId and wireScopeId.
    /// </summary>
    public sealed class DisplaySyncHub : IDisposable
    {
        private static readonly Regex PairCodeRegex = new Regex(@"^[0-9]{3}-[0-9]{3}$", RegexOptions.Compiled);

        private readonly SandboxWorld world;
        private readonly IViewerStreamBindingStore bindingStore;
        private readonly IPairingApprovalStore pairingStore;
        private readonly ISandboxTokenStore tokenStore;
        private readonly PairingRateLimiter rateLimiter;
        private readonly Func<DateTimeOffset> clock;
        private readonly ConcurrentDictionary<string, SyncConnection> connections = new ConcurrentDictionary<string, SyncConnection>(StringComparer.Ordinal);

        public DisplaySyncHub(
            SandboxWorld world,
            IViewerStreamBindingStore bindingStore,
            IPairingApprovalStore pairingStore,
            PairingRateLimiter? rateLimiter = null,
            Func<DateTimeOffset>? clock = null,
            ISandboxTokenStore? tokenStore = null)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.bindingStore = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
            this.pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
            this.rateLimiter = rateLimiter ?? new PairingRateLimiter();
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            this.tokenStore = tokenStore ?? new InMemorySandboxTokenStore();

            this.world.BlockDeltaEmitted += OnBlockDeltaEmitted;
            this.world.ChatEmitted += OnChatEmitted;
            this.world.WorldReset += OnWorldReset;
        }

        public int ActiveConnectionCount => connections.Count;

        public async Task HandleWebSocketAsync(WebSocket socket, CancellationToken cancellationToken, string? initialViewerId = null)
        {
            var connId = Guid.NewGuid().ToString("N");
            var conn = new SyncConnection(connId, socket)
            {
                ViewerId = string.IsNullOrWhiteSpace(initialViewerId) ? null : initialViewerId.Trim()
            };
            connections[connId] = conn;

            try
            {
                if (conn.ViewerId != null)
                {
                    bindingStore.GetOrCreatePlayerUuid(conn.ViewerId);
                }
                var initialSnapshot = world.GetSnapshot();
                var initialActive = conn.ViewerId != null ? bindingStore.GetActiveStreamForViewer(conn.ViewerId) : null;
                await conn.SendAsync(BuildSnapshotJson(initialSnapshot, initialActive), cancellationToken).ConfigureAwait(false);

                var buffer = new byte[16 * 1024];
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    string? message = await ReceiveTextMessageAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
                    if (message == null) break;
                    await HandleClientMessageAsync(conn, message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                connections.TryRemove(connId, out _);
                conn.Dispose();
            }
        }

        public void NotifyStreamOriginChanged(string streamId, string dimension, Int3 origin)
        {
            bindingStore.UpdateStreamOrigin(streamId, dimension, origin);
            var active = new ActiveStreamInfo(streamId, dimension, origin);
            var payload = BuildStreamUpdatedJson(active);
            var viewers = new HashSet<string>(bindingStore.GetViewersForStream(streamId), StringComparer.Ordinal);

            foreach (var conn in connections.Values)
            {
                if (conn.ViewerId != null && viewers.Contains(conn.ViewerId))
                {
                    _ = SendSafeAsync(conn, payload);
                }
            }
        }

        private async Task HandleClientMessageAsync(SyncConnection conn, string rawJson, CancellationToken cancellationToken)
        {
            JObject obj;
            try
            {
                var parsed = JToken.Parse(rawJson);
                if (parsed is not JObject parsedObj) return;
                obj = parsedObj;
            }
            catch (JsonException)
            {
                return;
            }

            string? type = obj.Value<string>("type");
            if (string.IsNullOrEmpty(type)) return;

            string? viewerId = obj.Value<string>("viewerId");
            if (!string.IsNullOrWhiteSpace(viewerId))
            {
                conn.ViewerId = viewerId.Trim();
                bindingStore.GetOrCreatePlayerUuid(conn.ViewerId);
            }

            switch (type)
            {
                case "resync":
                {
                    var snapshot = world.GetSnapshot();
                    var active = conn.ViewerId != null ? bindingStore.GetActiveStreamForViewer(conn.ViewerId) : null;
                    string payload = BuildSnapshotJson(snapshot, active);
                    await conn.SendAsync(payload, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case "approvePair":
                {
                    string pairCode = (obj.Value<string>("pairCode") ?? string.Empty).Trim();
                    await HandleApprovePairAsync(conn, pairCode, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case "chat":
                {
                    string text = (obj.Value<string>("message") ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(text)) break;
                    if (TryExtractPairCommandCode(text, out string extractedCode))
                    {
                        await HandleApprovePairAsync(conn, extractedCode, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var active = conn.ViewerId != null ? bindingStore.GetActiveStreamForViewer(conn.ViewerId) : null;
                        string streamId = active?.StreamId ?? "viewer";
                        world.PostChat(new StreamBuildContext(streamId), text);
                    }
                    break;
                }
            }
        }

        private static bool TryExtractPairCommandCode(string text, out string pairCode)
        {
            pairCode = string.Empty;
            if (!text.StartsWith("/mcremote pair", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string remainder = text.Substring("/mcremote pair".Length).Trim();
            if (TryNormalizeAsciiPairDigits(remainder, out string normalizedFormatted))
            {
                pairCode = normalizedFormatted;
                return true;
            }
            pairCode = remainder;
            return true;
        }

        private static bool TryNormalizeAsciiPairDigits(string raw, out string formatted)
        {
            formatted = string.Empty;
            if (string.IsNullOrEmpty(raw)) return false;

            Span<char> digits = stackalloc char[6];
            int count = 0;
            foreach (char c in raw)
            {
                if (c >= '0' && c <= '9')
                {
                    if (count >= 6) return false;
                    digits[count++] = c;
                }
                else if (char.IsDigit(c))
                {
                    // Reject fullwidth or non-ASCII digits per wire-format-design_ja.md §6.5
                    return false;
                }
            }

            if (count != 6) return false;
            formatted = $"{new string(digits[..3])}-{new string(digits[3..])}";
            return true;
        }

        private async Task HandleApprovePairAsync(SyncConnection conn, string pairCode, CancellationToken cancellationToken)
        {
            DateTimeOffset now = clock();
            var limit = rateLimiter.CheckAllowed(conn.ConnectionId, conn.ViewerId, now);
            if (!limit.Allowed)
            {
                await conn.SendAsync(BuildPairResultJson(false, pairCode, "rate_limited", 0, null), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(conn.ViewerId))
            {
                int remaining = rateLimiter.RecordFailure(conn.ConnectionId, null, now);
                await conn.SendAsync(BuildPairResultJson(false, pairCode, "missing_viewer_id", remaining, null), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!PairCodeRegex.IsMatch(pairCode))
            {
                int remaining = rateLimiter.RecordFailure(conn.ConnectionId, conn.ViewerId, now);
                await conn.SendAsync(BuildPairResultJson(false, pairCode, "invalid_pair_code_format", remaining, null), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var outcome = pairingStore.TryApproveCode(pairCode, conn.ViewerId, bindingStore, tokenStore, now);
            if (!outcome.Approved || outcome.StreamId == null)
            {
                int remaining = rateLimiter.RecordFailure(conn.ConnectionId, conn.ViewerId, now);
                await conn.SendAsync(BuildPairResultJson(false, pairCode, outcome.Reason ?? "invalid_pair_code", remaining, null), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var active = bindingStore.GetActiveStreamForViewer(conn.ViewerId);

            await conn.SendAsync(BuildPairResultJson(true, pairCode, null, limit.RemainingAttempts, active), cancellationToken)
                .ConfigureAwait(false);

            if (active != null)
            {
                string streamPayload = BuildStreamUpdatedJson(active);
                foreach (var target in connections.Values)
                {
                    if (string.Equals(target.ViewerId, conn.ViewerId, StringComparison.Ordinal))
                    {
                        await target.SendAsync(streamPayload, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private void OnBlockDeltaEmitted(WorldBlockDeltaEvent ev)
        {
            string payload = new JObject
            {
                ["type"] = "blockDelta",
                ["worldEpoch"] = ev.WorldEpoch,
                ["seq"] = ev.Seq,
                ["streamId"] = ev.StreamId,
                ["bounds"] = new JObject
                {
                    ["min"] = new JArray(ev.Bounds.Min.ToArray()),
                    ["max"] = new JArray(ev.Bounds.Max.ToArray())
                },
                ["block"] = ev.Block.ToJson()
            }.ToString(Formatting.None);

            BroadcastRaw(payload);
        }

        private void OnChatEmitted(WorldChatEvent ev)
        {
            string payload = new JObject
            {
                ["type"] = "chat",
                ["worldEpoch"] = ev.WorldEpoch,
                ["seq"] = ev.Seq,
                ["streamId"] = ev.StreamId,
                ["message"] = ev.Message
            }.ToString(Formatting.None);

            BroadcastRaw(payload);
        }

        private void OnWorldReset(WorldResetEvent resetEvent)
        {
            var snapshot = world.GetSnapshot();
            foreach (var conn in connections.Values)
            {
                var active = conn.ViewerId != null ? bindingStore.GetActiveStreamForViewer(conn.ViewerId) : null;
                string payload = BuildSnapshotJson(snapshot, active);
                _ = SendSafeAsync(conn, payload);
            }
        }

        private void BroadcastRaw(string payload)
        {
            foreach (var conn in connections.Values)
            {
                _ = SendSafeAsync(conn, payload);
            }
        }

        private static async Task SendSafeAsync(SyncConnection conn, string payload)
        {
            try
            {
                await conn.SendAsync(payload, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Connection cleanup is handled by the receive loop.
            }
        }

        private static string BuildSnapshotJson(WorldSnapshot snapshot, ActiveStreamInfo? active)
        {
            var regionsArr = new JArray();
            foreach (var region in snapshot.Regions)
            {
                regionsArr.Add(new JObject
                {
                    ["bounds"] = new JObject
                    {
                        ["min"] = new JArray(region.Bounds.Min.ToArray()),
                        ["max"] = new JArray(region.Bounds.Max.ToArray())
                    },
                    ["block"] = region.Block.ToJson()
                });
            }

            return new JObject
            {
                ["type"] = "snapshot",
                ["worldEpoch"] = snapshot.WorldEpoch,
                ["seq"] = snapshot.Seq,
                ["worldConstants"] = new JObject
                {
                    ["yMin"] = WorldConstants.YMin,
                    ["yMax"] = WorldConstants.YMax,
                    ["ySea"] = WorldConstants.YSea,
                    ["yGround"] = WorldConstants.YGround,
                    ["yCloud"] = WorldConstants.YCloud,
                    ["ySteveMin"] = WorldConstants.YSteveMin,
                    ["ySteveMax"] = WorldConstants.YSteveMax
                },
                ["regions"] = regionsArr,
                ["activeStream"] = SerializeActiveStream(active)
            }.ToString(Formatting.None);
        }

        private static string BuildStreamUpdatedJson(ActiveStreamInfo active)
        {
            return new JObject
            {
                ["type"] = "streamUpdated",
                ["activeStream"] = SerializeActiveStream(active)
            }.ToString(Formatting.None);
        }

        private static string BuildPairResultJson(
            bool approved,
            string pairCode,
            string? reason,
            int remainingAttempts,
            ActiveStreamInfo? active)
        {
            return new JObject
            {
                ["type"] = "pairResult",
                ["approved"] = approved,
                ["pairCode"] = pairCode,
                ["reason"] = reason == null ? JValue.CreateNull() : new JValue(reason),
                ["remainingAttempts"] = remainingAttempts,
                ["activeStream"] = SerializeActiveStream(active)
            }.ToString(Formatting.None);
        }

        private static JToken SerializeActiveStream(ActiveStreamInfo? active)
        {
            if (active == null) return JValue.CreateNull();
            return new JObject
            {
                ["streamId"] = active.StreamId,
                ["dimension"] = active.Dimension,
                ["origin"] = new JArray(active.Origin.ToArray())
            };
        }

        private static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
        {
            using var ms = new System.IO.MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    return null;
                }

                if (result.Count > 0)
                {
                    ms.Write(buffer, 0, result.Count);
                }

                if (result.EndOfMessage)
                {
                    if (result.MessageType != WebSocketMessageType.Text) return string.Empty;
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
            }
        }

        public void Dispose()
        {
            world.BlockDeltaEmitted -= OnBlockDeltaEmitted;
            world.ChatEmitted -= OnChatEmitted;
            world.WorldReset -= OnWorldReset;
        }

        private sealed class SyncConnection : IDisposable
        {
            private readonly WebSocket socket;
            private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);

            public string ConnectionId { get; }
            public string? ViewerId { get; set; }

            public SyncConnection(string connectionId, WebSocket socket)
            {
                ConnectionId = connectionId;
                this.socket = socket;
            }

            public async Task SendAsync(string text, CancellationToken cancellationToken)
            {
                if (socket.State != WebSocketState.Open) return;
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    sendGate.Release();
                }
            }

            public void Dispose() => sendGate.Dispose();
        }
    }
}

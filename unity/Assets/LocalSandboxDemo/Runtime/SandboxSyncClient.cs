using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Code2Create.UnitySandbox.LocalDemo
{
    internal sealed class SyncBoxRegion
    {
        public Vector3Int Min;
        public Vector3Int Max;
        public string BlockId;
        public bool Snowy;
        public bool Unstable;
    }

    internal sealed class SyncActiveStream
    {
        public string StreamId;
        public string Dimension;
        public Vector3Int Origin;
    }

    internal sealed class SyncSnapshotPayload
    {
        public long WorldEpoch;
        public long Seq;
        public List<SyncBoxRegion> Regions = new List<SyncBoxRegion>();
        public SyncActiveStream ActiveStream;
    }

    internal sealed class SyncBlockDeltaPayload
    {
        public long WorldEpoch;
        public long Seq;
        public string StreamId;
        public SyncBoxRegion Region;
    }

    internal sealed class SyncChatPayload
    {
        public long WorldEpoch;
        public long Seq;
        public string StreamId;
        public string Message;
    }

    internal sealed class SyncPairResultPayload
    {
        public bool Approved;
        public string PairCode;
        public string Reason;
        public int RemainingAttempts;
        public SyncActiveStream ActiveStream;
    }

    /// <summary>
    /// Browser WebGL and Editor/Standalone WebSocket client for /ws/sync.
    /// Walking and local demos keep working seamlessly when offline or reconnecting.
    /// </summary>
    internal sealed class SandboxSyncClient : IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern string SandboxWsGetOrCreateViewerId();
        [DllImport("__Internal")] private static extern string SandboxWsGetDefaultUrl();
        [DllImport("__Internal")] private static extern int SandboxWsConnect(string url);
        [DllImport("__Internal")] private static extern int SandboxWsSendText(string text);
        [DllImport("__Internal")] private static extern int SandboxWsIsConnected();
        [DllImport("__Internal")] private static extern string SandboxWsPopEvent();
        [DllImport("__Internal")] private static extern void SandboxWsClose();
#endif

        private readonly string viewerId;
        private readonly string wsUrl;
        private readonly ConcurrentQueue<string> desktopEvents = new ConcurrentQueue<string>();

        private ClientWebSocket desktopSocket;
        private CancellationTokenSource desktopCts;
        private float nextReconnectAt;
        private bool isConnecting;

        internal bool IsConnected { get; private set; }
        internal string StatusLabel { get; private set; } = "ローカル体験中";
        internal long LastWorldEpoch { get; private set; }
        internal long LastSeq { get; private set; }

        internal event Action<SyncSnapshotPayload> SnapshotReceived;
        internal event Action<SyncBlockDeltaPayload> BlockDeltaReceived;
        internal event Action<SyncChatPayload> ChatReceived;
        internal event Action<SyncActiveStream> StreamUpdatedReceived;
        internal event Action<SyncPairResultPayload> PairResultReceived;
        internal event Action<string> PasteReceived;

        internal SandboxSyncClient(string overrideUrl = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            viewerId = SandboxWsGetOrCreateViewerId();
            string baseUrl = string.IsNullOrEmpty(overrideUrl) ? SandboxWsGetDefaultUrl() : overrideUrl;
#else
            viewerId = PlayerPrefs.GetString("mc_remote_unity_viewer_id_v1", string.Empty);
            if (string.IsNullOrEmpty(viewerId) || viewerId.Length < 16)
            {
                viewerId = "v_" + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString("mc_remote_unity_viewer_id_v1", viewerId);
                PlayerPrefs.Save();
            }
            string baseUrl = !string.IsNullOrEmpty(overrideUrl)
                ? overrideUrl
                : Environment.GetEnvironmentVariable("UNITY_SANDBOX_WS_URL");
#endif
            if (!string.IsNullOrEmpty(baseUrl))
            {
                string sep = baseUrl.Contains("?") ? "&" : "?";
                wsUrl = baseUrl + sep + "viewerId=" + Uri.EscapeDataString(viewerId);
            }
            else
            {
                wsUrl = string.Empty;
            }
        }

        internal void Start()
        {
            if (string.IsNullOrEmpty(wsUrl))
            {
                StatusLabel = "ローカル体験（同期サーバー未指定）";
                return;
            }
            TryConnect();
        }

        internal void Tick(float timeNow)
        {
            if (string.IsNullOrEmpty(wsUrl)) return;

#if UNITY_WEBGL && !UNITY_EDITOR
            while (true)
            {
                string ev = SandboxWsPopEvent();
                if (string.IsNullOrEmpty(ev)) break;
                ProcessRawEvent(ev, timeNow);
            }
#else
            while (desktopEvents.TryDequeue(out string ev))
            {
                ProcessRawEvent(ev, timeNow);
            }
#endif

            if (!IsConnected && !isConnecting && timeNow >= nextReconnectAt)
            {
                TryConnect();
            }
        }

        internal bool RequestResync()
        {
            return SendRawJson($"{{\"type\":\"resync\",\"viewerId\":\"{EscapeJson(viewerId)}\"}}");
        }

        internal bool SendApprovePair(string pairCode)
        {
            return SendRawJson($"{{\"type\":\"approvePair\",\"viewerId\":\"{EscapeJson(viewerId)}\",\"pairCode\":\"{EscapeJson(pairCode)}\"}}");
        }

        internal bool SendChat(string message)
        {
            return SendRawJson($"{{\"type\":\"chat\",\"viewerId\":\"{EscapeJson(viewerId)}\",\"message\":\"{EscapeJson(message)}\"}}");
        }

        private void TryConnect()
        {
            if (string.IsNullOrEmpty(wsUrl)) return;
            isConnecting = true;
            StatusLabel = "同期サーバーへ接続中...";

#if UNITY_WEBGL && !UNITY_EDITOR
            int ok = SandboxWsConnect(wsUrl);
            if (ok == 0)
            {
                isConnecting = false;
                IsConnected = false;
                StatusLabel = "オフライン（ローカル体験可）";
                nextReconnectAt = Time.unscaledTime + 4f;
            }
#else
            StartDesktopConnectAsync(wsUrl);
#endif
        }

        private void ProcessRawEvent(string ev, float timeNow)
        {
            if (ev == "OPEN")
            {
                isConnecting = false;
                IsConnected = true;
                StatusLabel = "オンライン（自動同期中）";
                return;
            }

            if (ev == "CLOSE" || ev == "ERR")
            {
                isConnecting = false;
                IsConnected = false;
                StatusLabel = "オフライン（自動再接続待ち）";
                nextReconnectAt = timeNow + 4f;
                return;
            }

            if (ev.StartsWith("MSG:", StringComparison.Ordinal))
            {
                string json = ev.Substring(4);
                HandleServerJson(json);
                return;
            }

            if (ev.StartsWith("PASTE:", StringComparison.Ordinal))
            {
                PasteReceived?.Invoke(ev.Substring(6));
            }
        }

        private bool SendRawJson(string payload)
        {
            if (!IsConnected) return false;
#if UNITY_WEBGL && !UNITY_EDITOR
            return SandboxWsSendText(payload) == 1;
#else
            var sock = desktopSocket;
            if (sock == null || sock.State != WebSocketState.Open) return false;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            _ = sock.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
#endif
        }

#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private async void StartDesktopConnectAsync(string targetUrl)
        {
            DisposeDesktopSocket();
            var cts = new CancellationTokenSource();
            var ws = new ClientWebSocket();
            desktopCts = cts;
            desktopSocket = ws;

            try
            {
                await ws.ConnectAsync(new Uri(targetUrl), cts.Token);
                desktopEvents.Enqueue("OPEN");
                var buffer = new byte[64 * 1024];
                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (result.Count > 0)
                    {
                        ms.Write(buffer, 0, result.Count);
                    }
                    if (result.EndOfMessage)
                    {
                        string text = Encoding.UTF8.GetString(ms.ToArray());
                        ms.SetLength(0);
                        desktopEvents.Enqueue("MSG:" + text);
                    }
                }
            }
            catch
            {
                // Reconnect loop handles retry quietly so offline local play stays smooth.
            }
            finally
            {
                desktopEvents.Enqueue("CLOSE");
            }
        }

        private void DisposeDesktopSocket()
        {
            try { desktopCts?.Cancel(); } catch { }
            try { desktopCts?.Dispose(); } catch { }
            try { desktopSocket?.Dispose(); } catch { }
            desktopCts = null;
            desktopSocket = null;
        }
#endif

        public void Dispose()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            SandboxWsClose();
#else
            DisposeDesktopSocket();
#endif
        }

        private void HandleServerJson(string json)
        {
            if (!(MiniJson.Deserialize(json) is Dictionary<string, object> root)) return;
            string type = GetString(root, "type");
            if (string.IsNullOrEmpty(type)) return;

            switch (type)
            {
                case "snapshot":
                {
                    var snap = new SyncSnapshotPayload
                    {
                        WorldEpoch = GetLong(root, "worldEpoch"),
                        Seq = GetLong(root, "seq"),
                        ActiveStream = ParseActiveStream(GetDict(root, "activeStream"))
                    };
                    if (root.TryGetValue("regions", out var regObj) && regObj is List<object> list)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i] is Dictionary<string, object> item && TryParseRegion(item, out var region))
                            {
                                snap.Regions.Add(region);
                            }
                        }
                    }
                    LastWorldEpoch = snap.WorldEpoch;
                    LastSeq = snap.Seq;
                    SnapshotReceived?.Invoke(snap);
                    break;
                }

                case "blockDelta":
                {
                    if (TryParseRegion(root, out var region))
                    {
                        var delta = new SyncBlockDeltaPayload
                        {
                            WorldEpoch = GetLong(root, "worldEpoch"),
                            Seq = GetLong(root, "seq"),
                            StreamId = GetString(root, "streamId"),
                            Region = region
                        };
                        LastWorldEpoch = delta.WorldEpoch;
                        LastSeq = delta.Seq;
                        BlockDeltaReceived?.Invoke(delta);
                    }
                    break;
                }

                case "chat":
                {
                    var chat = new SyncChatPayload
                    {
                        WorldEpoch = GetLong(root, "worldEpoch"),
                        Seq = GetLong(root, "seq"),
                        StreamId = GetString(root, "streamId"),
                        Message = GetString(root, "message")
                    };
                    LastWorldEpoch = chat.WorldEpoch;
                    LastSeq = chat.Seq;
                    ChatReceived?.Invoke(chat);
                    break;
                }

                case "streamUpdated":
                {
                    StreamUpdatedReceived?.Invoke(ParseActiveStream(GetDict(root, "activeStream")));
                    break;
                }

                case "pairResult":
                {
                    var res = new SyncPairResultPayload
                    {
                        Approved = GetBool(root, "approved"),
                        PairCode = GetString(root, "pairCode"),
                        Reason = GetString(root, "reason"),
                        RemainingAttempts = (int)GetLong(root, "remainingAttempts"),
                        ActiveStream = ParseActiveStream(GetDict(root, "activeStream"))
                    };
                    PairResultReceived?.Invoke(res);
                    break;
                }
            }
        }

        private static bool TryParseRegion(Dictionary<string, object> dict, out SyncBoxRegion region)
        {
            region = null;
            var bounds = GetDict(dict, "bounds");
            var block = GetDict(dict, "block");
            if (bounds == null || block == null) return false;
            if (!TryParseInt3(bounds, "min", out var min) || !TryParseInt3(bounds, "max", out var max)) return false;

            var state = GetDict(block, "state");
            region = new SyncBoxRegion
            {
                Min = min,
                Max = max,
                BlockId = GetString(block, "block_id"),
                Snowy = state != null && GetBool(state, "snowy"),
                Unstable = state != null && GetBool(state, "unstable")
            };
            return true;
        }

        private static SyncActiveStream ParseActiveStream(Dictionary<string, object> dict)
        {
            if (dict == null) return null;
            TryParseInt3(dict, "origin", out var origin);
            return new SyncActiveStream
            {
                StreamId = GetString(dict, "streamId"),
                Dimension = GetString(dict, "dimension"),
                Origin = origin
            };
        }

        private static bool TryParseInt3(Dictionary<string, object> parent, string key, out Vector3Int result)
        {
            result = Vector3Int.zero;
            if (!parent.TryGetValue(key, out var obj) || !(obj is List<object> arr) || arr.Count < 3) return false;
            result = new Vector3Int(Convert.ToInt32(arr[0]), Convert.ToInt32(arr[1]), Convert.ToInt32(arr[2]));
            return true;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            return d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            return d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : string.Empty;
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            return d.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0L;
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            return bool.TryParse(v.ToString(), out bool parsed) && parsed;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private sealed class MiniJson : IDisposable
        {
            private readonly StringReader json;

            private MiniJson(string text) { json = new StringReader(text); }

            public void Dispose()
            {
                json?.Dispose();
            }

            public static object Deserialize(string text)
            {
                if (string.IsNullOrEmpty(text)) return null;
                using var parser = new MiniJson(text);
                return parser.ParseValue();
            }

            private void SkipWhitespace()
            {
                while (json.Peek() != -1 && char.IsWhiteSpace((char)json.Peek())) json.Read();
            }

            private object ParseValue()
            {
                SkipWhitespace();
                int next = json.Peek();
                if (next == -1) return null;
                char c = (char)next;
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': ConsumeLiteral("true"); return true;
                    case 'f': ConsumeLiteral("false"); return false;
                    case 'n': ConsumeLiteral("null"); return null;
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>(StringComparer.Ordinal);
                json.Read(); // {
                while (true)
                {
                    SkipWhitespace();
                    if (json.Peek() == -1) return table;
                    if ((char)json.Peek() == '}') { json.Read(); return table; }
                    if ((char)json.Peek() == ',') { json.Read(); continue; }
                    string key = ParseString();
                    SkipWhitespace();
                    if (json.Peek() == ':') json.Read();
                    table[key] = ParseValue();
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                json.Read(); // [
                while (true)
                {
                    SkipWhitespace();
                    if (json.Peek() == -1) return list;
                    if ((char)json.Peek() == ']') { json.Read(); return list; }
                    if ((char)json.Peek() == ',') { json.Read(); continue; }
                    list.Add(ParseValue());
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                if (json.Peek() == '"') json.Read();
                while (true)
                {
                    int n = json.Read();
                    if (n == -1) break;
                    char c = (char)n;
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        int esc = json.Read();
                        if (esc == -1) break;
                        char e = (char)esc;
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                var hex = new char[4];
                                for (int i = 0; i < 4; i++) hex[i] = (char)json.Read();
                                sb.Append((char)Convert.ToInt32(new string(hex), 16));
                                break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            private object ParseNumber()
            {
                var sb = new StringBuilder();
                while (json.Peek() != -1)
                {
                    char c = (char)json.Peek();
                    if ("-0123456789.eE+".IndexOf(c) < 0) break;
                    sb.Append((char)json.Read());
                }
                string raw = sb.ToString();
                if (raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0)
                {
                    double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d);
                    return d;
                }
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l);
                return l;
            }

            private void ConsumeLiteral(string expected)
            {
                for (int i = 0; i < expected.Length; i++) json.Read();
            }
        }
    }

    internal static partial class SandboxJsonHelper
    {
    }
}

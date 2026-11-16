using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.Server;
using Code2Create.UnitySandbox.Server.Sync;
using Code2Create.UnitySandbox.Server.Tcp;
using Code2Create.UnitySandbox.World;
using Code2Create.UnitySandbox.World.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Code2Create.UnitySandbox.Tests;

public class TcpJsonRpcAndPairingTests
{
    private sealed class TestClock
    {
        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 11, 14, 5, 0, 0, TimeSpan.Zero);
    }

    private sealed class TcpRpcClient : IDisposable
    {
        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;

        private TcpRpcClient(TcpClient client)
        {
            this.client = client;
            var stream = client.GetStream();
            var utf8 = new UTF8Encoding(false);
            reader = new StreamReader(stream, utf8, false, 8192, leaveOpen: true);
            writer = new StreamWriter(stream, utf8, 8192, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };
        }

        public static async Task<TcpRpcClient> ConnectAsync(int port, CancellationToken ct)
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync("127.0.0.1", port, ct);
            return new TcpRpcClient(client);
        }

        public async Task<JObject> CallAsync(object id, string method, JToken paramsToken, CancellationToken ct)
        {
            var req = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JToken.FromObject(id),
                ["method"] = method,
                ["params"] = paramsToken
            };
            await writer.WriteLineAsync(req.ToString(Formatting.None).AsMemory(), ct);
            await writer.FlushAsync(ct);

            string? line = await reader.ReadLineAsync(ct);
            Assert.NotNull(line);
            return JObject.Parse(line!);
        }

        public async Task NotifyAsync(string method, JToken paramsToken, CancellationToken ct)
        {
            var req = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = paramsToken
            };
            await writer.WriteLineAsync(req.ToString(Formatting.None).AsMemory(), ct);
            await writer.FlushAsync(ct);
        }

        public async Task ExpectEofAsync(CancellationToken ct)
        {
            string? next = await reader.ReadLineAsync(ct);
            Assert.Null(next);
        }

        public void Dispose()
        {
            reader.Dispose();
            writer.Dispose();
            client.Dispose();
        }
    }

    private static async Task<JObject> ReceiveWsJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("WebSocket closed unexpectedly.");
            }
            if (result.Count > 0)
            {
                ms.Write(buffer, 0, result.Count);
            }
            if (result.EndOfMessage)
            {
                return JObject.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
    }

    private static async Task SendWsJsonAsync(ClientWebSocket ws, JObject obj, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    [Fact]
    public async Task PreHelloRejectsNonAuthMethodsWithExpectedHelloAndClosesConnection()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        foreach (var (method, paramToken) in new[]
        {
            ("catalog.get", (JToken)new JArray()),
            ("world.getBlock", (JToken)new JArray(0, 63, 0))
        })
        {
            using var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
            var resp = await rpc.CallAsync(1, method, paramToken, cts.Token);
            Assert.Equal(-32000, resp["error"]!["code"]!.Value<int>());
            Assert.Equal("expected_hello", resp["error"]!["data"]!["reason"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }
    }

    [Fact]
    public async Task HelloValidatesProtocolSemverAndTokenErrors()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // 1. Missing protocol -> protocol_required (-32602) & closes
        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var resp = await rpc.CallAsync(1, "hello", new JObject(), cts.Token);
            Assert.Equal(-32602, resp["error"]!["code"]!.Value<int>());
            Assert.Equal("protocol_required", resp["error"]!["data"]!["reason"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }

        // 2. Incompatible versions -> protocol_mismatch (-32000, data.server & data.client_requires) & closes
        foreach (string badProtocol in new[] { "22.0.0", "24.0.0", "23.4.0", "not-semver" })
        {
            using var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
            var resp = await rpc.CallAsync(1, "hello", new JObject { ["protocol"] = badProtocol }, cts.Token);
            Assert.Equal(-32000, resp["error"]!["code"]!.Value<int>());
            var data = Assert.IsType<JObject>(resp["error"]!["data"]);
            Assert.Equal("protocol_mismatch", data["reason"]!.Value<string>());
            Assert.Equal("23.3.0", data["server"]!.Value<string>());
            Assert.Equal(badProtocol, data["client_requires"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }

        // 3. Token errors: missing auth -> auth_required; unknown -> token_not_found; bad format -> token_invalid
        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var resp = await rpc.CallAsync(1, "hello", new JObject { ["protocol"] = "23.3.0" }, cts.Token);
            Assert.Equal(-32000, resp["error"]!["code"]!.Value<int>());
            Assert.Equal("auth_required", resp["error"]!["data"]!["reason"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }

        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var resp = await rpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.3.0",
                ["auth"] = new JObject { ["token"] = "mcrs_fixture_only_not_a_real_credential" }
            }, cts.Token);
            Assert.Equal(-32000, resp["error"]!["code"]!.Value<int>());
            Assert.Equal("token_not_found", resp["error"]!["data"]!["reason"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }

        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var resp = await rpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.3.0",
                ["auth"] = new JObject { ["token"] = "not a token" }
            }, cts.Token);
            Assert.Equal(-32000, resp["error"]!["code"]!.Value<int>());
            Assert.Equal("token_invalid", resp["error"]!["data"]!["reason"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }

        // 4. Compatible client minor versions (23.2.1, 23.3.0) + unknown_dimension rejection
        var tokenStore = app.Services.GetRequiredService<ISandboxTokenStore>();
        var tokenRecord = tokenStore.IssueToken("session", "viewer-test", "stream-test", Guid.NewGuid().ToString("D"), clock.UtcNow);

        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var resp = await rpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.0.0",
                ["auth"] = new JObject { ["token"] = tokenRecord.Token },
                ["build"] = new JObject { ["dimension"] = "the_nether" }
            }, cts.Token);
            Assert.Equal(-32000, resp["error"]!["code"]!.Value<int>());
            Assert.Equal("unknown_dimension", resp["error"]!["data"]!["reason"]!.Value<string>());
            Assert.Equal("minecraft:the_nether", resp["error"]!["data"]!["dimension"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }

        foreach (string compatProtocol in new[] { "23.2.1", "23.3.0" })
        {
            using var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
            var resp = await rpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = compatProtocol,
                ["auth"] = new JObject { ["token"] = tokenRecord.Token }
            }, cts.Token);
            Assert.Equal("23.3.0", resp["result"]!["protocol"]!.Value<string>());
        }

        // 5. Session token expires after 7200s
        clock.UtcNow = clock.UtcNow.AddSeconds(WorldConstants.DefaultSessionTokenTtlSeconds + 1);
        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var resp = await rpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.3.0",
                ["auth"] = new JObject { ["token"] = tokenRecord.Token }
            }, cts.Token);
            Assert.Equal(-32000, resp["error"]!["code"]!.Value<int>());
            Assert.Equal("token_expired", resp["error"]!["data"]!["reason"]!.Value<string>());
            await rpc.ExpectEofAsync(cts.Token);
        }
    }

    [Fact]
    public async Task FullScratchBridgePairingAcrossSeparateTcpConnectionsAndLiveWebSocketSync()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        string httpUrl = app.Urls.First();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        Assert.True(tcpPort > 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Verify /healthz advertises tcpPort
        using (var http = new HttpClient())
        {
            var health = JObject.Parse(await http.GetStringAsync(httpUrl + "/healthz", cts.Token));
            Assert.Equal(tcpPort, health["tcpPort"]!.Value<int>());
        }

        // Connect Unity Web viewer /ws/sync
        var wsUri = new Uri(httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-secret-xyz");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wsUri, cts.Token);
        var initialSnapshot = await ReceiveWsJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", initialSnapshot["type"]!.Value<string>());
        Assert.DoesNotContain("viewer-secret-xyz", initialSnapshot.ToString());

        // Connection 1: auth.pairBegin
        string pairingId;
        string pairCodeDigits;
        using (var beginRpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var beginResp = await beginRpc.CallAsync(1, "auth.pairBegin", new JObject
            {
                ["token_type"] = "session",
                ["client"] = new JObject
                {
                    ["name"] = "scratch-editor",
                    ["version"] = "2321.1.0",
                    ["locale"] = "ja"
                }
            }, cts.Token);
            var res = Assert.IsType<JObject>(beginResp["result"]);
            pairingId = res["pairing_id"]!.Value<string>()!;
            pairCodeDigits = res["pair_code"]!.Value<string>()!;
            Assert.Equal(6, pairCodeDigits.Length);
            Assert.True(pairCodeDigits.All(c => c >= '0' && c <= '9'));
            Assert.Equal(120, res["expires_in"]!.Value<int>());
        }

        // Connection 2 (separate TCP connection like Bridge): auth.pairPoll before approval -> immediate pending
        using (var poll1Rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var poll1 = await poll1Rpc.CallAsync(1, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, cts.Token);
            Assert.Equal("pending", poll1["result"]!["status"]!.Value<string>());
        }

        // Unity Web user types `/mcremote pair NNN-NNN` in chat
        string formattedCode = pairCodeDigits.Substring(0, 3) + "-" + pairCodeDigits.Substring(3, 3);
        await SendWsJsonAsync(ws, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-secret-xyz",
            ["message"] = "/mcremote pair " + formattedCode
        }, cts.Token);

        var pairResultMsg = await ReceiveWsJsonAsync(ws, cts.Token);
        Assert.Equal("pairResult", pairResultMsg["type"]!.Value<string>());
        Assert.True(pairResultMsg["approved"]!.Value<bool>());
        string boundStreamId = pairResultMsg["activeStream"]!["streamId"]!.Value<string>()!;
        Assert.DoesNotContain("viewer-secret-xyz", boundStreamId);

        var streamUpdatedMsg = await ReceiveWsJsonAsync(ws, cts.Token);
        Assert.Equal("streamUpdated", streamUpdatedMsg["type"]!.Value<string>());
        Assert.Equal(boundStreamId, streamUpdatedMsg["activeStream"]!["streamId"]!.Value<string>());

        // Connection 3 (another separate TCP connection): auth.pairPoll -> status: ok + token; second poll -> pair_not_found
        string token;
        using (var poll2Rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var poll2 = await poll2Rpc.CallAsync(1, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, cts.Token);
            Assert.Equal("ok", poll2["result"]!["status"]!.Value<string>());
            token = poll2["result"]!["token"]!.Value<string>()!;
            Assert.StartsWith("mcrs_", token);

            var poll3 = await poll2Rpc.CallAsync(2, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, cts.Token);
            Assert.Equal(-32000, poll3["error"]!["code"]!.Value<int>());
            Assert.Equal("pair_not_found", poll3["error"]!["data"]!["reason"]!.Value<string>());
        }

        // Connection 4: hello with issued token (build omitted -> default origin [0, 0, 0])
        string playerUuid;
        string catalogHashFromHello;
        using (var sessionRpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var helloResp = await sessionRpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.3.0",
                ["client"] = new JObject { ["name"] = "scratch-editor", ["version"] = "2321.1.0", ["locale"] = "ja" },
                ["auth"] = new JObject { ["token"] = token }
            }, cts.Token);

            var hello = Assert.IsType<JObject>(helloResp["result"]);
            Assert.Equal("23.3.0", hello["protocol"]!.Value<string>());
            Assert.Equal("1.21.11", hello["mc_version"]!.Value<string>());
            Assert.Equal(new[] { "1.21.11" }, hello["supported_mc_versions"]!.ToObject<string[]>());
            Assert.Equal("mc-remote-unity-sandbox", hello["server"]?["name"]?.Value<string>());
            Assert.Equal(WorldConstants.DefaultServerVersion, hello["server"]?["version"]?.Value<string>());
            Assert.Equal("minecraft:overworld", hello["dimension"]!.Value<string>());
            Assert.Equal(new[] { 0, 0, 0 }, hello["origin"]!.ToObject<int[]>());
            Assert.Equal(62, hello["world_constants"]!["y_sea"]!.Value<int>());
            Assert.True(hello["permissions"]!["online"]!.Value<bool>());
            Assert.True(hello["permissions"]!["offline"]!.Value<bool>());
            Assert.Equal(1000, hello["permissions"]!["buildRange"]!.Value<int>());

            playerUuid = hello["player"]!.Value<string>()!;
            Assert.True(Guid.TryParse(playerUuid, out _));
            Assert.NotEqual("viewer-secret-xyz", playerUuid);
            catalogHashFromHello = hello["catalogHash"]!.Value<string>()!;
            Assert.Equal(64, catalogHashFromHello.Length);

            var helloStreamUpdated = await ReceiveWsJsonAsync(ws, cts.Token);
            Assert.Equal("streamUpdated", helloStreamUpdated["type"]!.Value<string>());

            // catalog.get
            var catalogResp = await sessionRpc.CallAsync(2, "catalog.get", new JArray(), cts.Token);
            var catalog = Assert.IsType<JObject>(catalogResp["result"]);
            Assert.Equal(catalogHashFromHello, catalog["catalogHash"]!.Value<string>());
            var blocks = Assert.IsType<JObject>(catalog["block"]);
            Assert.Equal(10, blocks.Count);
            Assert.Empty(Assert.IsType<JObject>(catalog["entity"]));
            Assert.Empty(Assert.IsType<JObject>(catalog["particle"]));
            Assert.False(blocks["minecraft:grass_block"]!["default_state"]!["snowy"]!.Value<bool>());
            Assert.Equal(new[] { false, true }, blocks["minecraft:tnt"]!["states"]!["unstable"]!.ToObject<bool[]>());

            // Verify catalogHash matches canonical SHA-256 of {block, entity, particle}
            var bodyOnly = new JObject
            {
                ["block"] = blocks,
                ["entity"] = catalog["entity"],
                ["particle"] = catalog["particle"]
            };
            string canonicalBody = SandboxBlockRegistry.ToSortedCanonicalJson(bodyOnly);
            using (var sha = SHA256.Create())
            {
                string expectedHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalBody))).ToLowerInvariant();
                Assert.Equal(expectedHash, catalogHashFromHello);
            }

            // events.poll (empty) & rejected inputs per events-v23.json
            var pollDefault = await sessionRpc.CallAsync(3, "events.poll", new JArray(0), cts.Token);
            Assert.Empty((JArray)pollDefault["result"]!["events"]!);
            Assert.Equal(0, pollDefault["result"]!["through_sequence"]!.Value<int>());
            Assert.Equal(0, pollDefault["result"]!["latest_sequence"]!.Value<int>());

            var pollBounded = await sessionRpc.CallAsync(4, "events.poll", new JArray(0, new JObject { ["max_events"] = 10 }), cts.Token);
            Assert.Empty((JArray)pollBounded["result"]!["events"]!);

            foreach (var badEventsParams in new JArray[]
            {
                new JArray(1),
                new JArray(0, 8),
                new JArray(0, new JObject()),
                new JArray(0, new JObject { ["limit"] = 8 }),
                new JArray(0, new JObject { ["max_events"] = 0 }),
                new JArray(0, new JObject { ["max_events"] = 8, ["filter"] = new JObject() })
            })
            {
                var badPoll = await sessionRpc.CallAsync(5, "events.poll", badEventsParams, cts.Token);
                Assert.Equal(-32602, badPoll["error"]!["code"]!.Value<int>());
                Assert.Equal("invalid_params", badPoll["error"]!["data"]!["reason"]!.Value<string>());
            }

            // build.setOrigin -> updates context and notifies Unity Web
            var setOriginResp = await sessionRpc.CallAsync(6, "build.setOrigin", new JArray(100, 0, -100), cts.Token);
            Assert.Equal("minecraft:overworld", setOriginResp["result"]!["dimension"]!.Value<string>());
            Assert.Equal(new[] { 100, 0, -100 }, setOriginResp["result"]!["origin"]!.ToObject<int[]>());

            var originWsMsg = await ReceiveWsJsonAsync(ws, cts.Token);
            Assert.Equal("streamUpdated", originWsMsg["type"]!.Value<string>());
            Assert.Equal(new[] { 100, 0, -100 }, originWsMsg["activeStream"]!["origin"]!.ToObject<int[]>());

            // build.setDimension
            var setDimOk = await sessionRpc.CallAsync(7, "build.setDimension", new JArray("overworld"), cts.Token);
            Assert.Equal("minecraft:overworld", setDimOk["result"]!["dimension"]!.Value<string>());
            var dimWsMsg = await ReceiveWsJsonAsync(ws, cts.Token);
            Assert.Equal("streamUpdated", dimWsMsg["type"]!.Value<string>());
            Assert.Equal("minecraft:overworld", dimWsMsg["activeStream"]!["dimension"]!.Value<string>());

            var setDimNether = await sessionRpc.CallAsync(8, "build.setDimension", new JArray("the_nether"), cts.Token);
            Assert.Equal(-32000, setDimNether["error"]!["code"]!.Value<int>());
            Assert.Equal("unknown_dimension", setDimNether["error"]!["data"]!["reason"]!.Value<string>());
            Assert.Equal("minecraft:the_nether", setDimNether["error"]!["data"]!["dimension"]!.Value<string>());

            var setDimInvalid = await sessionRpc.CallAsync(9, "build.setDimension", new JArray("Overworld"), cts.Token);
            Assert.Equal(-32602, setDimInvalid["error"]!["code"]!.Value<int>());
            Assert.Equal("invalid_params", setDimInvalid["error"]!["data"]!["reason"]!.Value<string>());

            // FAST notifications + connection.flush barrier -> reflected on /ws/sync & readable via getBlock
            await sessionRpc.NotifyAsync("world.setBlock", new JArray(0, 63, 0, new JObject
            {
                ["block_id"] = "gold_block",
                ["state"] = new JObject()
            }), cts.Token);
            await sessionRpc.NotifyAsync("world.setBlocks", new JArray(1, 63, 0, 2, 64, 1, new JObject
            {
                ["block_id"] = "minecraft:sea_lantern",
                ["state"] = new JObject()
            }), cts.Token);
            var flushResp = await sessionRpc.CallAsync(10, "connection.flush", new JArray(), cts.Token);
            Assert.Equal(10, flushResp["id"]!.Value<int>());
            Assert.Equal(JTokenType.Null, flushResp["result"]!.Type);

            var delta1 = await ReceiveWsJsonAsync(ws, cts.Token);
            Assert.Equal("blockDelta", delta1["type"]!.Value<string>());
            Assert.Equal(boundStreamId, delta1["streamId"]!.Value<string>());
            Assert.Equal(new[] { 100, 63, -100 }, delta1["bounds"]!["min"]!.ToObject<int[]>());
            Assert.Equal("minecraft:gold_block", delta1["block"]!["block_id"]!.Value<string>());
            Assert.DoesNotContain("viewer-secret-xyz", delta1.ToString());

            var delta2 = await ReceiveWsJsonAsync(ws, cts.Token);
            Assert.Equal("blockDelta", delta2["type"]!.Value<string>());
            Assert.Equal(new[] { 101, 63, -100 }, delta2["bounds"]!["min"]!.ToObject<int[]>());
            Assert.Equal(new[] { 102, 64, -99 }, delta2["bounds"]!["max"]!.ToObject<int[]>());
            Assert.Equal("minecraft:sea_lantern", delta2["block"]!["block_id"]!.Value<string>());

            var getBlockResp = await sessionRpc.CallAsync(11, "world.getBlock", new JArray(2, 64, 1), cts.Token);
            Assert.Equal("minecraft:sea_lantern", getBlockResp["result"]!["block_id"]!.Value<string>());

            // out_of_bounds check relative to [100, 0, -100] -> abs x=200
            var oobResp = await sessionRpc.CallAsync(12, "world.setBlock", new JArray(100, 63, 0, new JObject
            {
                ["block_id"] = "stone",
                ["state"] = new JObject()
            }), cts.Token);
            Assert.Equal(-32000, oobResp["error"]!["code"]!.Value<int>());
            Assert.Equal("out_of_bounds", oobResp["error"]!["data"]!["reason"]!.Value<string>());
            Assert.Equal(400.0, oobResp["error"]?["data"]?["bounds"]?["world_border"]?["size"]?.Value<double>());
            var oobViolating = Assert.IsType<JObject>(oobResp["error"]?["data"]?["violating"]);
            Assert.Equal(200, oobViolating["x"]!.Value<int>());
            Assert.Equal(63, oobViolating["y"]!.Value<int>());
            Assert.Equal(-100, oobViolating["z"]!.Value<int>());

            // block_count_limit_exceeded (> 32,768 blocks) check relative to [100, 0, -100]
            var limitResp = await sessionRpc.CallAsync(120, "world.setBlocks", new JArray(0, 63, 0, 32, 94, 31, new JObject
            {
                ["block_id"] = "stone",
                ["state"] = new JObject()
            }), cts.Token);
            Assert.Equal(-32000, limitResp["error"]!["code"]!.Value<int>());
            var limitData = Assert.IsType<JObject>(limitResp["error"]!["data"]);
            Assert.Equal("block_count_limit_exceeded", limitData["reason"]!.Value<string>());
            Assert.Equal(32_768, limitData["limit"]!.Value<int>());
            Assert.Equal(33_792L, limitData["requested"]!.Value<long>());
            Assert.Null(limitData["violating"]);

            // chat.post request + FAST notification
            var chatResp = await sessionRpc.CallAsync(13, "chat.post", new JArray("Scratchからビルド開始"), cts.Token);
            Assert.Equal(JTokenType.Null, chatResp["result"]!.Type);
            var chatWsMsg = await ReceiveWsJsonAsync(ws, cts.Token);
            Assert.Equal("chat", chatWsMsg["type"]!.Value<string>());
            Assert.Equal("Scratchからビルド開始", chatWsMsg["message"]!.Value<string>());

            // Unsupported methods -> method_not_found (-32601)
            foreach (string unsupported in new[] { "world.getSign", "entity.getPose", "hello", "auth.pairBegin" })
            {
                var notFound = await sessionRpc.CallAsync(14, unsupported, new JArray(), cts.Token);
                Assert.Equal(-32601, notFound["error"]!["code"]!.Value<int>());
                Assert.Equal("method_not_found", notFound["error"]!["data"]!["reason"]!.Value<string>());
            }
        }

        // World reset does NOT clear tokens or viewer binding
        var world = app.Services.GetRequiredService<SandboxWorld>();
        world.Reset();
        var resetWsSnapshot = await ReceiveWsJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", resetWsSnapshot["type"]!.Value<string>());
        Assert.Equal(2, resetWsSnapshot["worldEpoch"]!.Value<long>());
        Assert.Equal(boundStreamId, resetWsSnapshot["activeStream"]!["streamId"]!.Value<string>());

        using (var reconnectRpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var reHello = await reconnectRpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.3.0",
                ["auth"] = new JObject { ["token"] = token }
            }, cts.Token);
            Assert.Equal(playerUuid, reHello["result"]!["player"]!.Value<string>());
            Assert.Equal(new[] { 0, 0, 0 }, reHello["result"]!["origin"]!.ToObject<int[]>());
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task PairBeginValidationAnd120SecondExpirationMatchWireSpec()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);

        // Reject legacy "player" and unknown token_type
        var legacyType = await rpc.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "player" }, cts.Token);
        Assert.Equal(-32602, legacyType["error"]!["code"]!.Value<int>());
        Assert.Equal("invalid_params", legacyType["error"]!["data"]!["reason"]!.Value<string>());

        // Reject overly long device name (> 64 codepoints)
        var longDevice = await rpc.CallAsync(2, "auth.pairBegin", new JObject
        {
            ["token_type"] = "long_lived",
            ["device"] = new string('a', 65)
        }, cts.Token);
        Assert.Equal(-32602, longDevice["error"]!["code"]!.Value<int>());
        Assert.Equal("invalid_params", longDevice["error"]!["data"]!["reason"]!.Value<string>());

        // Valid session pairing expires after 120s
        var begun = await rpc.CallAsync(3, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
        string pairingId = begun["result"]!["pairing_id"]!.Value<string>()!;

        clock.UtcNow = clock.UtcNow.AddSeconds(120);
        var expiredPoll = await rpc.CallAsync(4, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, cts.Token);
        Assert.Equal(-32000, expiredPoll["error"]!["code"]!.Value<int>());
        Assert.Equal("pair_expired", expiredPoll["error"]!["data"]!["reason"]!.Value<string>());
    }

    [Fact]
    public async Task ServerRestartClearsTokensAndWorldStateRejectingPreviousTokenWithTokenNotFound()
    {
        var clock = new TestClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string tokenBeforeRestart;

        // First server process lifecycle: pair, authenticate, and place a gold block at (0, 63, 0).
        await using (var app1 = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        }))
        {
            await app1.StartAsync();
            string httpUrl1 = app1.Urls.First();
            int tcpPort1 = app1.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;

            var wsUri1 = new Uri(httpUrl1.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-restart-check");
            using var ws1 = new ClientWebSocket();
            await ws1.ConnectAsync(wsUri1, cts.Token);
            _ = await ReceiveWsJsonAsync(ws1, cts.Token);

            using var beginRpc = await TcpRpcClient.ConnectAsync(tcpPort1, cts.Token);
            var beginResp = await beginRpc.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
            string pairingId = beginResp["result"]!["pairing_id"]!.Value<string>()!;
            string digits = beginResp["result"]!["pair_code"]!.Value<string>()!;

            await SendWsJsonAsync(ws1, new JObject
            {
                ["type"] = "chat",
                ["viewerId"] = "viewer-restart-check",
                ["message"] = "/mcremote pair " + digits
            }, cts.Token);
            var pairResult = await ReceiveWsJsonAsync(ws1, cts.Token);
            Assert.True(pairResult["approved"]!.Value<bool>());

            using var pollRpc = await TcpRpcClient.ConnectAsync(tcpPort1, cts.Token);
            var pollResp = await pollRpc.CallAsync(1, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, cts.Token);
            tokenBeforeRestart = pollResp["result"]!["token"]!.Value<string>()!;
            Assert.StartsWith("mcrs_", tokenBeforeRestart);

            using var session1 = await TcpRpcClient.ConnectAsync(tcpPort1, cts.Token);
            var hello1 = await session1.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.3.0",
                ["auth"] = new JObject { ["token"] = tokenBeforeRestart }
            }, cts.Token);
            Assert.Null(hello1["error"]);

            var setBlock1 = await session1.CallAsync(
                2,
                "world.setBlock",
                new JArray(0, 63, 0, new JObject { ["block_id"] = "minecraft:gold_block", ["state"] = new JObject() }),
                cts.Token);
            Assert.Equal(JTokenType.Null, setBlock1["result"]!.Type);

            await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "restart", cts.Token);
        }

        // Second server process lifecycle (fresh in-memory stores):
        // Pre-restart token is rejected with token_not_found, and placed blocks have reset to initial air/terrain.
        await using var app2 = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });
        await app2.StartAsync();
        string httpUrl2 = app2.Urls.First();
        int tcpPort2 = app2.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;

        using (var staleTokenRpc = await TcpRpcClient.ConnectAsync(tcpPort2, cts.Token))
        {
            var rejectedHello = await staleTokenRpc.CallAsync(1, "hello", new JObject
            {
                ["protocol"] = "23.3.0",
                ["auth"] = new JObject { ["token"] = tokenBeforeRestart }
            }, cts.Token);
            Assert.Equal(-32000, rejectedHello["error"]!["code"]!.Value<int>());
            Assert.Equal("token_not_found", rejectedHello["error"]!["data"]!["reason"]!.Value<string>());
            await staleTokenRpc.ExpectEofAsync(cts.Token);
        }

        var wsUri2 = new Uri(httpUrl2.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-restart-check");
        using var ws2 = new ClientWebSocket();
        await ws2.ConnectAsync(wsUri2, cts.Token);
        var snapshot2 = await ReceiveWsJsonAsync(ws2, cts.Token);
        Assert.Equal("snapshot", snapshot2["type"]!.Value<string>());
        Assert.Empty((JArray)snapshot2["regions"]!);
        Assert.True(snapshot2["activeStream"] == null || snapshot2["activeStream"]!.Type == JTokenType.Null);

        using var beginRpc2 = await TcpRpcClient.ConnectAsync(tcpPort2, cts.Token);
        var beginResp2 = await beginRpc2.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
        string pairingId2 = beginResp2["result"]!["pairing_id"]!.Value<string>()!;
        string digits2 = beginResp2["result"]!["pair_code"]!.Value<string>()!;

        await SendWsJsonAsync(ws2, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-restart-check",
            ["message"] = "/mcremote pair " + digits2
        }, cts.Token);
        _ = await ReceiveWsJsonAsync(ws2, cts.Token);

        using var pollRpc2 = await TcpRpcClient.ConnectAsync(tcpPort2, cts.Token);
        string tokenAfterRestart = (await pollRpc2.CallAsync(1, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId2 }, cts.Token))["result"]!["token"]!.Value<string>()!;

        using var session2 = await TcpRpcClient.ConnectAsync(tcpPort2, cts.Token);
        var hello2 = await session2.CallAsync(1, "hello", new JObject
        {
            ["protocol"] = "23.3.0",
            ["auth"] = new JObject { ["token"] = tokenAfterRestart }
        }, cts.Token);
        Assert.Null(hello2["error"]);

        var getBlockAfterRestart = await session2.CallAsync(2, "world.getBlock", new JArray(0, 63, 0), cts.Token);
        Assert.Equal("minecraft:air", getBlockAfterRestart["result"]!["block_id"]!.Value<string>());

        await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task PlayerMethodsAndWorldGetBlocksHeightSyncWithPairedWebSocketAndOfflineState()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        string httpUrl = app.Urls.First();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        string wsUrlPrimary = httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-player-01";
        string wsUrlOther = httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-other-02";

        using var wsPrimary = new ClientWebSocket();
        await wsPrimary.ConnectAsync(new Uri(wsUrlPrimary), cts.Token);
        _ = await ReceiveWsJsonAsync(wsPrimary, cts.Token); // snapshot

        using var wsOther = new ClientWebSocket();
        await wsOther.ConnectAsync(new Uri(wsUrlOther), cts.Token);
        _ = await ReceiveWsJsonAsync(wsOther, cts.Token); // snapshot

        string token = await PairViewerAndGetTokenAsync(tcpPort, wsPrimary, "viewer-player-01", "session", cts.Token);

        using var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        var helloResp = await rpc.CallAsync(1, "hello", new JObject
        {
            ["protocol"] = "23.3.0",
            ["auth"] = new JObject { ["token"] = token },
            ["build"] = new JObject { ["origin"] = new JArray(100, 0, 100) }
        }, cts.Token);
        Assert.Null(helloResp["error"]);
        Assert.Equal("streamUpdated", (await ReceiveWsJsonAsync(wsPrimary, cts.Token))["type"]!.Value<string>());

        // Viewer reports live walking pose over /ws/sync -> reflected via player.getPos / getPose / getDirection
        await SendWsJsonAsync(wsPrimary, new JObject
        {
            ["type"] = "playerPose",
            ["viewerId"] = "viewer-player-01",
            ["poseSeq"] = 0,
            ["pos"] = new JArray(102.5, 64.25, 105.75),
            ["yaw"] = -90.0,
            ["pitch"] = 12.5
        }, cts.Token);
        await Task.Delay(30, cts.Token);

        var getPosResp = await rpc.CallAsync(2, "player.getPos", new JArray(), cts.Token);
        Assert.Equal("minecraft:overworld", getPosResp["result"]!["dimension"]!.Value<string>());
        Assert.Equal(new[] { 2.5, 64.25, 5.75 }, getPosResp["result"]!["pos"]!.ToObject<double[]>());

        var getPoseResp = await rpc.CallAsync(3, "player.getPose", new JArray(), cts.Token);
        Assert.Equal(-90.0, getPoseResp["result"]!["yaw"]!.Value<double>());
        Assert.Equal(12.5, getPoseResp["result"]!["pitch"]!.Value<double>());

        var getDirResp = await rpc.CallAsync(4, "player.getDirection", new JArray(), cts.Token);
        var dirArr = Assert.IsType<JArray>(getDirResp["result"]);
        Assert.Equal(3, dirArr.Count);
        Assert.True(dirArr[0].Value<double>() > 0.95); // -90 yaw faces +X East

        // player.setPose -> sends playerPose ONLY to wsPrimary (without viewerId) and canonicalizes yaw 180 -> -180
        var setPoseResp = await rpc.CallAsync(5, "player.setPose",
            new JArray("overworld", 10.1235, 70.0, -4.5, 540.0, -45.0), cts.Token);
        Assert.Equal(new[] { 10.124, 70.0, -4.5 }, setPoseResp["result"]!["pos"]!.ToObject<double[]>());
        Assert.Equal(-180, setPoseResp["result"]!["yaw"]!.Value<int>());
        Assert.Equal(-45, setPoseResp["result"]!["pitch"]!.Value<int>());

        var wsPoseMsg = await ReceiveWsJsonAsync(wsPrimary, cts.Token);
        Assert.Equal("playerPose", wsPoseMsg["type"]!.Value<string>());
        Assert.True(wsPoseMsg["hasPos"]!.Value<bool>());
        Assert.True(wsPoseMsg["hasRotation"]!.Value<bool>());
        Assert.Equal(new[] { 110.124, 70.0, 95.5 }, wsPoseMsg["pos"]!.ToObject<double[]>());
        Assert.Equal(-180, wsPoseMsg["yaw"]!.Value<int>());
        Assert.DoesNotContain("viewer-player-01", wsPoseMsg.ToString());
        long currentPoseSeq = wsPoseMsg["poseSeq"]!.Value<long>();
        Assert.True(currentPoseSeq >= 1);

        // Stale pre-teleport client playerPose with lower poseSeq must NOT overwrite server pose
        await SendWsJsonAsync(wsPrimary, new JObject
        {
            ["type"] = "playerPose",
            ["viewerId"] = "viewer-player-01",
            ["poseSeq"] = 0,
            ["pos"] = new JArray(100.0, 63.04, 86.0),
            ["yaw"] = 0.0,
            ["pitch"] = 8.0
        }, cts.Token);
        await Task.Delay(30, cts.Token);
        var afterStaleGetPose = await rpc.CallAsync(6, "player.getPose", new JArray(), cts.Token);
        Assert.Equal(new[] { 10.124, 70.0, -4.5 }, afterStaleGetPose["result"]!["pos"]!.ToObject<double[]>());

        // player.setDirection -> updates rotation only (hasPos: false), handles zero_direction
        var setDirResp = await rpc.CallAsync(7, "player.setDirection", new JArray(0, 0, 10), cts.Token);
        Assert.Equal("[0,0,1]", setDirResp["result"]!.ToString(Formatting.None));
        var wsLookMsg = await ReceiveWsJsonAsync(wsPrimary, cts.Token);
        Assert.Equal("playerPose", wsLookMsg["type"]!.Value<string>());
        Assert.False(wsLookMsg["hasPos"]!.Value<bool>());
        Assert.True(wsLookMsg["hasRotation"]!.Value<bool>());
        Assert.Equal(0, wsLookMsg["yaw"]!.Value<int>());

        var zeroDirErr = await rpc.CallAsync(8, "player.setDirection", new JArray(0, 0, 0), cts.Token);
        Assert.Equal(-32602, zeroDirErr["error"]!["code"]!.Value<int>());
        Assert.Equal("zero_direction", zeroDirErr["error"]!["data"]!["reason"]!.Value<string>());

        // Error checks: out_of_bounds ([-200,200), [-64,320]), unknown_dimension, pitch > 90
        foreach (double[] badRel in new[]
        {
            new[] { 100.0, 64.0, 0.0 },   // abs x = 200.0 (exclusive max)
            new[] { -300.1, 64.0, 0.0 },  // abs x = -200.1
            new[] { 0.0, 64.0, 100.0 },   // abs z = 200.0
            new[] { 0.0, -64.1, 0.0 },    // abs y = -64.1
            new[] { 0.0, 320.1, 0.0 }     // abs y = 320.1
        })
        {
            var oobSetPos = await rpc.CallAsync(9, "player.setPos",
                new JArray("minecraft:overworld", badRel[0], badRel[1], badRel[2]), cts.Token);
            Assert.Equal(-32000, oobSetPos["error"]!["code"]!.Value<int>());
            Assert.Equal("out_of_bounds", oobSetPos["error"]!["data"]!["reason"]!.Value<string>());
        }

        // Valid upper Y=320 succeeds for player
        var maxHeightPlayer = await rpc.CallAsync(10, "player.setPos", new JArray("overworld", 0, 320, 0), cts.Token);
        Assert.Equal(new[] { 0, 320, 0 }, maxHeightPlayer["result"]!["pos"]!.ToObject<int[]>());
        _ = await ReceiveWsJsonAsync(wsPrimary, cts.Token);

        var badPitch = await rpc.CallAsync(11, "player.setPose", new JArray("overworld", 0, 64, 0, 0, 90.5), cts.Token);
        Assert.Equal(-32602, badPitch["error"]!["code"]!.Value<int>());
        Assert.Equal("invalid_params", badPitch["error"]!["data"]!["reason"]!.Value<string>());

        var unknownDim = await rpc.CallAsync(12, "player.setPos", new JArray("the_nether", 0, 64, 0), cts.Token);
        Assert.Equal(-32000, unknownDim["error"]!["code"]!.Value<int>());
        Assert.Equal("unknown_dimension", unknownDim["error"]!["data"]!["reason"]!.Value<string>());

        // world.getHeight and world.getBlocks over TCP
        await rpc.CallAsync(13, "world.setBlock", new JArray(1, 75, 1, new JObject { ["block_id"] = "gold_block", ["state"] = new JObject() }), cts.Token);
        _ = await ReceiveWsJsonAsync(wsPrimary, cts.Token); // blockDelta
        var getHeightResp = await rpc.CallAsync(14, "world.getHeight", new JArray(1, 1), cts.Token);
        Assert.Equal(75, getHeightResp["result"]!.Value<int>());
        var getBlocksResp = await rpc.CallAsync(15, "world.getBlocks", new JArray(1, 75, 1, 1, 76, 1), cts.Token);
        var blocksArr = Assert.IsType<JArray>(getBlocksResp["result"]);
        Assert.Equal(2, blocksArr.Count);
        Assert.Equal("minecraft:gold_block", blocksArr[0]["block_id"]!.Value<string>());
        Assert.Equal("minecraft:air", blocksArr[1]["block_id"]!.Value<string>());

        // Ensure unrelated wsOther received blockDelta but NEVER received wsPrimary's playerPose or streamUpdated
        var otherMsg = await ReceiveWsJsonAsync(wsOther, cts.Token);
        Assert.Equal("blockDelta", otherMsg["type"]!.Value<string>());

        // Closing wsPrimary causes player_offline (-32000) while preserving token & binding; reconnect restores online + pose sync
        await wsPrimary.CloseAsync(WebSocketCloseStatus.NormalClosure, "tab closed", cts.Token);
        await Task.Delay(50, cts.Token);

        foreach (var (m, p) in new (string, JArray)[]
        {
            ("player.getPos", new JArray()),
            ("player.setPos", new JArray("overworld", 0, 64, 0)),
            ("player.getPose", new JArray()),
            ("player.setPose", new JArray("overworld", 0, 64, 0, 0, 0)),
            ("player.getDirection", new JArray()),
            ("player.setDirection", new JArray(0, 0, 1))
        })
        {
            var offlineErr = await rpc.CallAsync(16, m, p, cts.Token);
            Assert.Equal(-32000, offlineErr["error"]!["code"]!.Value<int>());
            Assert.Equal("player_offline", offlineErr["error"]!["data"]!["reason"]!.Value<string>());
        }

        // Reconnect same viewerId -> snapshot then persisted playerPose arrive, and player.getPos succeeds again
        using var wsReconnected = new ClientWebSocket();
        await wsReconnected.ConnectAsync(new Uri(wsUrlPrimary), cts.Token);
        var reconnSnap = await ReceiveWsJsonAsync(wsReconnected, cts.Token);
        Assert.Equal("snapshot", reconnSnap["type"]!.Value<string>());
        Assert.NotEqual(JTokenType.Null, reconnSnap["activeStream"]!.Type);
        var reconnPose = await ReceiveWsJsonAsync(wsReconnected, cts.Token);
        Assert.Equal("playerPose", reconnPose["type"]!.Value<string>());
        Assert.Equal(new[] { 100, 320, 100 }, reconnPose["pos"]!.ToObject<int[]>());

        var onlineAgain = await rpc.CallAsync(17, "player.getPos", new JArray(), cts.Token);
        Assert.Equal(new[] { 0, 320, 0 }, onlineAgain["result"]!["pos"]!.ToObject<int[]>());

        await wsOther.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        await wsReconnected.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task EventsPollCapturesOnlyPlainChatFromPairedViewerWithImmutableOriginSnapshotAndSessionEpoch()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        string httpUrl = app.Urls.First();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var wsPaired = new ClientWebSocket();
        await wsPaired.ConnectAsync(new Uri(httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-chat-01"), cts.Token);
        _ = await ReceiveWsJsonAsync(wsPaired, cts.Token); // snapshot

        using var wsStranger = new ClientWebSocket();
        await wsStranger.ConnectAsync(new Uri(httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-chat-02"), cts.Token);
        _ = await ReceiveWsJsonAsync(wsStranger, cts.Token); // snapshot

        string token = await PairViewerAndGetTokenAsync(tcpPort, wsPaired, "viewer-chat-01", "long_lived", cts.Token);

        using var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        var helloResp = await rpc.CallAsync(1, "hello", new JObject
        {
            ["protocol"] = "23.3.0",
            ["auth"] = new JObject { ["token"] = token },
            ["build"] = new JObject { ["origin"] = new JArray(200, 0, 200) }
        }, cts.Token);
        Assert.Null(helloResp["error"]);

        // Programmatic chat.post, stranger viewer chat, and slash commands must NOT become chat_posted events
        await rpc.CallAsync(2, "chat.post", new JArray("from-python"), cts.Token);
        await SendWsJsonAsync(wsStranger, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-chat-02",
            ["message"] = "stranger-hello"
        }, cts.Token);
        await SendWsJsonAsync(wsPaired, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-chat-01",
            ["message"] = "/help"
        }, cts.Token);
        await Task.Delay(30, cts.Token);

        var emptyPoll = await rpc.CallAsync(3, "events.poll", new JArray(0), cts.Token);
        Assert.Empty((JArray)emptyPoll["result"]!["events"]!);
        Assert.Equal(0L, emptyPoll["result"]!["latest_sequence"]!.Value<long>());

        // Plain human chat from paired viewer captures exact chat-event-compat-v23.2.1 shape with session origin
        await SendWsJsonAsync(wsPaired, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-chat-01",
            ["message"] = "hello"
        }, cts.Token);
        await Task.Delay(30, cts.Token);

        // Changing origin AFTER capture must not mutate existing event #1
        await rpc.CallAsync(4, "build.setOrigin", new JArray(100, 0, -100), cts.Token);

        await SendWsJsonAsync(wsPaired, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-chat-01",
            ["message"] = "second"
        }, cts.Token);
        await Task.Delay(30, cts.Token);

        var pollAll = await rpc.CallAsync(5, "events.poll", new JArray(0), cts.Token);
        var res = Assert.IsType<JObject>(pollAll["result"]);
        var evs = Assert.IsType<JArray>(res["events"]);
        Assert.Equal(2, evs.Count);

        var ev1 = Assert.IsType<JObject>(evs[0]);
        Assert.Equal(1L, ev1["sequence"]!.Value<long>());
        Assert.Equal("chat_posted", ev1["type"]!.Value<string>());
        Assert.Equal("minecraft:overworld", ev1["dimension"]!.Value<string>());
        Assert.Equal(new[] { 200, 0, 200 }, ev1["origin"]!.ToObject<int[]>());
        Assert.Equal("hello", ev1["message"]!.Value<string>());

        var ev2 = Assert.IsType<JObject>(evs[1]);
        Assert.Equal(2L, ev2["sequence"]!.Value<long>());
        Assert.Equal(new[] { 100, 0, -100 }, ev2["origin"]!.ToObject<int[]>());
        Assert.Equal("second", ev2["message"]!.Value<string>());

        Assert.Equal(2L, res["through_sequence"]!.Value<long>());
        Assert.Equal(2L, res["latest_sequence"]!.Value<long>());
        Assert.Equal(0L, res["filtered_out"]!.Value<long>());
        Assert.Equal(0L, res["overflow_dropped_total"]!.Value<long>());
        Assert.Equal(0L, res["capacity_dropped_total"]!.Value<long>());
        Assert.Equal(0L, res["explicitly_discarded_total"]!.Value<long>());

        // New TCP connection epoch has empty ring starting at sequence 0 (no reconnect replay)
        using var rpc2 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        await rpc2.CallAsync(1, "hello", new JObject
        {
            ["protocol"] = "23.3.0",
            ["auth"] = new JObject { ["token"] = token }
        }, cts.Token);
        var freshPoll = await rpc2.CallAsync(2, "events.poll", new JArray(0), cts.Token);
        Assert.Empty((JArray)freshPoll["result"]!["events"]!);
        Assert.Equal(0L, freshPoll["result"]!["latest_sequence"]!.Value<long>());

        await wsPaired.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        await wsStranger.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    private static async Task<string> PairViewerAndGetTokenAsync(
        int tcpPort,
        ClientWebSocket viewerWs,
        string viewerId,
        string tokenType,
        CancellationToken ct)
    {
        using var pairBeginRpc = await TcpRpcClient.ConnectAsync(tcpPort, ct);
        var beginResp = await pairBeginRpc.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = tokenType }, ct);
        string pairingId = beginResp["result"]!["pairing_id"]!.Value<string>()!;
        string pairCode = beginResp["result"]!["pair_code"]!.Value<string>()!;

        await SendWsJsonAsync(viewerWs, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = viewerId,
            ["message"] = "/mcremote pair " + pairCode
        }, ct);

        var pairResult = await ReceiveWsJsonAsync(viewerWs, ct);
        Assert.Equal("pairResult", pairResult["type"]!.Value<string>());
        Assert.True(pairResult["approved"]!.Value<bool>());
        var streamUpdated = await ReceiveWsJsonAsync(viewerWs, ct);
        Assert.Equal("streamUpdated", streamUpdated["type"]!.Value<string>());

        using var pairPollRpc = await TcpRpcClient.ConnectAsync(tcpPort, ct);
        var pollResp = await pairPollRpc.CallAsync(1, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, ct);
        return pollResp["result"]!["token"]!.Value<string>()!;
    }
}

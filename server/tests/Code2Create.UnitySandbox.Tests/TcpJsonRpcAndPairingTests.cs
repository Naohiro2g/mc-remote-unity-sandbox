using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

        public static async Task<TcpRpcClient> ConnectAsync(int port, CancellationToken ct, string? localIp = null)
        {
            TcpClient client = string.IsNullOrEmpty(localIp)
                ? new TcpClient()
                : new TcpClient(new IPEndPoint(IPAddress.Parse(localIp), 0));
            client.NoDelay = true;
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

        public async Task<string?> TryCallRawAsync(object id, string method, JToken paramsToken, CancellationToken ct)
        {
            var req = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JToken.FromObject(id),
                ["method"] = method,
                ["params"] = paramsToken
            };
            try
            {
                await writer.WriteLineAsync(req.ToString(Formatting.None).AsMemory(), ct);
                await writer.FlushAsync(ct);
                return await reader.ReadLineAsync(ct);
            }
            catch (IOException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        public async Task SendRawBytesAndExpectCloseWithoutResponseAsync(byte[] rawBytes, CancellationToken ct)
        {
            var stream = client.GetStream();
            try
            {
                await stream.WriteAsync(rawBytes, 0, rawBytes.Length, ct);
                await stream.FlushAsync(ct);
            }
            catch (IOException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            byte[] probe = new byte[128];
            try
            {
                int n = await stream.ReadAsync(probe, 0, probe.Length, ct);
                Assert.Equal(0, n);
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
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
            try
            {
                string? next = await reader.ReadLineAsync(ct);
                Assert.Null(next);
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
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

        // Verify /healthz advertises version, protocol, and tcpPort
        using (var http = new HttpClient())
        {
            var health = JObject.Parse(await http.GetStringAsync(httpUrl + "/healthz", cts.Token));
            Assert.Equal("2321.2.0", health["version"]!.Value<string>());
            Assert.Equal("23.3.0", health["protocol"]!.Value<string>());
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

    [Fact]
    public async Task CardinalYawAndDirectionVectorsMatchMcRemoteSpec()
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

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-cardinal-01"), cts.Token);
        _ = await ReceiveWsJsonAsync(ws, cts.Token); // snapshot

        string token = await PairViewerAndGetTokenAsync(tcpPort, ws, "viewer-cardinal-01", "session", cts.Token);
        using var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        var helloResp = await rpc.CallAsync(1, "hello", new JObject
        {
            ["protocol"] = WorldConstants.ProtocolVersion,
            ["auth"] = new JObject { ["token"] = token }
        }, cts.Token);
        Assert.Null(helloResp["error"]);
        Assert.Equal("streamUpdated", (await ReceiveWsJsonAsync(ws, cts.Token))["type"]!.Value<string>());

        // Unsynced initial server pose defaults to North-facing spawn (-180 deg yaw -> [0, 0, -1])
        var initPose = await rpc.CallAsync(2, "player.getPose", new JArray(), cts.Token);
        Assert.Equal(-180.0, initPose["result"]!["yaw"]!.Value<double>());
        Assert.Equal(8.0, initPose["result"]!["pitch"]!.Value<double>());
        Assert.Equal(new[] { 100.5, 63.04, 114.5 }, initPose["result"]!["pos"]!.ToObject<double[]>());

        var cardinal = new (double yaw, double pitch, double[] dir)[]
        {
            (-180.0, 0.0, new[] { 0.0, 0.0, -1.0 }), // North
            (-90.0,  0.0, new[] { 1.0, 0.0, 0.0 }),  // East
            (0.0,    0.0, new[] { 0.0, 0.0, 1.0 }),  // South
            (90.0,   0.0, new[] { -1.0, 0.0, 0.0 })  // West
        };

        int nextId = 10;
        foreach (var c in cardinal)
        {
            var poseResp = await rpc.CallAsync(nextId++, "player.setPose",
                new JArray("overworld", 0.5, 64.0, 0.5, c.yaw, c.pitch), cts.Token);
            Assert.Equal(c.yaw, poseResp["result"]!["yaw"]!.Value<double>());
            Assert.Equal(c.pitch, poseResp["result"]!["pitch"]!.Value<double>());
            _ = await ReceiveWsJsonAsync(ws, cts.Token);

            var dirResp = await rpc.CallAsync(nextId++, "player.getDirection", new JArray(), cts.Token);
            var actualDir = dirResp["result"]!.ToObject<double[]>()!;
            Assert.Equal(3, actualDir.Length);
            Assert.True(Math.Abs(actualDir[0] - c.dir[0]) < 1e-6);
            Assert.True(Math.Abs(actualDir[1] - c.dir[1]) < 1e-6);
            Assert.True(Math.Abs(actualDir[2] - c.dir[2]) < 1e-6);

            var setDirResp = await rpc.CallAsync(nextId++, "player.setDirection",
                new JArray(c.dir[0], c.dir[1], c.dir[2]), cts.Token);
            _ = await ReceiveWsJsonAsync(ws, cts.Token);
            var getPoseAfterDir = await rpc.CallAsync(nextId++, "player.getPose", new JArray(), cts.Token);
            Assert.Equal(c.yaw, getPoseAfterDir["result"]!["yaw"]!.Value<double>());
            var setDirArr = setDirResp["result"]!.ToObject<double[]>()!;
            Assert.True(Math.Abs(setDirArr[0] - c.dir[0]) < 1e-6);
            Assert.True(Math.Abs(setDirArr[1] - c.dir[1]) < 1e-6);
            Assert.True(Math.Abs(setDirArr[2] - c.dir[2]) < 1e-6);
        }

        // Yaw 180 canonicalizes to -180 (North)
        var northAlias = await rpc.CallAsync(nextId++, "player.setPose", new JArray("overworld", 0.5, 64.0, 0.5, 180.0, 0.0), cts.Token);
        Assert.Equal(-180.0, northAlias["result"]!["yaw"]!.Value<double>());
        _ = await ReceiveWsJsonAsync(ws, cts.Token);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task CrossHostWebSocketIpAndTcpLocalhostPairingSucceeds()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        string httpUrl = app.Urls.First(); // http://127.0.0.1:<port>
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Unity Web connects using explicit IPv4 loopback URL
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-cross-host"), cts.Token);
        _ = await ReceiveWsJsonAsync(ws, cts.Token);

        // Bridge/RPC client resolves "localhost" separately over TCP
        using var beginClient = new TcpClient { NoDelay = true };
        await beginClient.ConnectAsync("localhost", tcpPort, cts.Token);
        string pairingId;
        string digits;
        using (var beginStream = beginClient.GetStream())
        using (var reader = new StreamReader(beginStream, new UTF8Encoding(false), false, 8192, true))
        using (var writer = new StreamWriter(beginStream, new UTF8Encoding(false), 8192, true) { NewLine = "\n", AutoFlush = true })
        {
            await writer.WriteLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "auth.pairBegin",
                ["params"] = new JObject { ["token_type"] = "session" }
            }.ToString(Formatting.None));
            var beginResp = JObject.Parse((await reader.ReadLineAsync(cts.Token))!);
            pairingId = beginResp["result"]!["pairing_id"]!.Value<string>()!;
            digits = beginResp["result"]!["pair_code"]!.Value<string>()!;
        }

        await SendWsJsonAsync(ws, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-cross-host",
            ["message"] = "/mcremote pair " + digits
        }, cts.Token);
        var pairResult = await ReceiveWsJsonAsync(ws, cts.Token);
        Assert.True(pairResult["approved"]!.Value<bool>());
        _ = await ReceiveWsJsonAsync(ws, cts.Token); // streamUpdated

        using var pollClient = new TcpClient { NoDelay = true };
        await pollClient.ConnectAsync("localhost", tcpPort, cts.Token);
        using (var pollStream = pollClient.GetStream())
        using (var reader = new StreamReader(pollStream, new UTF8Encoding(false), false, 8192, true))
        using (var writer = new StreamWriter(pollStream, new UTF8Encoding(false), 8192, true) { NewLine = "\n", AutoFlush = true })
        {
            await writer.WriteLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "auth.pairPoll",
                ["params"] = new JObject { ["pairing_id"] = pairingId }
            }.ToString(Formatting.None));
            var pollResp = JObject.Parse((await reader.ReadLineAsync(cts.Token))!);
            Assert.Equal("ok", pollResp["result"]!["status"]!.Value<string>());
            string issuedToken = pollResp["result"]!["token"]!.Value<string>()!;

            await writer.WriteLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "hello",
                ["params"] = new JObject
                {
                    ["protocol"] = WorldConstants.ProtocolVersion,
                    ["auth"] = new JObject { ["token"] = issuedToken }
                }
            }.ToString(Formatting.None));
            var helloResp = JObject.Parse((await reader.ReadLineAsync(cts.Token))!);
            Assert.Null(helloResp["error"]);
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
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

    [Fact]
    public void DefaultConnectionLimitsMatchApprovedProductionValues()
    {
        var limits = new SandboxConnectionLimits();
        Assert.Equal(64, limits.MaxTotalTcpConnections);
        Assert.Equal(32, limits.MaxPreHelloTcpConnections);
        Assert.Equal(32, limits.MaxPerIpTcpConnections);
        Assert.Equal(TimeSpan.FromSeconds(30), limits.PreHelloIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(180), limits.PreHelloDeadline);
        Assert.True(limits.EnableTcpKeepAlive);
        Assert.Equal(60, limits.TcpKeepAliveTimeSeconds);
        Assert.Equal(30, limits.TcpKeepAliveIntervalSeconds);
        Assert.Equal(8, limits.TcpKeepAliveRetryCount);
        Assert.Equal(20, limits.MaxPairBeginPerIpPerMinute);
        Assert.Equal(20, limits.MaxPairBeginGlobalPerSecond);
        Assert.Equal(120, limits.MaxPairBeginGlobalPerMinute);
        Assert.Equal(128, limits.MaxPendingPairCodes);
        Assert.Equal(TimeSpan.FromSeconds(60), limits.ExpiredPairRetentionGrace);
        Assert.Equal(256, limits.MaxTotalPairEntries);
        Assert.Equal(65_536, limits.MaxTcpLineBytes);
        Assert.Equal(65_536, limits.MaxWebSocketMessageBytes);
    }

    [Fact]
    public async Task TcpMaxTotalConnectionsClosesExcessConnectionAndRecoversSlotOnDisconnect()
    {
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Limits = new SandboxConnectionLimits
            {
                MaxTotalTcpConnections = 3,
                MaxPreHelloTcpConnections = 32,
                MaxPerIpTcpConnections = 32
            }
        });

        await app.StartAsync();
        var tcpServer = app.Services.GetRequiredService<TcpJsonRpcServer>();
        int tcpPort = tcpServer.BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var held = new List<TcpRpcClient>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var c = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
                var resp = await c.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
                Assert.NotNull(resp["result"]);
                held.Add(c);
            }
            Assert.Equal(3, tcpServer.ActiveClientCount);

            using (var excess = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
            {
                string? closed = await excess.TryCallRawAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
                Assert.Null(closed);
            }

            held[0].Dispose();
            held.RemoveAt(0);
            for (int w = 0; w < 80 && tcpServer.ActiveClientCount >= 3; w++)
            {
                await Task.Delay(25, cts.Token);
            }
            Assert.True(tcpServer.ActiveClientCount < 3);

            using var recovered = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
            var recResp = await recovered.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
            Assert.NotNull(recResp["result"]);
        }
        finally
        {
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public async Task TcpMaxPreHelloConnectionsClosesExcessPreHelloWhileAllowingPromotionToPostHello()
    {
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            AuthEnforcement = false,
            Limits = new SandboxConnectionLimits
            {
                MaxTotalTcpConnections = 4,
                MaxPreHelloTcpConnections = 2,
                MaxPerIpTcpConnections = 4
            }
        });

        await app.StartAsync();
        var tcpServer = app.Services.GetRequiredService<TcpJsonRpcServer>();
        int tcpPort = tcpServer.BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var c1 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        Assert.NotNull((await c1.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);

        using var c2 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        Assert.NotNull((await c2.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
        Assert.Equal(2, tcpServer.ActivePreHelloCount);

        using (var c3 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            string? closed = await c3.TryCallRawAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
            Assert.Null(closed);
        }

        // Completing hello promotes c1 out of pre-hello quota
        var helloResp = await c1.CallAsync(2, "hello", new JObject { ["protocol"] = WorldConstants.ProtocolVersion }, cts.Token);
        Assert.Null(helloResp["error"]);
        Assert.Equal(1, tcpServer.ActivePreHelloCount);
        Assert.Equal(2, tcpServer.ActiveClientCount);

        using var c4 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        Assert.NotNull((await c4.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
    }

    [Fact]
    public async Task TcpMaxPerIpConnectionsClosesExcessFromSameIpWhileAllowingOtherIp()
    {
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Limits = new SandboxConnectionLimits
            {
                MaxTotalTcpConnections = 4,
                MaxPreHelloTcpConnections = 4,
                MaxPerIpTcpConnections = 2
            }
        });

        await app.StartAsync();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var c1 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.1");
        Assert.NotNull((await c1.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);

        using var c2 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.1");
        Assert.NotNull((await c2.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);

        // Third connection from 127.0.0.1 exceeds MaxPerIpTcpConnections = 2
        using (var c3 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.1"))
        {
            string? closed = await c3.TryCallRawAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
            Assert.Null(closed);
        }

        // Connection from distinct source loopback 127.0.0.2 still succeeds
        using var cOtherIp = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.2");
        Assert.NotNull((await cOtherIp.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
    }

    [Fact]
    public async Task PreHelloIdleTimeoutClosesSilentConnectionWhilePostHelloStaysOpenAndEnablesKeepAlive()
    {
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            AuthEnforcement = false,
            Limits = new SandboxConnectionLimits
            {
                PreHelloIdleTimeout = TimeSpan.FromMilliseconds(200),
                PreHelloDeadline = TimeSpan.FromSeconds(10),
                EnableTcpKeepAlive = true,
                TcpKeepAliveTimeSeconds = 60,
                TcpKeepAliveIntervalSeconds = 30,
                TcpKeepAliveRetryCount = 8
            }
        });

        await app.StartAsync();
        var tcpServer = app.Services.GetRequiredService<TcpJsonRpcServer>();
        int tcpPort = tcpServer.BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using (var silent = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            await silent.ExpectEofAsync(cts.Token);
        }

        using var authed = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        var hello = await authed.CallAsync(1, "hello", new JObject { ["protocol"] = WorldConstants.ProtocolVersion }, cts.Token);
        Assert.Null(hello["error"]);

        Assert.True(tcpServer.TryGetAnyClientKeepAliveOption(out bool kaEnabled, out int? kaTime, out int? kaInterval, out int? kaRetry));
        Assert.True(kaEnabled);
        if (kaTime.HasValue) Assert.Equal(60, kaTime.Value);
        if (kaInterval.HasValue) Assert.Equal(30, kaInterval.Value);
        if (kaRetry.HasValue) Assert.Equal(8, kaRetry.Value);

        await Task.Delay(450, cts.Token);
        var flush = await authed.CallAsync(2, "connection.flush", new JArray(), cts.Token);
        Assert.Null(flush["error"]);
        Assert.Equal(JTokenType.Null, flush["result"]!.Type);
    }

    [Fact]
    public async Task PreHelloDeadlineClosesConnectionEvenWhenPollingContinues()
    {
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Limits = new SandboxConnectionLimits
            {
                PreHelloIdleTimeout = TimeSpan.FromMilliseconds(400),
                PreHelloDeadline = TimeSpan.FromMilliseconds(550)
            }
        });

        await app.StartAsync();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        string pairingId;
        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var begun = await rpc.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
            pairingId = begun["result"]!["pairing_id"]!.Value<string>()!;

            bool closedDuringPolling = false;
            int pendingCount = 0;
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(120, cts.Token);
                string? raw = await rpc.TryCallRawAsync(10 + i, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, cts.Token);
                if (raw == null)
                {
                    closedDuringPolling = true;
                    break;
                }
                var obj = JObject.Parse(raw);
                Assert.Equal("pending", obj["result"]!["status"]!.Value<string>());
                pendingCount++;
            }

            Assert.True(pendingCount >= 1);
            Assert.True(closedDuringPolling);
        }

        // Pending pairing survives pre-hello TCP deadline and continues on a new connection
        using var freshRpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token);
        var pollAfterReconnect = await freshRpc.CallAsync(1, "auth.pairPoll", new JObject { ["pairing_id"] = pairingId }, cts.Token);
        Assert.Equal("pending", pollAfterReconnect["result"]!["status"]!.Value<string>());
    }

    [Fact]
    public async Task PairBeginRateLimitsPerIpAndGlobalCloseConnectionWithoutErrorReason()
    {
        // Part A: Per-IP rate limit (3 / 60s) with distinct remote loopback and window recovery
        var clockA = new TestClock();
        await using (var appA = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clockA.UtcNow,
            Limits = new SandboxConnectionLimits
            {
                MaxPairBeginPerIpPerMinute = 3,
                MaxPairBeginGlobalPerSecond = 20,
                MaxPairBeginGlobalPerMinute = 20
            }
        }))
        {
            await appA.StartAsync();
            int tcpPort = appA.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            using (var rpcIp1 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.1"))
            {
                for (int i = 0; i < 3; i++)
                {
                    var ok = await rpcIp1.CallAsync(1 + i, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
                    Assert.NotNull(ok["result"]);
                }

                string? closed = await rpcIp1.TryCallRawAsync(10, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
                Assert.Null(closed);
            }

            // Different source IP is unaffected by 127.0.0.1 per-IP cap
            using (var rpcIp2 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.2"))
            {
                var ok = await rpcIp2.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
                Assert.NotNull(ok["result"]);
            }

            // Advancing clock past 60s recovers per-IP quota
            clockA.UtcNow = clockA.UtcNow.AddSeconds(61);
            using (var rpcIp1Recovered = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.1"))
            {
                var ok = await rpcIp1Recovered.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
                Assert.NotNull(ok["result"]);
            }
        }

        // Part B: Global per-second (2/s) and global per-minute (4/min) limits
        var clockB = new TestClock();
        await using (var appB = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clockB.UtcNow,
            Limits = new SandboxConnectionLimits
            {
                MaxPairBeginPerIpPerMinute = 20,
                MaxPairBeginGlobalPerSecond = 2,
                MaxPairBeginGlobalPerMinute = 4
            }
        }))
        {
            await appB.StartAsync();
            int tcpPort = appB.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            using (var r1 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.1"))
            {
                Assert.NotNull((await r1.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            }
            using (var r2 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.2"))
            {
                Assert.NotNull((await r2.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            }
            // 3rd call across another IP in same second hits global per-second cap (2/s)
            using (var r3 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.3"))
            {
                Assert.Null(await r3.TryCallRawAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token));
            }

            clockB.UtcNow = clockB.UtcNow.AddSeconds(2);
            using (var r4 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.3"))
            {
                Assert.NotNull((await r4.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
                Assert.NotNull((await r4.CallAsync(2, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            }

            // After 4 calls within 1 minute, another +2s still hits global per-minute cap (4/min)
            clockB.UtcNow = clockB.UtcNow.AddSeconds(2);
            using (var r5 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token, "127.0.0.4"))
            {
                Assert.Null(await r5.TryCallRawAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token));
            }
        }
    }

    [Fact]
    public async Task MaxPendingPairCodesClosesExcessPairBeginUntilExpiredOrApproved()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow,
            Limits = new SandboxConnectionLimits
            {
                MaxPendingPairCodes = 3,
                MaxTotalPairEntries = 50,
                MaxPairBeginPerIpPerMinute = 50,
                MaxPairBeginGlobalPerSecond = 50,
                MaxPairBeginGlobalPerMinute = 50
            }
        });

        await app.StartAsync();
        string httpUrl = app.Urls.First();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        var pairingStore = Assert.IsType<InMemoryPairingApprovalStore>(app.Services.GetRequiredService<IPairingApprovalStore>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        string firstCode;
        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            firstCode = (await rpc.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]!["pair_code"]!.Value<string>()!;
            Assert.NotNull((await rpc.CallAsync(2, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            Assert.NotNull((await rpc.CallAsync(3, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            Assert.Equal(3, pairingStore.PendingPairCodeCount);

            // 4th pending pairBegin exceeds MaxPendingPairCodes = 3 -> closes without JSON response
            Assert.Null(await rpc.TryCallRawAsync(4, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token));
        }

        // Approving one code via WebSocket frees a pending slot
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-cap-01"), cts.Token);
        _ = await ReceiveWsJsonAsync(ws, cts.Token);

        await SendWsJsonAsync(ws, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-cap-01",
            ["message"] = "/mcremote pair " + firstCode
        }, cts.Token);
        Assert.True((await ReceiveWsJsonAsync(ws, cts.Token))["approved"]!.Value<bool>());
        _ = await ReceiveWsJsonAsync(ws, cts.Token); // streamUpdated
        Assert.Equal(2, pairingStore.PendingPairCodeCount);

        using (var rpc2 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            Assert.NotNull((await rpc2.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            Assert.Equal(3, pairingStore.PendingPairCodeCount);
            Assert.Null(await rpc2.TryCallRawAsync(2, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token));
        }

        // Advancing clock past 120s purges expired codes
        clock.UtcNow = clock.UtcNow.AddSeconds(120);
        using (var rpc3 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            Assert.NotNull((await rpc3.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            Assert.Equal(1, pairingStore.PendingPairCodeCount);
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task UnpolledPairRecordsArePurgedAfterGraceAndBoundedByMaxTotalEntriesWhilePreservingPairExpired()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            Clock = () => clock.UtcNow,
            Limits = new SandboxConnectionLimits
            {
                MaxPendingPairCodes = 128,
                ExpiredPairRetentionGrace = TimeSpan.FromSeconds(60),
                MaxTotalPairEntries = 4,
                MaxPairBeginPerIpPerMinute = 50,
                MaxPairBeginGlobalPerSecond = 50,
                MaxPairBeginGlobalPerMinute = 50
            }
        });

        await app.StartAsync();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        var pairingStore = Assert.IsType<InMemoryPairingApprovalStore>(app.Services.GetRequiredService<IPairingApprovalStore>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var initialIds = new List<string>();
        using (var rpc = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            for (int i = 0; i < 4; i++)
            {
                var r = await rpc.CallAsync(1 + i, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
                initialIds.Add(r["result"]!["pairing_id"]!.Value<string>()!);
            }
            Assert.Equal(4, pairingStore.TrackedEntryCount);

            // Hard entry cap prevents creating a 5th active entry even below MaxPendingPairCodes (128)
            Assert.Null(await rpc.TryCallRawAsync(10, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token));
            Assert.Equal(4, pairingStore.TrackedEntryCount);
        }

        // At 120s (within +60s grace): polling returns pair_expired; creating new entries evicts oldest expired-in-grace to stay <= 4
        clock.UtcNow = clock.UtcNow.AddSeconds(120);
        using (var rpc2 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var p0Resp = await rpc2.CallAsync(1, "auth.pairPoll", new JObject { ["pairing_id"] = initialIds[0] }, cts.Token);
            Assert.Equal(-32000, p0Resp["error"]!["code"]!.Value<int>());
            Assert.Equal("pair_expired", p0Resp["error"]!["data"]!["reason"]!.Value<string>());
            Assert.Equal(3, pairingStore.TrackedEntryCount); // initialIds[1], [2], [3] remain unpolled

            // Creates p5 (count 4), then p6 evicts oldest expired initialIds[1] (count stays 4)
            Assert.NotNull((await rpc2.CallAsync(2, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            Assert.NotNull((await rpc2.CallAsync(3, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token))["result"]);
            Assert.Equal(4, pairingStore.TrackedEntryCount);

            var evictedPoll = await rpc2.CallAsync(4, "auth.pairPoll", new JObject { ["pairing_id"] = initialIds[1] }, cts.Token);
            Assert.Equal("pair_not_found", evictedPoll["error"]!["data"]!["reason"]!.Value<string>());
            Assert.Equal(4, pairingStore.TrackedEntryCount); // initialIds[2], [3] (t=0, exp 120), p5, p6 (t=120, exp 240)
        }

        // Advance +61s (total t = 181s): initialIds[2], [3] passed 120s + 60s grace and are auto-purged when touched
        clock.UtcNow = clock.UtcNow.AddSeconds(61);
        using (var rpc3 = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            var triggerNew = await rpc3.CallAsync(1, "auth.pairBegin", new JObject { ["token_type"] = "session" }, cts.Token);
            Assert.NotNull(triggerNew["result"]);
            Assert.Equal(3, pairingStore.TrackedEntryCount); // Only p5, p6, and new trigger remain

            var purgedPoll = await rpc3.CallAsync(2, "auth.pairPoll", new JObject { ["pairing_id"] = initialIds[2] }, cts.Token);
            Assert.Equal("pair_not_found", purgedPoll["error"]!["data"]!["reason"]!.Value<string>());
        }
    }

    [Fact]
    public async Task TcpMaxLineBytesClosesConnectionPreAndPostHelloWhileAcceptingBoundaryLine()
    {
        const int limit = 1024;
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            TcpListenPort = 0,
            AuthEnforcement = false,
            Limits = new SandboxConnectionLimits
            {
                MaxTcpLineBytes = limit
            }
        });

        await app.StartAsync();
        int tcpPort = app.Services.GetRequiredService<TcpJsonRpcServer>().BoundPort;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        static byte[] BuildExactLengthJsonLine(string method, JToken paramsToken, int exactLineBytes, string lineEnding)
        {
            var obj = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = method,
                ["params"] = paramsToken,
                ["pad"] = string.Empty
            };
            int overhead = Encoding.UTF8.GetByteCount(obj.ToString(Formatting.None));
            int padLen = exactLineBytes - overhead;
            Assert.True(padLen >= 0);
            obj["pad"] = new string('p', padLen);
            string json = obj.ToString(Formatting.None);
            Assert.Equal(exactLineBytes, Encoding.UTF8.GetByteCount(json));
            return Encoding.UTF8.GetBytes(json + lineEnding);
        }

        // Pre-hello: exact 1024 bytes (+ CRLF) succeeds; 1025 bytes without LF closes connection immediately
        {
            byte[] line = BuildExactLengthJsonLine("auth.pairBegin", new JObject { ["token_type"] = "session" }, limit, "\r\n");
            using var s = new TcpClient { NoDelay = true };
            await s.ConnectAsync("127.0.0.1", tcpPort, cts.Token);
            var stream = s.GetStream();
            await stream.WriteAsync(line, 0, line.Length, cts.Token);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var resp = JObject.Parse((await reader.ReadLineAsync(cts.Token))!);
            Assert.NotNull(resp["result"]!["pairing_id"]);
        }

        using (var preOversized = await TcpRpcClient.ConnectAsync(tcpPort, cts.Token))
        {
            byte[] oversized = Encoding.UTF8.GetBytes(new string('a', limit + 1));
            await preOversized.SendRawBytesAndExpectCloseWithoutResponseAsync(oversized, cts.Token);
        }

        // Post-hello: exact 1024 bytes succeeds; 1025 bytes without LF closes connection immediately
        using (var postClient = new TcpClient { NoDelay = true })
        {
            await postClient.ConnectAsync("127.0.0.1", tcpPort, cts.Token);
            var ns = postClient.GetStream();
            using var writer = new StreamWriter(ns, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(ns, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            await writer.WriteLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "hello",
                ["params"] = new JObject { ["protocol"] = WorldConstants.ProtocolVersion }
            }.ToString(Formatting.None));
            Assert.Null(JObject.Parse((await reader.ReadLineAsync(cts.Token))!)["error"]);

            byte[] exactPost = BuildExactLengthJsonLine("chat.post", new JArray("hello exact"), limit, "\n");
            await ns.WriteAsync(exactPost, 0, exactPost.Length, cts.Token);
            var postOk = JObject.Parse((await reader.ReadLineAsync(cts.Token))!);
            Assert.Null(postOk["error"]);

            byte[] oversizedPost = Encoding.UTF8.GetBytes(new string('z', limit + 1));
            await ns.WriteAsync(oversizedPost, 0, oversizedPost.Length, cts.Token);
            byte[] probe = new byte[64];
            try
            {
                int n = await ns.ReadAsync(probe, 0, probe.Length, cts.Token);
                Assert.Equal(0, n);
            }
            catch (IOException)
            {
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.Server;
using Code2Create.UnitySandbox.Server.Sync;
using Code2Create.UnitySandbox.World;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Code2Create.UnitySandbox.Tests;

public class DisplaySyncServerTests
{
    private sealed class TestClock
    {
        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 11, 14, 2, 0, 0, TimeSpan.Zero);
    }

    private static async Task<JObject> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new System.IO.MemoryStream();
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
                string text = Encoding.UTF8.GetString(ms.ToArray());
                return JObject.Parse(text);
            }
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, JObject obj, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    [Fact]
    public async Task WebSocketSyncStreamsDeltasPairsViewerPreservesBindingOnResetAndNeverLeaksViewerId()
    {
        var clock = new TestClock();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            Clock = () => clock.UtcNow
        });

        await app.StartAsync();
        string httpUrl = app.Urls.First();
        var wsUri = new Uri(httpUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/sync?viewerId=viewer-secret-01");

        var world = app.Services.GetRequiredService<SandboxWorld>();
        var pairingStore = app.Services.GetRequiredService<IPairingApprovalStore>();
        var bindingStore = app.Services.GetRequiredService<IViewerStreamBindingStore>();
        var hub = app.Services.GetRequiredService<DisplaySyncHub>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wsUri, cts.Token);

        // 1. Initial snapshot arrives automatically on connect
        var snapshotMsg = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", snapshotMsg["type"]!.Value<string>());
        Assert.Equal(1, snapshotMsg["worldEpoch"]!.Value<long>());
        Assert.True(snapshotMsg["activeStream"]!.Type == JTokenType.Null);
        Assert.DoesNotContain("wireScopeId", snapshotMsg.Properties().Select(p => p.Name));
        Assert.DoesNotContain("viewer-secret-01", snapshotMsg.ToString());

        // 2. Register a pairing code and approve via /mcremote pair chat message
        pairingStore.RegisterPendingCode("482-915", "stream-42", clock.UtcNow.AddSeconds(120));
        await SendJsonAsync(ws, new JObject
        {
            ["type"] = "chat",
            ["viewerId"] = "viewer-secret-01",
            ["message"] = "/mcremote pair 482-915"
        }, cts.Token);

        var pairResult = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("pairResult", pairResult["type"]!.Value<string>());
        Assert.True(pairResult["approved"]!.Value<bool>());
        Assert.Equal("stream-42", pairResult["activeStream"]!["streamId"]!.Value<string>());

        var streamUpdated = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("streamUpdated", streamUpdated["type"]!.Value<string>());
        Assert.Equal("stream-42", streamUpdated["activeStream"]!["streamId"]!.Value<string>());

        // Separate player UUID created and distinct from viewerId
        string playerUuid = bindingStore.GetOrCreatePlayerUuid("viewer-secret-01");
        Assert.True(Guid.TryParse(playerUuid, out _));
        Assert.NotEqual("viewer-secret-01", playerUuid);

        // 3. Place a box and post chat on stream-42 -> arrives as box blockDelta + chat without viewerId/wireScopeId
        var streamCtx = bindingStore.GetOrCreateStreamContext("stream-42");
        hub.NotifyStreamOriginChanged("stream-42", WorldConstants.DefaultDimension, new Int3(100, 63, 100));
        var originUpdated = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("streamUpdated", originUpdated["type"]!.Value<string>());
        Assert.Equal(new[] { 100, 63, 100 }, originUpdated["activeStream"]!["origin"]!.ToObject<int[]>());

        world.SetBlocks(streamCtx, 0, 0, 0, 2, 1, 2, new JObject
        {
            ["block_id"] = "minecraft:gold_block",
            ["state"] = new JObject()
        });
        var boxDelta = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("blockDelta", boxDelta["type"]!.Value<string>());
        Assert.Equal("stream-42", boxDelta["streamId"]!.Value<string>());
        Assert.Equal(new[] { 100, 63, 100 }, boxDelta["bounds"]!["min"]!.ToObject<int[]>());
        Assert.Equal(new[] { 102, 64, 102 }, boxDelta["bounds"]!["max"]!.ToObject<int[]>());
        Assert.DoesNotContain("viewer-secret-01", boxDelta.ToString());
        Assert.DoesNotContain("wireScopeId", boxDelta.Properties().Select(p => p.Name));

        world.PostChat(streamCtx, "Hello Sandbox");
        var chatMsg = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("chat", chatMsg["type"]!.Value<string>());
        Assert.Equal("Hello Sandbox", chatMsg["message"]!.Value<string>());

        // 4. World reset increments worldEpoch and preserves stream binding
        world.Reset();
        var resetSnapshot = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", resetSnapshot["type"]!.Value<string>());
        Assert.Equal(2, resetSnapshot["worldEpoch"]!.Value<long>());
        Assert.Equal("stream-42", resetSnapshot["activeStream"]!["streamId"]!.Value<string>());

        // 5. Rate-limit pairing attempts after 5 failures within 60s
        for (int i = 0; i < 5; i++)
        {
            await SendJsonAsync(ws, new JObject
            {
                ["type"] = "approvePair",
                ["viewerId"] = "viewer-secret-01",
                ["pairCode"] = "000-00" + i
            }, cts.Token);
            var failMsg = await ReceiveJsonAsync(ws, cts.Token);
            Assert.False(failMsg["approved"]!.Value<bool>());
            Assert.Equal("invalid_pair_code", failMsg["reason"]!.Value<string>());
        }

        await SendJsonAsync(ws, new JObject
        {
            ["type"] = "approvePair",
            ["viewerId"] = "viewer-secret-01",
            ["pairCode"] = "999-999"
        }, cts.Token);
        var rateLimitedMsg = await ReceiveJsonAsync(ws, cts.Token);
        Assert.False(rateLimitedMsg["approved"]!.Value<bool>());
        Assert.Equal("rate_limited", rateLimitedMsg["reason"]!.Value<string>());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);

        // 6. Reconnect with same viewerId restores bound stream and receives resync snapshot
        using var ws2 = new ClientWebSocket();
        await ws2.ConnectAsync(wsUri, cts.Token);
        var reconnectSnapshot = await ReceiveJsonAsync(ws2, cts.Token);
        Assert.Equal("snapshot", reconnectSnapshot["type"]!.Value<string>());
        Assert.Equal("stream-42", reconnectSnapshot["activeStream"]!["streamId"]!.Value<string>());
        await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task WebSocketSyncOversizedMessageClosesWithMessageTooBigWhileAcceptingNormalMessages()
    {
        var limits = new SandboxConnectionLimits();
        await using var app = SandboxServerApp.Build(new SandboxServerOptions
        {
            ListenUrl = "http://127.0.0.1:0",
            Limits = limits
        });

        await app.StartAsync();
        string httpUrl = app.Urls.First();
        var wsBase = httpUrl.Replace("http://", "ws://", StringComparison.Ordinal);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var wsNormal = new ClientWebSocket();
        await wsNormal.ConnectAsync(new Uri(wsBase + "/ws/sync?viewerId=viewer-normal-size"), cts.Token);
        Assert.Equal("snapshot", (await ReceiveJsonAsync(wsNormal, cts.Token))["type"]!.Value<string>());

        using var wsBig = new ClientWebSocket();
        await wsBig.ConnectAsync(new Uri(wsBase + "/ws/sync?viewerId=viewer-oversized"), cts.Token);
        Assert.Equal("snapshot", (await ReceiveJsonAsync(wsBig, cts.Token))["type"]!.Value<string>());

        // Normal resync still works on wsBig before sending an oversized frame
        await SendJsonAsync(wsBig, new JObject { ["type"] = "resync", ["viewerId"] = "viewer-oversized" }, cts.Token);
        Assert.Equal("snapshot", (await ReceiveJsonAsync(wsBig, cts.Token))["type"]!.Value<string>());

        // 65,537 bytes exceeds MaxWebSocketMessageBytes (65,536) -> WebSocket Close 1009 (MessageTooBig)
        byte[] oversized = new byte[limits.MaxWebSocketMessageBytes + 1];
        Array.Fill(oversized, (byte)'x');
        await wsBig.SendAsync(new ArraySegment<byte>(oversized), WebSocketMessageType.Text, true, cts.Token);

        var recvBuf = new byte[4096];
        var result = await wsBig.ReceiveAsync(new ArraySegment<byte>(recvBuf), cts.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);

        // Existing normal viewer connection remains unaffected
        await SendJsonAsync(wsNormal, new JObject { ["type"] = "resync", ["viewerId"] = "viewer-normal-size" }, cts.Token);
        Assert.Equal("snapshot", (await ReceiveJsonAsync(wsNormal, cts.Token))["type"]!.Value<string>());
        await wsNormal.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }
}

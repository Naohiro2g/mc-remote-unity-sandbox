using System;
using System.Collections.Generic;
using Code2Create.UnitySandbox.World;
using Code2Create.UnitySandbox.World.JsonRpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Code2Create.UnitySandbox.Tests;

public class WorldModelTests
{
    private static JObject Spec(string blockId, params (string key, object value)[] stateEntries)
    {
        var state = new JObject();
        foreach (var (key, value) in stateEntries)
        {
            state[key] = new JValue(value);
        }
        return new JObject
        {
            ["block_id"] = blockId,
            ["state"] = state
        };
    }

    [Fact]
    public void WorldConstantsAndPlotOriginsMatchMinecraft12111Spec()
    {
        Assert.Equal("1.21.11", WorldConstants.McVersion);
        Assert.Equal("minecraft:overworld", WorldConstants.DefaultDimension);
        Assert.Equal(-64, WorldConstants.YMin);
        Assert.Equal(319, WorldConstants.YMax);
        Assert.Equal(62, WorldConstants.YSea);
        Assert.Equal(63, WorldConstants.YGround);
        Assert.Equal(-200, WorldConstants.MinX);
        Assert.Equal(199, WorldConstants.MaxX);
        Assert.Equal(-200, WorldConstants.MinZ);
        Assert.Equal(199, WorldConstants.MaxZ);
        Assert.Equal(9, WorldConstants.PlotCount);
        Assert.Equal(25585, WorldConstants.DefaultTcpPort);
        Assert.Equal(32_768, WorldConstants.DefaultMaxSetBlocksVolume);

        // Plot 0..8 grid centered around Plot 4 = (0, 0, 0)
        Assert.Equal(new Int3(-100, 0, -100), WorldConstants.PlotOrigin(0));
        Assert.Equal(new Int3(0, 0, 0), WorldConstants.PlotOrigin(4));
        Assert.Equal(new Int3(100, 0, 100), WorldConstants.PlotOrigin(8));
    }

    [Fact]
    public void InitialTerrainMatchesGrassDirtStoneAndEasterEggStrata()
    {
        var world = new SandboxWorld();
        var ctx = new StreamBuildContext("stream-0");

        Assert.Equal("minecraft:air", world.GetBlock(ctx, 5, 63, 5).BlockId);
        var grass = world.GetBlock(ctx, 5, 62, 5);
        Assert.Equal("minecraft:grass_block", grass.BlockId);
        Assert.Equal(BlockStateScalar.FromBoolean(false), grass.State["snowy"]);
        Assert.Equal("minecraft:grass_block", world.GetBlock(ctx, 100, 62, 100).BlockId);
        Assert.Equal("minecraft:grass_block", world.GetBlock(ctx, 48, 62, 48).BlockId);
        Assert.Equal("minecraft:grass_block", world.GetBlock(ctx, -48, 62, -48).BlockId);
        Assert.Equal("minecraft:air", world.GetBlock(ctx, 49, 62, 48).BlockId);
        Assert.Equal("minecraft:air", world.GetBlock(ctx, -49, 62, -48).BlockId);
        Assert.Equal("minecraft:air", world.GetBlock(ctx, 50, 62, 50).BlockId);
        Assert.Equal("minecraft:dirt", world.GetBlock(ctx, 50, 61, 50).BlockId);

        Assert.Equal("minecraft:dirt", world.GetBlock(ctx, 5, 61, 5).BlockId);
        Assert.Equal("minecraft:dirt", world.GetBlock(ctx, 5, 60, 5).BlockId);
        Assert.Equal("minecraft:stone", world.GetBlock(ctx, 5, 59, 5).BlockId);
        Assert.Equal("minecraft:stone", world.GetBlock(ctx, 5, -64, 5).BlockId);

        // Underground Easter egg under plot origins at y % 10 == 0
        Assert.Equal("minecraft:gold_block", world.GetBlock(ctx, 0, 40, 0).BlockId);
        Assert.Equal("minecraft:diamond_block", world.GetBlock(ctx, 0, 0, 0).BlockId);
        Assert.Equal("minecraft:tnt", world.GetBlock(ctx, 100, -60, 100).BlockId);

        // Digging replaces terrain with air; Reset restores terrain and advances worldEpoch
        world.SetBlock(ctx, 5, 62, 5, Spec("minecraft:air"));
        Assert.Equal("minecraft:air", world.GetBlock(ctx, 5, 62, 5).BlockId);

        long epochBefore = world.WorldEpoch;
        var resetSnapshot = world.Reset();
        Assert.Equal(epochBefore + 1, world.WorldEpoch);
        Assert.Equal(0, resetSnapshot.Seq);
        Assert.Empty(resetSnapshot.Regions);
        Assert.Equal("minecraft:grass_block", world.GetBlock(ctx, 5, 62, 5).BlockId);
    }

    [Fact]
    public void BasicCatalogHasTenBlocksAndSupportsExtension()
    {
        var registry = SandboxBlockRegistry.CreateBasic();
        Assert.Equal(10, registry.Blocks.Count);

        var catalog = registry.BuildCatalogResult();
        var blocksObj = Assert.IsType<JObject>(catalog["block"]);
        Assert.True(blocksObj.ContainsKey("minecraft:air"));
        Assert.True(blocksObj.ContainsKey("minecraft:grass_block"));
        Assert.True(blocksObj.ContainsKey("minecraft:dirt"));
        Assert.True(blocksObj.ContainsKey("minecraft:stone"));
        Assert.True(blocksObj.ContainsKey("minecraft:gold_block"));
        Assert.True(blocksObj.ContainsKey("minecraft:iron_block"));
        Assert.True(blocksObj.ContainsKey("minecraft:sea_lantern"));
        Assert.True(blocksObj.ContainsKey("minecraft:glass"));
        Assert.True(blocksObj.ContainsKey("minecraft:diamond_block"));
        Assert.True(blocksObj.ContainsKey("minecraft:tnt"));

        // Verify 1.21.11 states on grass_block and tnt
        Assert.False(blocksObj["minecraft:grass_block"]!["default_state"]!["snowy"]!.Value<bool>());
        Assert.False(blocksObj["minecraft:tnt"]!["default_state"]!["unstable"]!.Value<bool>());
        Assert.NotNull(catalog["catalogHash"]?.Value<string>());

        // Extensible registration
        registry.Register(new BlockDefinition("minecraft:emerald_block"));
        Assert.Equal(11, registry.Blocks.Count);
        var resolved = registry.ResolveAndValidate(Spec("emerald_block"), "params[3]");
        Assert.Equal("minecraft:emerald_block", resolved.BlockId);
    }

    [Fact]
    public void BlockSpecValidationNormalizesAndRejectsInvalidSpecs()
    {
        var registry = SandboxBlockRegistry.CreateBasic();

        // Bare ID normalizes to minecraft: and fills default states
        var grass = registry.ResolveAndValidate(Spec("grass_block"));
        Assert.Equal("minecraft:grass_block", grass.BlockId);
        Assert.Equal(BlockStateScalar.FromBoolean(false), grass.State["snowy"]);

        var snowyGrass = registry.ResolveAndValidate(Spec("minecraft:grass_block", ("snowy", true)));
        Assert.Equal(BlockStateScalar.FromBoolean(true), snowyGrass.State["snowy"]);

        // Unknown block
        var exUnknown = Assert.Throws<SandboxOperationException>(() =>
            registry.ResolveAndValidate(Spec("minecraft:definitely_not_a_block")));
        Assert.Equal("unknown_block", exUnknown.Error.Reason);
        Assert.Equal(-32602, exUnknown.Error.Code);

        // Unknown property
        var exUnknownProp = Assert.Throws<SandboxOperationException>(() =>
            registry.ResolveAndValidate(Spec("minecraft:stone", ("facing", "north"))));
        Assert.Equal("unknown_property", exUnknownProp.Error.Reason);
        Assert.Equal(-32602, exUnknownProp.Error.Code);

        // Invalid property value includes allowed array
        var exInvalidVal = Assert.Throws<SandboxOperationException>(() =>
            registry.ResolveAndValidate(Spec("minecraft:grass_block", ("snowy", "maybe"))));
        Assert.Equal("invalid_property_value", exInvalidVal.Error.Reason);
        Assert.Equal(-32602, exInvalidVal.Error.Code);
        var allowed = Assert.IsType<object[]>(exInvalidVal.Error.Data["allowed"]);
        Assert.Equal(new object[] { false, true }, allowed);
    }

    [Fact]
    public void FourJsonRpcOperationsAndOutOfBoundsFollow2026111402()
    {
        var world = new SandboxWorld();
        var dispatcher = new SandboxJsonRpcDispatcher(world);
        var ctx = new StreamBuildContext("stream-a", WorldConstants.DefaultDimension, new Int3(10, 63, 20));

        var deltas = new List<WorldBlockDeltaEvent>();
        var chats = new List<WorldChatEvent>();
        world.BlockDeltaEmitted += deltas.Add;
        world.ChatEmitted += chats.Add;

        // 1. world.setBlock (relative to origin)
        string? setResp = dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"world.setBlock\",\"params\":[1,0,2,{\"block_id\":\"gold_block\",\"state\":{}}]}");
        Assert.NotNull(setResp);
        Assert.Contains("\"result\":null", setResp);
        Assert.Single(deltas);
        Assert.Equal(new Int3(11, 63, 22), deltas[0].Bounds.Min);
        Assert.Equal(new Int3(11, 63, 22), deltas[0].Bounds.Max);

        // 2. world.getBlock
        string? getResp = dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"world.getBlock\",\"params\":[1,0,2]}");
        var getObj = JObject.Parse(getResp!);
        Assert.Equal("minecraft:gold_block", getObj["result"]!["block_id"]!.Value<string>());

        // 3. world.setBlocks emits single box delta (not cell-expanded)
        string? boxResp = dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"world.setBlocks\",\"params\":[0,0,0,2,1,2,{\"block_id\":\"minecraft:glass\",\"state\":{}}]}");
        Assert.Contains("\"result\":null", boxResp);
        Assert.Equal(2, deltas.Count);
        Assert.Equal(18, deltas[1].Bounds.Volume);
        Assert.Equal(new Int3(10, 63, 20), deltas[1].Bounds.Min);
        Assert.Equal(new Int3(12, 64, 22), deltas[1].Bounds.Max);

        // 4. chat.post
        string? chatResp = dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"chat.post\",\"params\":[\"こんにちはUnity箱庭\"]}");
        Assert.Contains("\"result\":null", chatResp);
        Assert.Single(chats);
        Assert.Equal("こんにちはUnity箱庭", chats[0].Message);

        // FAST notification returns null (no response) and applies side-effect
        string? notifResp = dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"method\":\"world.setBlock\",\"params\":[5,0,5,{\"block_id\":\"diamond_block\",\"state\":{}}]}");
        Assert.Null(notifResp);
        Assert.Equal("minecraft:diamond_block", world.GetBlock(ctx, 5, 0, 5).BlockId);
    }

    [Theory]
    [InlineData(-10, -65, 0)]
    [InlineData(-10, 320, 0)]
    [InlineData(-201, 63, 0)]
    [InlineData(200, 63, 0)]
    [InlineData(0, 63, -201)]
    [InlineData(0, 63, 200)]
    public void OutOfBoundsAppliesToBothHeightAndHorizontalForSetGetAndSetBlocks(int absX, int absY, int absZ)
    {
        var world = new SandboxWorld();
        var ctx = new StreamBuildContext("stream-bounds");

        // world.setBlock
        var exSet = Assert.Throws<SandboxOperationException>(() =>
            world.SetBlock(ctx, absX, absY, absZ, Spec("minecraft:gold_block")));
        AssertOutOfBoundsError(exSet.Error, absX, absY, absZ);

        // world.getBlock
        var exGet = Assert.Throws<SandboxOperationException>(() =>
            world.GetBlock(ctx, absX, absY, absZ));
        AssertOutOfBoundsError(exGet.Error, absX, absY, absZ);

        // world.setBlocks partial box starting at valid (0, 63, 0) and crossing boundary to (absX, absY, absZ)
        long seqBefore = world.Seq;
        var exBox = Assert.Throws<SandboxOperationException>(() =>
            world.SetBlocks(ctx, 0, 63, 0, absX, absY, absZ, Spec("minecraft:diamond_block")));
        AssertOutOfBoundsError(exBox.Error, absX, absY, absZ);
        // Atomic: zero blocks changed
        Assert.Equal(seqBefore, world.Seq);
        Assert.Equal("minecraft:air", world.GetBlock(ctx, 0, 63, 0).BlockId);
    }

    [Fact]
    public void SetBlocksExceedingVolumeLimitReturnsBlockCountLimitExceededAtomically()
    {
        var world = new SandboxWorld();
        var ctx = new StreamBuildContext("stream-limit");

        // 33 * 32 * 32 = 33,792 > 32,768
        var ex = Assert.Throws<SandboxOperationException>(() =>
            world.SetBlocks(ctx, 0, 63, 0, 32, 94, 31, Spec("minecraft:stone")));
        Assert.Equal("block_count_limit_exceeded", ex.Error.Reason);
        Assert.Equal(-32000, ex.Error.Code);
        var data = Assert.IsType<JObject>(ex.Error.ToJson()["data"]);
        Assert.Equal("block_count_limit_exceeded", data["reason"]!.Value<string>());
        Assert.Equal(32_768, data["limit"]!.Value<int>());
        Assert.Equal(33_792L, data["requested"]!.Value<long>());
        Assert.False(data.ContainsKey("violating"));
        Assert.Equal("minecraft:air", world.GetBlock(ctx, 0, 63, 0).BlockId);
    }

    [Fact]
    public void SnapshotCoalescesLargeBoxesWithoutArtificialCap()
    {
        var world = new SandboxWorld();
        var ctx = new StreamBuildContext("stream-coalesce");

        // Place two 32x32x32 cubes (> 65k modified cells total) and verify coalescing compresses them efficiently
        world.SetBlocks(ctx, 0, 63, 0, 31, 94, 31, Spec("minecraft:gold_block"));
        world.SetBlocks(ctx, 40, 63, 0, 71, 94, 31, Spec("minecraft:diamond_block"));

        Assert.Equal(65_536, world.ModifiedCellCount);
        var snapshot = world.GetSnapshot();
        Assert.True(snapshot.Regions.Count <= 32);
    }

    [Fact]
    public void GetBlocksOrdersXYZAscendingHandlesReversedEndpointsAndChecksWorkLimitBeforeBounds()
    {
        var world = new SandboxWorld();
        var dispatcher = new SandboxJsonRpcDispatcher(world);
        var ctx = new StreamBuildContext("stream-getblocks", WorldConstants.DefaultDimension, new Int3(10, 63, 20));

        // Populate distinct blocks in a 2 (x) x 2 (y) x 3 (z) = 12-cell box at relative [0..1, 0..1, 0..2]
        world.SetBlock(ctx, 0, 0, 0, Spec("minecraft:gold_block"));
        world.SetBlock(ctx, 0, 0, 2, Spec("minecraft:iron_block"));
        world.SetBlock(ctx, 0, 1, 1, Spec("minecraft:sea_lantern"));
        world.SetBlock(ctx, 1, 1, 2, Spec("minecraft:diamond_block"));

        var forwardResp = JObject.Parse(dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"world.getBlocks\",\"params\":[0,0,0,1,1,2]}")!);
        var reversedResp = JObject.Parse(dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"world.getBlocks\",\"params\":[1,1,2,0,0,0]}")!);

        var forwardArr = Assert.IsType<JArray>(forwardResp["result"]);
        var reversedArr = Assert.IsType<JArray>(reversedResp["result"]);
        Assert.Equal(12, forwardArr.Count);
        Assert.Equal(forwardArr.ToString(Newtonsoft.Json.Formatting.None), reversedArr.ToString(Newtonsoft.Json.Formatting.None));

        // Order: x outer, y middle, z inner -> (0,0,0)=0, (0,0,2)=2, (0,1,1)=4, (1,1,2)=11
        Assert.Equal("minecraft:gold_block", forwardArr[0]["block_id"]!.Value<string>());
        Assert.Equal("minecraft:iron_block", forwardArr[2]["block_id"]!.Value<string>());
        Assert.Equal("minecraft:sea_lantern", forwardArr[4]["block_id"]!.Value<string>());
        Assert.Equal("minecraft:diamond_block", forwardArr[11]["block_id"]!.Value<string>());

        // Max 10x10x10 = 1000 cells succeeds
        var maxOk = JObject.Parse(dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"world.getBlocks\",\"params\":[0,0,0,9,9,9]}")!);
        Assert.Equal(1000, ((JArray)maxOk["result"]!).Count);

        // Axis length > 10 AND crossing world bounds -> work_limit_exceeded checked before world bounds
        var limitBeforeBounds = JObject.Parse(dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"world.getBlocks\",\"params\":[185,0,0,195,0,0]}")!);
        Assert.Equal(-32000, limitBeforeBounds["error"]!["code"]!.Value<int>());
        Assert.Equal("work_limit_exceeded", limitBeforeBounds["error"]!["data"]!["reason"]!.Value<string>());
        Assert.Null(limitBeforeBounds["error"]!["data"]!["limit"]);

        // Within axis limit (2 cells) but outside world bounds (abs x=200) -> out_of_bounds
        var oob = JObject.Parse(dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"world.getBlocks\",\"params\":[189,0,0,190,0,0]}")!);
        Assert.Equal(-32000, oob["error"]!["code"]!.Value<int>());
        Assert.Equal("out_of_bounds", oob["error"]!["data"]!["reason"]!.Value<string>());

        // Non-integer param rejected with invalid_params (-32602)
        var nonInt = JObject.Parse(dispatcher.DispatchLine(ctx,
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"world.getBlocks\",\"params\":[0.5,0,0,1,0,0]}")!);
        Assert.Equal(-32602, nonInt["error"]!["code"]!.Value<int>());
        Assert.Equal("invalid_params", nonInt["error"]!["data"]!["reason"]!.Value<string>());
    }

    [Fact]
    public void GetHeightSupportsPlotsGapsMultiLayerCeilingTopOfWorldAndErrors()
    {
        var world = new SandboxWorld();
        var dispatcher = new SandboxJsonRpcDispatcher(world);
        var zeroOrigin = new StreamBuildContext("stream-height-0");
        var offsetOrigin = new StreamBuildContext("stream-height-off", WorldConstants.DefaultDimension, new Int3(100, 10, 100));

        // Plot grass surface y=62, inter-plot dirt surface y=61, relative offset on offsetOrigin -> 52
        Assert.Equal(62, world.GetHeight(zeroOrigin, 5, 5));
        Assert.Equal(61, world.GetHeight(zeroOrigin, 50, 50));
        Assert.Equal(52, JObject.Parse(dispatcher.DispatchLine(offsetOrigin,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"world.getHeight\",\"params\":[5,5]}")!)["result"]!.Value<int>());

        // Non-air (including glass / sea_lantern / tnt) counts as non-passable roof; covered block under solid roof is skipped when scanning from covered y
        world.SetBlock(zeroOrigin, 5, 70, 5, Spec("minecraft:glass"));
        Assert.Equal(70, world.GetHeight(zeroOrigin, 5, 5));
        Assert.Equal(70, world.GetHeight(zeroOrigin, 5, 5, 70));
        world.SetBlock(zeroOrigin, 5, 71, 5, Spec("minecraft:stone"));
        // At max_y=70, above (71) is stone (not passable), so y=70 is NOT exposed -> finds y=62 grass below!
        Assert.Equal(62, world.GetHeight(zeroOrigin, 5, 5, 70));
        Assert.Equal(62, JObject.Parse(dispatcher.DispatchLine(zeroOrigin,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"world.getHeight\",\"params\":[5,5,70]}")!)["result"]!.Value<int>());

        // Top of world (y=319) is exposed because above world top is treated as passable
        world.SetBlock(zeroOrigin, 6, 319, 6, Spec("minecraft:sea_lantern"));
        Assert.Equal(319, world.GetHeight(zeroOrigin, 6, 6));

        // Dug all the way down to y=-64 -> height_not_found (-32000)
        world.SetBlocks(zeroOrigin, 7, -64, 7, 7, 319, 7, Spec("minecraft:air"));
        var notFound = JObject.Parse(dispatcher.DispatchLine(zeroOrigin,
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"world.getHeight\",\"params\":[7,7]}")!);
        Assert.Equal(-32000, notFound["error"]!["code"]!.Value<int>());
        Assert.Equal("height_not_found", notFound["error"]!["data"]!["reason"]!.Value<string>());

        // max_y below bedrock -> height_not_found
        var belowMin = JObject.Parse(dispatcher.DispatchLine(zeroOrigin,
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"world.getHeight\",\"params\":[5,5,-65]}")!);
        Assert.Equal("height_not_found", belowMin["error"]!["data"]!["reason"]!.Value<string>());

        // Horizontal out of bounds -> out_of_bounds
        var oob = JObject.Parse(dispatcher.DispatchLine(zeroOrigin,
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"world.getHeight\",\"params\":[200,0]}")!);
        Assert.Equal(-32000, oob["error"]!["code"]!.Value<int>());
        Assert.Equal("out_of_bounds", oob["error"]!["data"]!["reason"]!.Value<string>());
    }

    [Fact]
    public void WireNumbersDirectionAndEventRingMatchSpec()
    {
        // §5.0.1 HALF_UP rounding, -0 -> 0, stripped trailing zeros
        Assert.Equal("1.235", SandboxWireNumbers.Position(1.2345).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("-1.235", SandboxWireNumbers.Position(-1.2345).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("0", SandboxWireNumbers.Position(-0.0001).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("64", SandboxWireNumbers.Position(64.0).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("1.2", SandboxWireNumbers.Position(1.200).ToString(Newtonsoft.Json.Formatting.None));

        Assert.Equal("-180", SandboxWireNumbers.Yaw(180.0).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("-180", SandboxWireNumbers.Yaw(179.996).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("-179", SandboxWireNumbers.Yaw(181.0).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("179", SandboxWireNumbers.Yaw(-181.0).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("-180", SandboxWireNumbers.Yaw(540.0).ToString(Newtonsoft.Json.Formatting.None));

        Assert.Equal("-90", SandboxWireNumbers.Pitch(-90.0).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("90", SandboxWireNumbers.Pitch(90.0).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Throws<SandboxOperationException>(() => SandboxWireNumbers.RequirePitch(90.01));
        Assert.Throws<SandboxOperationException>(() => SandboxWireNumbers.RequirePitch(-90.01));

        // §5.8.2 scale-safe direction & zero_direction
        var unit1 = SandboxDirectionUnit.Normalize(1, 2, 3);
        var unit10 = SandboxDirectionUnit.Normalize(10, 20, 30);
        Assert.Equal(
            unit1.ToWireJson().ToString(Newtonsoft.Json.Formatting.None),
            unit10.ToWireJson().ToString(Newtonsoft.Json.Formatting.None));
        var huge = SandboxDirectionUnit.Normalize(1e200, 0, 0);
        Assert.Equal("[-1,0,0]", SandboxDirectionUnit.FromYawPitch(90, 0).ToWireJson().ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("[1,0,0]", huge.ToWireJson().ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal(-90.0, huge.ToYawDegrees(), 6);
        Assert.Equal(0.0, SandboxDirectionUnit.Normalize(0, 0, 1).ToYawDegrees(), 6);
        Assert.Equal(-180.0, SandboxDirectionUnit.Normalize(0, 0, -1).ToYawDegrees(), 6);
        Assert.Equal(-90.0, SandboxDirectionUnit.Normalize(0, 1, 0).ToPitchDegrees(), 6);
        Assert.Equal(90.0, SandboxDirectionUnit.Normalize(0, -1, 0).ToPitchDegrees(), 6);

        var exZero = Assert.Throws<SandboxOperationException>(() => SandboxDirectionUnit.Normalize(0.0, -0.0, 0.0));
        Assert.Equal("zero_direction", exZero.Error.Reason);
        Assert.Equal(-32602, exZero.Error.Code);

        // §5.4 finite event ring overflow + non-destructive replay
        var ring = new SandboxEventRing(maxEvents: 2, maxBytes: 4096, maxResultBytes: 61_440);
        Assert.True(ring.OfferChatPosted("minecraft:overworld", new Int3(0, 0, 0), "first"));
        Assert.True(ring.OfferChatPosted("minecraft:overworld", new Int3(10, 0, 10), "second"));
        Assert.True(ring.OfferChatPosted("minecraft:overworld", new Int3(20, 0, 20), "third"));

        var poll = ring.Poll(0, 64);
        var evs = Assert.IsType<JArray>(poll["events"]);
        Assert.Equal(2, evs.Count);
        Assert.Equal(2L, evs[0]["sequence"]!.Value<long>());
        Assert.Equal("second", evs[0]["message"]!.Value<string>());
        Assert.Equal(3L, evs[1]["sequence"]!.Value<long>());
        Assert.Equal("third", evs[1]["message"]!.Value<string>());
        Assert.Equal(3L, poll["through_sequence"]!.Value<long>());
        Assert.Equal(3L, poll["latest_sequence"]!.Value<long>());
        Assert.Equal(1L, poll["overflow_dropped_total"]!.Value<long>());

        // Non-destructive replay with same cursor returns same events
        var replay = ring.Poll(0, 1);
        Assert.Single((JArray)replay["events"]!);
        Assert.Equal(2L, replay["through_sequence"]!.Value<long>());
        Assert.Equal(3L, replay["latest_sequence"]!.Value<long>());

        // after_sequence > latest_sequence -> invalid_params
        Assert.Throws<SandboxOperationException>(() => ring.Poll(4, 64));
    }

    private static void AssertOutOfBoundsError(SandboxProtocolError error, int expectedX, int expectedY, int expectedZ)
    {
        Assert.Equal("out_of_bounds", error.Reason);
        Assert.Equal(-32000, error.Code);
        var json = error.ToJson();
        var data = Assert.IsType<JObject>(json["data"]);
        Assert.Equal("out_of_bounds", data["reason"]!.Value<string>());
        var bounds = Assert.IsType<JObject>(data["bounds"]);
        Assert.Equal(-64, bounds["y_min"]!.Value<int>());
        Assert.Equal(319, bounds["y_max"]!.Value<int>());
        var worldBorder = Assert.IsType<JObject>(bounds["world_border"]);
        var center = Assert.IsType<JObject>(worldBorder["center"]);
        Assert.Equal(0.0, center["x"]!.Value<double>());
        Assert.Equal(0.0, center["z"]!.Value<double>());
        Assert.Equal(400.0, worldBorder["size"]!.Value<double>());
        var violating = Assert.IsType<JObject>(data["violating"]);
        Assert.Equal(expectedX, violating["x"]!.Value<int>());
        Assert.Equal(expectedY, violating["y"]!.Value<int>());
        Assert.Equal(expectedZ, violating["z"]!.Value<int>());
    }
}

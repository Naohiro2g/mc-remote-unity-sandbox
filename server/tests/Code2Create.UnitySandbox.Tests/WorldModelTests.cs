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
    public void SetBlocksExceedingVolumeLimitReturnsWorkLimitExceededAtomically()
    {
        var world = new SandboxWorld();
        var ctx = new StreamBuildContext("stream-limit");

        // 33 * 32 * 32 = 33,792 > 32,768
        var ex = Assert.Throws<SandboxOperationException>(() =>
            world.SetBlocks(ctx, 0, 63, 0, 32, 94, 31, Spec("minecraft:stone")));
        Assert.Equal("work_limit_exceeded", ex.Error.Reason);
        Assert.Equal(-32000, ex.Error.Code);
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

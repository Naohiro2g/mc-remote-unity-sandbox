using System;
using System.Collections.Generic;

namespace Code2Create.UnitySandbox.World
{
    public enum EasterEggMode
    {
        /// <summary>
        /// Places treasure strata at y % 10 == 0 (-60..50) within 3x3 columns under each of the 9 plot origins.
        /// </summary>
        PlotOriginColumnsAtTens = 0,

        /// <summary>
        /// Places no underground Easter egg blocks (pure grass y=62, dirt y=61..60, stone y=59..-64).
        /// </summary>
        None = 1
    }

    /// <summary>
    /// Deterministic initial terrain for the 1.21.11-aligned Unity Sandbox.
    /// y >= 63: air, y == 62: grass_block inside [-48, 48] of each plot origin and air elsewhere (exposing y == 61 dirt), y == 61..60: dirt, y == 59..-64: stone (plus optional Easter eggs).
    /// </summary>
    public sealed class InitialTerrain
    {
        private readonly BlockValue air;
        private readonly BlockValue grass;
        private readonly BlockValue dirt;
        private readonly BlockValue stone;
        private readonly Dictionary<int, BlockValue> easterEggByY = new Dictionary<int, BlockValue>();

        public EasterEggMode EasterEggMode { get; }

        public InitialTerrain(SandboxBlockRegistry registry, EasterEggMode easterEggMode = EasterEggMode.PlotOriginColumnsAtTens)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            EasterEggMode = easterEggMode;

            air = registry.ResolveSimple("minecraft:air");
            grass = registry.ResolveSimple("minecraft:grass_block");
            dirt = registry.ResolveSimple("minecraft:dirt");
            stone = registry.ResolveSimple("minecraft:stone");

            if (easterEggMode == EasterEggMode.PlotOriginColumnsAtTens)
            {
                var gold = registry.ResolveSimple("minecraft:gold_block");
                var iron = registry.ResolveSimple("minecraft:iron_block");
                var lantern = registry.ResolveSimple("minecraft:sea_lantern");
                var glass = registry.ResolveSimple("minecraft:glass");
                var diamond = registry.ResolveSimple("minecraft:diamond_block");
                var tnt = registry.ResolveSimple("minecraft:tnt");

                easterEggByY[50] = iron;
                easterEggByY[40] = gold;
                easterEggByY[30] = lantern;
                easterEggByY[20] = glass;
                easterEggByY[10] = gold;
                easterEggByY[0] = diamond;
                easterEggByY[-10] = iron;
                easterEggByY[-20] = gold;
                easterEggByY[-30] = lantern;
                easterEggByY[-40] = diamond;
                easterEggByY[-50] = gold;
                easterEggByY[-60] = tnt;
            }
        }

        public BlockValue GetBlock(int absX, int absY, int absZ)
        {
            if (absY >= WorldConstants.YGround) return air;
            if (absY == WorldConstants.YSea) return WorldConstants.IsInAnyPlotGrass(absX, absZ) ? grass : air;
            if (absY >= 60) return dirt;

            if (EasterEggMode == EasterEggMode.PlotOriginColumnsAtTens &&
                easterEggByY.TryGetValue(absY, out var eggBlock) &&
                IsNearAnyPlotOrigin(absX, absZ))
            {
                return eggBlock;
            }

            return stone;
        }

        private static bool IsNearAnyPlotOrigin(int absX, int absZ)
        {
            for (int i = 0; i < WorldConstants.PlotCount; i++)
            {
                var origin = WorldConstants.PlotOrigin(i);
                if (Math.Abs(absX - origin.X) <= 1 && Math.Abs(absZ - origin.Z) <= 1)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

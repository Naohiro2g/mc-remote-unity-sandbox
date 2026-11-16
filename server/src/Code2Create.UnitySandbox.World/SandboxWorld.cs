using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Thread-safe, UnityEngine-independent world model for the 400x400x384 Unity Sandbox.
    /// Stores only sparse 16x16x16 chunks that differ from InitialTerrain and coalesces snapshot regions into boxes.
    /// </summary>
    public sealed class SandboxWorld
    {
        private const int ChunkSize = 16;
        private const int ChunkCellCount = ChunkSize * ChunkSize * ChunkSize;

        private readonly object gate = new object();
        private readonly Dictionary<ChunkKey, BlockValue?[]> modifiedChunks = new Dictionary<ChunkKey, BlockValue?[]>();

        public SandboxBlockRegistry Registry { get; }
        public InitialTerrain Terrain { get; }
        public int MaxSetBlocksVolume { get; }

        public long WorldEpoch { get; private set; } = 1;
        public long Seq { get; private set; }

        public event Action<WorldBlockDeltaEvent>? BlockDeltaEmitted;
        public event Action<WorldChatEvent>? ChatEmitted;
        public event Action<WorldResetEvent>? WorldReset;

        public SandboxWorld(
            SandboxBlockRegistry? registry = null,
            EasterEggMode easterEggMode = EasterEggMode.PlotOriginColumnsAtTens,
            int maxSetBlocksVolume = WorldConstants.DefaultMaxSetBlocksVolume)
        {
            Registry = registry ?? SandboxBlockRegistry.CreateBasic();
            Terrain = new InitialTerrain(Registry, easterEggMode);
            MaxSetBlocksVolume = maxSetBlocksVolume > 0 ? maxSetBlocksVolume : throw new ArgumentOutOfRangeException(nameof(maxSetBlocksVolume));
        }

        public int ModifiedCellCount
        {
            get
            {
                lock (gate)
                {
                    int count = 0;
                    foreach (var cells in modifiedChunks.Values)
                    {
                        for (int i = 0; i < cells.Length; i++)
                        {
                            if (cells[i] != null) count++;
                        }
                    }
                    return count;
                }
            }
        }

        public int ModifiedChunkCount
        {
            get
            {
                lock (gate)
                {
                    return modifiedChunks.Count;
                }
            }
        }

        public void SetBlock(StreamBuildContext context, int relX, int relY, int relZ, JToken blockSpecToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var abs = context.Origin + new Int3(relX, relY, relZ);
            ValidateCoordinateBounds(abs);
            var resolved = Registry.ResolveAndValidate(blockSpecToken, "params[3]");

            WorldBlockDeltaEvent? emitted;
            lock (gate)
            {
                SetCellInternal(abs.X, abs.Y, abs.Z, resolved);
                Seq++;
                emitted = new WorldBlockDeltaEvent(WorldEpoch, Seq, context.StreamId, WorldBox.SingleCell(abs), resolved);
            }

            BlockDeltaEmitted?.Invoke(emitted);
        }

        public BlockValue GetBlock(StreamBuildContext context, int relX, int relY, int relZ)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var abs = context.Origin + new Int3(relX, relY, relZ);
            ValidateCoordinateBounds(abs);

            lock (gate)
            {
                return GetCellInternal(abs.X, abs.Y, abs.Z);
            }
        }

        public void SetBlocks(StreamBuildContext context, int relX1, int relY1, int relZ1, int relX2, int relY2, int relZ2, JToken blockSpecToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var abs1 = context.Origin + new Int3(relX1, relY1, relZ1);
            var abs2 = context.Origin + new Int3(relX2, relY2, relZ2);

            ValidateCoordinateBounds(abs1);
            ValidateCoordinateBounds(abs2);

            var box = WorldBox.FromEndpoints(abs1, abs2);
            if (box.Volume > MaxSetBlocksVolume)
            {
                throw new SandboxOperationException(SandboxProtocolError.BlockCountLimitExceeded(box.Volume, MaxSetBlocksVolume));
            }

            var resolved = Registry.ResolveAndValidate(blockSpecToken, "params[6]");

            WorldBlockDeltaEvent? emitted;
            lock (gate)
            {
                for (int x = box.Min.X; x <= box.Max.X; x++)
                for (int y = box.Min.Y; y <= box.Max.Y; y++)
                for (int z = box.Min.Z; z <= box.Max.Z; z++)
                {
                    SetCellInternal(x, y, z, resolved);
                }

                Seq++;
                emitted = new WorldBlockDeltaEvent(WorldEpoch, Seq, context.StreamId, box, resolved);
            }

            BlockDeltaEmitted?.Invoke(emitted);
        }

        public void PostChat(StreamBuildContext context, string message)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (message == null)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[0]"));
            }

            WorldChatEvent emitted;
            lock (gate)
            {
                Seq++;
                emitted = new WorldChatEvent(WorldEpoch, Seq, context.StreamId, message);
            }

            ChatEmitted?.Invoke(emitted);
        }

        public WorldSnapshot Reset()
        {
            WorldResetEvent resetEvent;
            WorldSnapshot snapshot;
            lock (gate)
            {
                modifiedChunks.Clear();
                WorldEpoch++;
                Seq = 0;
                resetEvent = new WorldResetEvent(WorldEpoch, Seq);
                snapshot = new WorldSnapshot(WorldEpoch, Seq, Array.Empty<WorldBoxChange>());
            }

            WorldReset?.Invoke(resetEvent);
            return snapshot;
        }

        public WorldSnapshot GetSnapshot()
        {
            lock (gate)
            {
                var regions = new List<WorldBoxChange>();
                foreach (var kv in modifiedChunks)
                {
                    CoalesceChunkRegions(kv.Key, kv.Value, regions);
                }
                return new WorldSnapshot(WorldEpoch, Seq, regions);
            }
        }

        private static void ValidateCoordinateBounds(Int3 abs)
        {
            if (!WorldConstants.IsInBounds(abs))
            {
                throw new SandboxOperationException(SandboxProtocolError.OutOfBounds(abs));
            }
        }

        private BlockValue GetCellInternal(int absX, int absY, int absZ)
        {
            var (chunkKey, localIndex) = SplitCoordinates(absX, absY, absZ);
            if (modifiedChunks.TryGetValue(chunkKey, out var cells))
            {
                var overridden = cells[localIndex];
                if (overridden != null) return overridden;
            }
            return Terrain.GetBlock(absX, absY, absZ);
        }

        private void SetCellInternal(int absX, int absY, int absZ, BlockValue value)
        {
            var (chunkKey, localIndex) = SplitCoordinates(absX, absY, absZ);
            var baseline = Terrain.GetBlock(absX, absY, absZ);
            bool matchesBaseline = value.Equals(baseline);

            if (!modifiedChunks.TryGetValue(chunkKey, out var cells))
            {
                if (matchesBaseline) return;
                cells = new BlockValue?[ChunkCellCount];
                cells[localIndex] = value;
                modifiedChunks[chunkKey] = cells;
                return;
            }

            cells[localIndex] = matchesBaseline ? null : value;
            if (matchesBaseline)
            {
                bool anyRemaining = false;
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] != null)
                    {
                        anyRemaining = true;
                        break;
                    }
                }
                if (!anyRemaining)
                {
                    modifiedChunks.Remove(chunkKey);
                }
            }
        }

        private static (ChunkKey key, int index) SplitCoordinates(int absX, int absY, int absZ)
        {
            int cx = FloorDiv(absX, ChunkSize);
            int cy = FloorDiv(absY, ChunkSize);
            int cz = FloorDiv(absZ, ChunkSize);
            int lx = absX - cx * ChunkSize;
            int ly = absY - cy * ChunkSize;
            int lz = absZ - cz * ChunkSize;
            return (new ChunkKey(cx, cy, cz), LocalIndex(lx, ly, lz));
        }

        private static int LocalIndex(int lx, int ly, int lz) => (lx * ChunkSize + ly) * ChunkSize + lz;

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            int r = a % b;
            if (r != 0 && ((a ^ b) < 0)) q--;
            return q;
        }

        private static void CoalesceChunkRegions(ChunkKey key, BlockValue?[] cells, List<WorldBoxChange> output)
        {
            var visited = new bool[ChunkCellCount];
            int baseX = key.Cx * ChunkSize;
            int baseY = key.Cy * ChunkSize;
            int baseZ = key.Cz * ChunkSize;

            for (int x = 0; x < ChunkSize; x++)
            for (int y = 0; y < ChunkSize; y++)
            for (int z = 0; z < ChunkSize; z++)
            {
                int startIdx = LocalIndex(x, y, z);
                if (visited[startIdx]) continue;
                var block = cells[startIdx];
                if (block == null) continue;

                int maxZ = z;
                while (maxZ + 1 < ChunkSize)
                {
                    int idx = LocalIndex(x, y, maxZ + 1);
                    if (visited[idx] || !block.Equals(cells[idx])) break;
                    maxZ++;
                }

                int maxY = y;
                while (maxY + 1 < ChunkSize)
                {
                    bool rowMatches = true;
                    for (int iz = z; iz <= maxZ; iz++)
                    {
                        int idx = LocalIndex(x, maxY + 1, iz);
                        if (visited[idx] || !block.Equals(cells[idx]))
                        {
                            rowMatches = false;
                            break;
                        }
                    }
                    if (!rowMatches) break;
                    maxY++;
                }

                int maxX = x;
                while (maxX + 1 < ChunkSize)
                {
                    bool planeMatches = true;
                    for (int iy = y; iy <= maxY && planeMatches; iy++)
                    for (int iz = z; iz <= maxZ; iz++)
                    {
                        int idx = LocalIndex(maxX + 1, iy, iz);
                        if (visited[idx] || !block.Equals(cells[idx]))
                        {
                            planeMatches = false;
                            break;
                        }
                    }
                    if (!planeMatches) break;
                    maxX++;
                }

                for (int ix = x; ix <= maxX; ix++)
                for (int iy = y; iy <= maxY; iy++)
                for (int iz = z; iz <= maxZ; iz++)
                {
                    visited[LocalIndex(ix, iy, iz)] = true;
                }

                var min = new Int3(baseX + x, baseY + y, baseZ + z);
                var max = new Int3(baseX + maxX, baseY + maxY, baseZ + maxZ);
                output.Add(new WorldBoxChange(new WorldBox(min, max), block));
            }
        }

        private readonly struct ChunkKey : IEquatable<ChunkKey>
        {
            public int Cx { get; }
            public int Cy { get; }
            public int Cz { get; }

            public ChunkKey(int cx, int cy, int cz)
            {
                Cx = cx;
                Cy = cy;
                Cz = cz;
            }

            public bool Equals(ChunkKey other) => Cx == other.Cx && Cy == other.Cy && Cz == other.Cz;
            public override bool Equals(object? obj) => obj is ChunkKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Cx, Cy, Cz);
        }
    }
}

using System.Collections.Generic;

namespace Code2Create.UnitySandbox.World
{
    public sealed class WorldBoxChange
    {
        public WorldBox Bounds { get; }
        public BlockValue Block { get; }

        public WorldBoxChange(WorldBox bounds, BlockValue block)
        {
            Bounds = bounds;
            Block = block;
        }
    }

    public sealed class WorldSnapshot
    {
        public long WorldEpoch { get; }
        public long Seq { get; }
        public IReadOnlyList<WorldBoxChange> Regions { get; }

        public WorldSnapshot(long worldEpoch, long seq, IReadOnlyList<WorldBoxChange> regions)
        {
            WorldEpoch = worldEpoch;
            Seq = seq;
            Regions = regions;
        }
    }

    public sealed class WorldBlockDeltaEvent
    {
        public long WorldEpoch { get; }
        public long Seq { get; }
        public string StreamId { get; }
        public WorldBox Bounds { get; }
        public BlockValue Block { get; }

        public WorldBlockDeltaEvent(long worldEpoch, long seq, string streamId, WorldBox bounds, BlockValue block)
        {
            WorldEpoch = worldEpoch;
            Seq = seq;
            StreamId = streamId;
            Bounds = bounds;
            Block = block;
        }
    }

    public sealed class WorldChatEvent
    {
        public long WorldEpoch { get; }
        public long Seq { get; }
        public string StreamId { get; }
        public string Message { get; }

        public WorldChatEvent(long worldEpoch, long seq, string streamId, string message)
        {
            WorldEpoch = worldEpoch;
            Seq = seq;
            StreamId = streamId;
            Message = message;
        }
    }

    public sealed class WorldResetEvent
    {
        public long WorldEpoch { get; }
        public long Seq { get; }

        public WorldResetEvent(long worldEpoch, long seq)
        {
            WorldEpoch = worldEpoch;
            Seq = seq;
        }
    }
}

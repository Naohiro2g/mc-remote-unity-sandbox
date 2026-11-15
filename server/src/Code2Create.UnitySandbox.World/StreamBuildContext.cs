using System;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Per-stream build coordinate context (1 stream = 1 connection = 1 build state).
    /// </summary>
    public sealed class StreamBuildContext
    {
        public string StreamId { get; }
        public string Dimension { get; set; }
        public Int3 Origin { get; set; }

        public StreamBuildContext(string streamId, string dimension = WorldConstants.DefaultDimension, Int3? origin = null)
        {
            if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required.", nameof(streamId));
            StreamId = streamId;
            Dimension = dimension;
            Origin = origin ?? Int3.Zero;
        }
    }
}

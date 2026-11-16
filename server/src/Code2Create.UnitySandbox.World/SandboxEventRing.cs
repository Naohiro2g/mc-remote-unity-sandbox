using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Thread-safe, connection-epoch-scoped non-destructive event ring aligned with wire §5.4 and chat-event-compat-v23.2.1.
    /// </summary>
    public sealed class SandboxEventRing
    {
        private readonly object gate = new object();
        private readonly Queue<Entry> entries = new Queue<Entry>();
        private readonly int maxEvents;
        private readonly int maxBytes;
        private readonly int maxResultBytes;

        private int retainedBytes;
        private long latestSequence;
        private long overflowDroppedTotal;
        private long capacityDroppedTotal;

        public SandboxEventRing(
            int maxEvents = WorldConstants.DefaultEventRingMaxEvents,
            int maxBytes = WorldConstants.DefaultEventRingMaxBytes,
            int maxResultBytes = WorldConstants.MaxEventPollResponseBytes)
        {
            if (maxEvents < 1 || maxBytes < 1 || maxResultBytes < 256)
            {
                throw new ArgumentException("Event ring limits must be positive (maxResultBytes >= 256).");
            }

            this.maxEvents = maxEvents;
            this.maxBytes = maxBytes;
            this.maxResultBytes = maxResultBytes;
        }

        public long LatestSequence
        {
            get { lock (gate) return latestSequence; }
        }

        public int RetainedCount
        {
            get { lock (gate) return entries.Count; }
        }

        public bool OfferChatPosted(string dimension, Int3 origin, string message)
        {
            if (string.IsNullOrEmpty(dimension)) throw new ArgumentException("dimension is required.", nameof(dimension));
            if (message == null) throw new ArgumentNullException(nameof(message));

            lock (gate)
            {
                long sequence = ++latestSequence;
                var ev = new JObject
                {
                    ["sequence"] = sequence,
                    ["type"] = "chat_posted",
                    ["dimension"] = dimension,
                    ["origin"] = new JArray(origin.X, origin.Y, origin.Z),
                    ["message"] = message
                };

                int bytes = JsonBytes(ev);
                if (bytes > maxBytes || bytes > maxResultBytes - 256)
                {
                    capacityDroppedTotal++;
                    return false;
                }

                while (entries.Count > 0 && (entries.Count >= maxEvents || retainedBytes + bytes > maxBytes))
                {
                    Entry removed = entries.Dequeue();
                    retainedBytes -= removed.Bytes;
                    overflowDroppedTotal++;
                }

                if (entries.Count >= maxEvents || retainedBytes + bytes > maxBytes)
                {
                    capacityDroppedTotal++;
                    return false;
                }

                entries.Enqueue(new Entry(sequence, ev, bytes));
                retainedBytes += bytes;
                return true;
            }
        }

        public JObject Poll(long afterSequence, int requestedLimit)
        {
            lock (gate)
            {
                if (afterSequence < 0 || afterSequence > latestSequence || requestedLimit < 1)
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[0]"));
                }

                var selected = new List<JObject>();
                long through = afterSequence;
                bool limitReached = false;

                foreach (Entry entry in entries)
                {
                    if (entry.Sequence <= afterSequence)
                    {
                        continue;
                    }
                    if (selected.Count >= requestedLimit)
                    {
                        limitReached = true;
                        break;
                    }
                    selected.Add((JObject)entry.Event.DeepClone());
                    through = entry.Sequence;
                }

                if (!limitReached && selected.Count < requestedLimit)
                {
                    through = latestSequence;
                }

                JObject result = BuildResult(selected, through);
                while (selected.Count > 0 && JsonBytes(result) > maxResultBytes)
                {
                    selected.RemoveAt(selected.Count - 1);
                    through = selected.Count == 0
                        ? afterSequence
                        : selected[selected.Count - 1]["sequence"]!.Value<long>();
                    result = BuildResult(selected, through);
                }

                return result;
            }
        }

        private JObject BuildResult(List<JObject> selected, long through)
        {
            var eventsArray = new JArray();
            for (int i = 0; i < selected.Count; i++)
            {
                eventsArray.Add(selected[i]);
            }

            return new JObject
            {
                ["events"] = eventsArray,
                ["through_sequence"] = through,
                ["latest_sequence"] = latestSequence,
                ["filtered_out"] = 0L,
                ["overflow_dropped_total"] = overflowDroppedTotal,
                ["capacity_dropped_total"] = capacityDroppedTotal,
                ["explicitly_discarded_total"] = 0L
            };
        }

        private static int JsonBytes(JObject obj) =>
            Encoding.UTF8.GetByteCount(obj.ToString(Formatting.None));

        private readonly struct Entry
        {
            public long Sequence { get; }
            public JObject Event { get; }
            public int Bytes { get; }

            public Entry(long sequence, JObject ev, int bytes)
            {
                Sequence = sequence;
                Event = ev;
                Bytes = bytes;
            }
        }
    }
}

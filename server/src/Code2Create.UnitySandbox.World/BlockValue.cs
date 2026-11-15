using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Canonical scalar value allowed inside a BlockSpec/BlockValue state dictionary (boolean, number, or string).
    /// </summary>
    public readonly struct BlockStateScalar : IEquatable<BlockStateScalar>
    {
        public enum ScalarKind { Boolean, Integer, Number, String }

        public ScalarKind Kind { get; }
        public bool BoolValue { get; }
        public long IntValue { get; }
        public double NumberValue { get; }
        public string? StringValue { get; }

        private BlockStateScalar(ScalarKind kind, bool b, long i, double d, string? s)
        {
            Kind = kind;
            BoolValue = b;
            IntValue = i;
            NumberValue = d;
            StringValue = s;
        }

        public static BlockStateScalar FromBoolean(bool value) => new BlockStateScalar(ScalarKind.Boolean, value, 0, 0, null);
        public static BlockStateScalar FromInteger(long value) => new BlockStateScalar(ScalarKind.Integer, false, value, value, null);
        public static BlockStateScalar FromDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentException("State scalar number must be finite.", nameof(value));
            }
            return new BlockStateScalar(ScalarKind.Number, false, 0, value, null);
        }

        public static BlockStateScalar FromString(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return new BlockStateScalar(ScalarKind.String, false, 0, 0, value);
        }

        public object ToClrObject() => Kind switch
        {
            ScalarKind.Boolean => BoolValue,
            ScalarKind.Integer => IntValue,
            ScalarKind.Number => NumberValue,
            ScalarKind.String => StringValue!,
            _ => throw new InvalidOperationException()
        };

        public JValue ToJValue() => Kind switch
        {
            ScalarKind.Boolean => new JValue(BoolValue),
            ScalarKind.Integer => new JValue(IntValue),
            ScalarKind.Number => new JValue(NumberValue),
            ScalarKind.String => new JValue(StringValue!),
            _ => throw new InvalidOperationException()
        };

        public bool IsNumeric => Kind == ScalarKind.Integer || Kind == ScalarKind.Number;

        /// <summary>
        /// Compares JSON numbers scale-independently (e.g. 3 == 3.0 == 3e0) while keeping string "3" distinct.
        /// </summary>
        public bool MatchesWireToken(JToken token, out object? rawClrValue)
        {
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    bool b = token.Value<bool>();
                    rawClrValue = b;
                    return Kind == ScalarKind.Boolean && BoolValue == b;

                case JTokenType.Integer:
                    long i = token.Value<long>();
                    rawClrValue = i;
                    if (Kind == ScalarKind.Integer) return IntValue == i;
                    if (Kind == ScalarKind.Number) return NumberValue == i;
                    return false;

                case JTokenType.Float:
                    double d = token.Value<double>();
                    rawClrValue = d;
                    if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                    if (Kind == ScalarKind.Integer) return d == IntValue;
                    if (Kind == ScalarKind.Number) return NumberValue == d;
                    return false;

                case JTokenType.String:
                    string s = token.Value<string>()!;
                    rawClrValue = s;
                    return Kind == ScalarKind.String && string.Equals(StringValue, s, StringComparison.Ordinal);

                default:
                    rawClrValue = null;
                    return false;
            }
        }

        public bool Equals(BlockStateScalar other)
        {
            if (Kind != other.Kind)
            {
                if (IsNumeric && other.IsNumeric)
                {
                    double left = Kind == ScalarKind.Integer ? IntValue : NumberValue;
                    double right = other.Kind == ScalarKind.Integer ? other.IntValue : other.NumberValue;
                    return left == right;
                }
                return false;
            }

            return Kind switch
            {
                ScalarKind.Boolean => BoolValue == other.BoolValue,
                ScalarKind.Integer => IntValue == other.IntValue,
                ScalarKind.Number => NumberValue == other.NumberValue,
                ScalarKind.String => string.Equals(StringValue, other.StringValue, StringComparison.Ordinal),
                _ => false
            };
        }

        public override bool Equals(object? obj) => obj is BlockStateScalar other && Equals(other);

        public override int GetHashCode() => Kind switch
        {
            ScalarKind.Boolean => HashCode.Combine(1, BoolValue),
            ScalarKind.Integer => HashCode.Combine(2, (double)IntValue),
            ScalarKind.Number => HashCode.Combine(2, NumberValue),
            ScalarKind.String => HashCode.Combine(3, StringValue),
            _ => 0
        };

        public override string ToString() => Kind switch
        {
            ScalarKind.Boolean => BoolValue ? "true" : "false",
            ScalarKind.Integer => IntValue.ToString(CultureInfo.InvariantCulture),
            ScalarKind.Number => NumberValue.ToString(CultureInfo.InvariantCulture),
            ScalarKind.String => StringValue!,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Canonical structured block value ({ block_id, state }) per wire-format-design_ja.md §7.1.
    /// </summary>
    public sealed class BlockValue : IEquatable<BlockValue>
    {
        private readonly string canonicalKey;

        public string BlockId { get; }
        public IReadOnlyDictionary<string, BlockStateScalar> State { get; }

        public BlockValue(string blockId, IReadOnlyDictionary<string, BlockStateScalar>? state = null)
        {
            if (string.IsNullOrEmpty(blockId)) throw new ArgumentException("blockId is required.", nameof(blockId));
            BlockId = blockId;

            var sorted = new SortedDictionary<string, BlockStateScalar>(StringComparer.Ordinal);
            if (state != null)
            {
                foreach (var kv in state)
                {
                    sorted[kv.Key] = kv.Value;
                }
            }

            State = sorted;
            canonicalKey = BuildCanonicalJson(BlockId, sorted);
        }

        public JObject ToJson()
        {
            var stateObj = new JObject();
            foreach (var kv in State)
            {
                stateObj[kv.Key] = kv.Value.ToJValue();
            }

            return new JObject
            {
                ["block_id"] = BlockId,
                ["state"] = stateObj
            };
        }

        public string ToCanonicalJson() => canonicalKey;

        private static string BuildCanonicalJson(string blockId, SortedDictionary<string, BlockStateScalar> state)
        {
            var sb = new StringBuilder(64);
            sb.Append("{\"block_id\":");
            sb.Append(JsonConvert.ToString(blockId));
            sb.Append(",\"state\":{");
            bool first = true;
            foreach (var kv in state)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonConvert.ToString(kv.Key));
                sb.Append(':');
                sb.Append(kv.Value.ToJValue().ToString(Formatting.None));
            }
            sb.Append("}}");
            return sb.ToString();
        }

        public bool Equals(BlockValue? other) =>
            other is not null && string.Equals(canonicalKey, other.canonicalKey, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is BlockValue other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(canonicalKey);
        public override string ToString() => canonicalKey;
    }
}

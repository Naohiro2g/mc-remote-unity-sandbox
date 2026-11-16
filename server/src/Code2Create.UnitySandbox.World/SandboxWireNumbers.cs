using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Wire numeric canonicalization aligned with wire-format-design_ja.md §5.0.1 and §5.8.2 (DECISION 2026-08-19-01).
    /// Positions round HALF_UP to 3 decimals, angles to 2 decimals with yaw in [-180, 180), and directions to 6 decimals.
    /// </summary>
    public static class SandboxWireNumbers
    {
        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public static void RequireFinite(double value, string? path = null)
        {
            if (!IsFinite(value))
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }
        }

        public static double NormalizeYaw(double value, string? path = null)
        {
            RequireFinite(value, path);
            double normalized = value % 360.0;
            if (normalized >= 180.0)
            {
                normalized -= 360.0;
            }
            else if (normalized < -180.0)
            {
                normalized += 360.0;
            }
            return normalized == 0.0 ? 0.0 : normalized;
        }

        public static void RequirePitch(double value, string? path = null)
        {
            RequireFinite(value, path);
            if (value < -90.0 || value > 90.0)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }
        }

        public static JValue Position(double value) => DecimalToken(value, 3);

        public static JValue Direction(double value) => DecimalToken(value, 6);

        public static JValue Pitch(double value) => DecimalToken(value, 2);

        public static JValue Yaw(double value)
        {
            double normalized = NormalizeYaw(value);
            decimal rounded = RoundHalfUp(normalized, 2);
            if (rounded >= 180m)
            {
                rounded -= 360m;
            }
            return ToCanonicalJValue(rounded);
        }

        /// <summary>
        /// Formats position coordinates safely for diagnostic error payloads even when very large finite inputs exceed decimal range.
        /// </summary>
        public static object PositionOrRawDouble(double value)
        {
            if (TryRoundHalfUp(value, 3, out decimal rounded))
            {
                return ToCanonicalClrNumber(rounded);
            }
            return value == 0.0 ? 0L : value;
        }

        private static JValue DecimalToken(double value, int scale)
        {
            decimal rounded = RoundHalfUp(value, scale);
            return ToCanonicalJValue(rounded);
        }

        private static decimal RoundHalfUp(double value, int scale)
        {
            RequireFinite(value);
            if (!TryRoundHalfUp(value, scale, out decimal rounded))
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams());
            }
            return rounded;
        }

        private static bool TryRoundHalfUp(double value, int scale, out decimal rounded)
        {
            rounded = 0m;
            if (!IsFinite(value)) return false;
            try
            {
                decimal dec = decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
                rounded = Math.Round(dec, scale, MidpointRounding.AwayFromZero);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static JValue ToCanonicalJValue(decimal rounded)
        {
            if (rounded == 0m)
            {
                return new JValue(0L);
            }

            decimal stripped = rounded / 1.0000000000000000000000000000m;
            if (stripped == decimal.Truncate(stripped) && stripped >= long.MinValue && stripped <= long.MaxValue)
            {
                return new JValue((long)stripped);
            }

            return new JValue(stripped);
        }

        private static object ToCanonicalClrNumber(decimal rounded)
        {
            if (rounded == 0m)
            {
                return 0L;
            }

            decimal stripped = rounded / 1.0000000000000000000000000000m;
            if (stripped == decimal.Truncate(stripped) && stripped >= long.MinValue && stripped <= long.MaxValue)
            {
                return (long)stripped;
            }

            return stripped;
        }
    }
}

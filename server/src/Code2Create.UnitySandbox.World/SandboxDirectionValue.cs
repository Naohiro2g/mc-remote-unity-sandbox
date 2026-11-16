using System;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Scale-safe unit direction vector and Minecraft yaw/pitch conversions aligned with wire §5.8.2.
    /// </summary>
    public readonly struct SandboxDirectionUnit
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        private SandboxDirectionUnit(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static SandboxDirectionUnit Normalize(double x, double y, double z)
        {
            SandboxWireNumbers.RequireFinite(x);
            SandboxWireNumbers.RequireFinite(y);
            SandboxWireNumbers.RequireFinite(z);

            double scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
            if (scale == 0.0)
            {
                throw new SandboxOperationException(SandboxProtocolError.ZeroDirection());
            }

            double scaledX = x / scale;
            double scaledY = y / scale;
            double scaledZ = z / scale;
            double norm = Math.Sqrt(scaledX * scaledX + scaledY * scaledY + scaledZ * scaledZ);
            return new SandboxDirectionUnit(scaledX / norm, scaledY / norm, scaledZ / norm);
        }

        public static SandboxDirectionUnit FromYawPitch(double mcYawDegrees, double mcPitchDegrees)
        {
            SandboxWireNumbers.RequireFinite(mcYawDegrees);
            SandboxWireNumbers.RequireFinite(mcPitchDegrees);

            double yawRad = mcYawDegrees * (Math.PI / 180.0);
            double pitchRad = mcPitchDegrees * (Math.PI / 180.0);
            double xz = Math.Cos(pitchRad);
            double dx = -xz * Math.Sin(yawRad);
            double dy = -Math.Sin(pitchRad);
            double dz = xz * Math.Cos(yawRad);
            return Normalize(dx, dy, dz);
        }

        public double ToYawDegrees()
        {
            double deg = Math.Atan2(-X, Z) * (180.0 / Math.PI);
            return SandboxWireNumbers.NormalizeYaw(deg);
        }

        public double ToPitchDegrees()
        {
            double hypot = Math.Sqrt(X * X + Z * Z);
            double deg = Math.Atan2(-Y, hypot) * (180.0 / Math.PI);
            return deg == 0.0 ? 0.0 : deg;
        }

        public JArray ToWireJson() => new JArray(
            SandboxWireNumbers.Direction(X),
            SandboxWireNumbers.Direction(Y),
            SandboxWireNumbers.Direction(Z));
    }
}

using System;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    public enum PlayerAccessStatus
    {
        Ok,
        AuthRequired,
        PlayerOffline
    }

    /// <summary>
    /// Canonical continuous viewer state synchronized with the paired Unity Web viewer.
    /// Coordinates are absolute world coordinates (Feet) using Minecraft yaw/pitch conventions.
    /// </summary>
    public readonly struct SandboxPlayerState
    {
        public string Dimension { get; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double Yaw { get; }
        public double Pitch { get; }

        public SandboxPlayerState(string dimension, double x, double y, double z, double yaw, double pitch)
        {
            Dimension = string.IsNullOrEmpty(dimension) ? WorldConstants.DefaultDimension : dimension;
            X = x;
            Y = y;
            Z = z;
            Yaw = yaw;
            Pitch = pitch;
        }

        public JObject ToPositionResultJson(Int3 origin) => new JObject
        {
            ["dimension"] = Dimension,
            ["pos"] = new JArray(
                SandboxWireNumbers.Position(X - origin.X),
                SandboxWireNumbers.Position(Y - origin.Y),
                SandboxWireNumbers.Position(Z - origin.Z))
        };

        public JObject ToPoseResultJson(Int3 origin)
        {
            var obj = ToPositionResultJson(origin);
            obj["yaw"] = SandboxWireNumbers.Yaw(Yaw);
            obj["pitch"] = SandboxWireNumbers.Pitch(Pitch);
            return obj;
        }
    }

    /// <summary>
    /// Abstraction allowing pure World dispatch logic to read/update the bound viewer's pose without depending on ASP.NET/WebSockets.
    /// </summary>
    public interface ISandboxPlayerBridge
    {
        PlayerAccessStatus TryGetPlayerState(out SandboxPlayerState state);
        PlayerAccessStatus TrySetPlayerPosition(string dimension, double absX, double absY, double absZ, out SandboxPlayerState updated);
        PlayerAccessStatus TrySetPlayerPose(string dimension, double absX, double absY, double absZ, double normalizedYaw, double pitch, out SandboxPlayerState updated);
        PlayerAccessStatus TrySetPlayerRotation(double normalizedYaw, double pitch, out SandboxPlayerState updated);
    }
}

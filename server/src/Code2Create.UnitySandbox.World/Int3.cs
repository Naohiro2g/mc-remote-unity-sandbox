using System;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Immutable 3D integer coordinate independent of UnityEngine.
    /// </summary>
    public readonly struct Int3 : IEquatable<Int3>
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public Int3(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Int3 Zero => new Int3(0, 0, 0);

        public int[] ToArray() => new[] { X, Y, Z };

        public static Int3 operator +(Int3 a, Int3 b) => new Int3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Int3 operator -(Int3 a, Int3 b) => new Int3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public bool Equals(Int3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is Int3 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public static bool operator ==(Int3 left, Int3 right) => left.Equals(right);
        public static bool operator !=(Int3 left, Int3 right) => !left.Equals(right);
        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}

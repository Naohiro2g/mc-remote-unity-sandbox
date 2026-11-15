using System;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Inclusive axis-aligned 3D integer bounding box.
    /// </summary>
    public readonly struct WorldBox : IEquatable<WorldBox>
    {
        public Int3 Min { get; }
        public Int3 Max { get; }

        public WorldBox(Int3 min, Int3 max)
        {
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            {
                throw new ArgumentException("Box min must not exceed max on any axis.");
            }

            Min = min;
            Max = max;
        }

        public static WorldBox FromEndpoints(Int3 a, Int3 b)
        {
            return new WorldBox(
                new Int3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
                new Int3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        }

        public static WorldBox SingleCell(Int3 cell) => new WorldBox(cell, cell);

        public long SizeX => (long)Max.X - Min.X + 1;
        public long SizeY => (long)Max.Y - Min.Y + 1;
        public long SizeZ => (long)Max.Z - Min.Z + 1;
        public long Volume => SizeX * SizeY * SizeZ;

        public bool Contains(Int3 p) =>
            p.X >= Min.X && p.X <= Max.X &&
            p.Y >= Min.Y && p.Y <= Max.Y &&
            p.Z >= Min.Z && p.Z <= Max.Z;

        public bool Equals(WorldBox other) => Min.Equals(other.Min) && Max.Equals(other.Max);
        public override bool Equals(object? obj) => obj is WorldBox other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Min, Max);
        public static bool operator ==(WorldBox left, WorldBox right) => left.Equals(right);
        public static bool operator !=(WorldBox left, WorldBox right) => !left.Equals(right);
        public override string ToString() => $"[{Min} .. {Max}]";
    }
}

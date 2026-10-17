using System;

namespace CombatSimulation.Physics
{
    // Engine-free 2D vector. Plain scalar code, so .NET JIT and IL2CPP give the same results.
    public struct Vec2 : IEquatable<Vec2>
    {
        public float X;
        public float Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 Zero => default;

        public float LengthSquared => X * X + Y * Y;
        public float Length => (float)Math.Sqrt(X * X + Y * Y);

        // Unit vector, or zero when the vector is too short to normalize.
        public Vec2 Normalized
        {
            get
            {
                var length = Length;
                if (length < Epsilon) return default;
                var inv = 1f / length;
                return new Vec2(X * inv, Y * inv);
            }
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Y * s);

        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

        // z component of the 3D cross product.
        public static float Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

        // Cross product of a scalar (z axis) and a vector: s × v.
        public static Vec2 Cross(float s, Vec2 v) => new Vec2(-s * v.Y, s * v.X);

        // Cross product of a vector and a scalar (z axis): v × s.
        public static Vec2 Cross(Vec2 v, float s) => new Vec2(s * v.Y, -s * v.X);

        // Counter-clockwise perpendicular.
        public static Vec2 LeftPerp(Vec2 v) => new Vec2(-v.Y, v.X);

        // Clockwise perpendicular.
        public static Vec2 RightPerp(Vec2 v) => new Vec2(v.Y, -v.X);

        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
        public static Vec2 Min(Vec2 a, Vec2 b) => new Vec2(a.X < b.X ? a.X : b.X, a.Y < b.Y ? a.Y : b.Y);
        public static Vec2 Max(Vec2 a, Vec2 b) => new Vec2(a.X > b.X ? a.X : b.X, a.Y > b.Y ? a.Y : b.Y);
        public static float DistanceSquared(Vec2 a, Vec2 b) => (b - a).LengthSquared;

        public bool IsFinite => !float.IsNaN(X) && !float.IsInfinity(X) && !float.IsNaN(Y) && !float.IsInfinity(Y);

        public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is Vec2 other && Equals(other);
        public override int GetHashCode() => (X.GetHashCode() * 397) ^ Y.GetHashCode();
        public override string ToString() => $"({X}, {Y})";

        private const float Epsilon = 1e-12f;
    }
}

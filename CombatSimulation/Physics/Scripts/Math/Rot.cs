using System;
using System.Runtime.CompilerServices;

namespace CombatSimulation.Physics
{
    // Rotation stored as cosine and sine. Angles are in radians.
    public struct Rot
    {
        public float C;
        public float S;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Rot(float c, float s)
        {
            C = c;
            S = s;
        }

        public static Rot Identity => new Rot(1f, 0f);

        public static Rot FromAngle(float radians) => new Rot((float)Math.Cos(radians), (float)Math.Sin(radians));

        public float Angle => (float)Math.Atan2(S, C);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec2 Rotate(Vec2 v) => new Vec2(C * v.X - S * v.Y, S * v.X + C * v.Y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec2 InvRotate(Vec2 v) => new Vec2(C * v.X + S * v.Y, -S * v.X + C * v.Y);

        // q * r: apply r, then q.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Rot Mul(Rot q, Rot r) => new Rot(q.C * r.C - q.S * r.S, q.S * r.C + q.C * r.S);

        // inverse(q) * r: rotation r seen from frame q.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Rot InvMul(Rot q, Rot r) => new Rot(q.C * r.C + q.S * r.S, q.C * r.S - q.S * r.C);

        // Adds a small angle and normalizes. Adapted from Box2D v3 (MIT, Erin Catto), b2IntegrateRotation.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Rot Integrate(Rot q, float deltaAngle)
        {
            var c = q.C - deltaAngle * q.S;
            var s = q.S + deltaAngle * q.C;
            return Normalize(c, s);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Rot Normalize(float c, float s)
        {
            var length = (float)Math.Sqrt(c * c + s * s);
            if (length <= 0f) return Identity;
            var inv = 1f / length;
            return new Rot(c * inv, s * inv);
        }

        public override string ToString() => $"{Angle} rad";
    }
}

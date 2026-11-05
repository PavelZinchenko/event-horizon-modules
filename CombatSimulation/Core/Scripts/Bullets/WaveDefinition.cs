using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy ArcView: the apex faces +X; the chord and wings trail behind the bullet origin.
    public sealed class WaveDefinition
    {
        public WaveDefinition(float margins, float deformation)
        {
            if (!(margins >= 0f && margins < 1f)) throw new ArgumentOutOfRangeException(nameof(margins));
            if (!(deformation > -1f) || float.IsInfinity(deformation))
                throw new ArgumentOutOfRangeException(nameof(deformation));
            MaxAngle = (float)(Math.PI * 0.5) * (1f - margins);
            Deformation = deformation;
            var vertices = new Vec2[Polygon.MaxVertices];
            for (var i = 0; i < vertices.Length; ++i) vertices[i] = ArcPoint((float)i / (vertices.Length - 1));
            Shape = Polygon.FromPoints(vertices);
        }

        public float MaxAngle { get; }
        public float Deformation { get; }
        public Polygon Shape { get; }

        public Vec2 ArcPoint(float share)
        {
            var angle = MaxAngle * (2f * share - 1f);
            return new Vec2((float)Math.Cos(angle) - 1f, (float)Math.Sin(angle));
        }

        public float Growth(float elapsed, float lifetime) =>
            1f + Deformation * (lifetime > 0f ? Math.Max(0f, Math.Min(1f, elapsed / lifetime)) : 1f);
    }

    internal static class Waves
    {
        public static void Resize(PhysicsWorld physics, ref BulletEntity bullet, long tick)
        {
            if (bullet.Definition.Wave == null) return;
            var elapsed = (tick - bullet.SpawnTick) * Battle.TickDuration;
            bullet.WaveScale = bullet.Definition.Scale * bullet.Definition.Wave.Growth(elapsed, bullet.FlightLifetime);
            physics.SetBulletPolygonScale(bullet.Body, bullet.WaveScale);
        }

        // Size at the start of this tick. The same age rule handles local shots, children and late owner records.
        public static void Update(BattleWorld world, long tick)
        {
            var bullets = world.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
            {
                if (!bullets.IsAlive(i)) continue;
                ref var bullet = ref bullets[i];
                if (bullet.IsBeam || bullet.Definition.Wave == null) continue;
                Resize(world.Physics, ref bullet, tick);
            }
        }
    }
}

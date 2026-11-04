using System;

namespace CombatSimulation.Core
{
    // Legacy AuraController (phase 5, step 4g). At the end of stage 5, after the spawns and before the physics, each aura
    // grows by one tick of its growth time, up to its full radius. Its size never exceeds the share of its lifetime that
    // is left before this tick's step (legacy Lifetime.Value before Lifetime.Take), so it shrinks when its weapon stops
    // renewing the lifetime. Physics takes the new radius; at size 0, in the last tick of its lifetime, it touches nothing.
    internal static class Auras
    {
        // Legacy Lifetime.Restore at stage 2: the lifetime runs again from this tick, as after a spawn in it.
        public static void Renew(ref BulletEntity bullet, long tick)
        {
            bullet.RenewTick = tick;
            bullet.ExpiryTick = tick + bullet.Definition.LifetimeTicks - 1;
        }

        public static void Update(BattleWorld world, long tick)
        {
            var bullets = world.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
            {
                if (!bullets.IsAlive(i)) continue;
                ref var bullet = ref bullets[i];
                if (bullet.IsBeam || !bullet.Definition.IsAura) continue;

                var growth = bullet.Definition.AuraGrowthTicks;
                var left = growth - (tick - bullet.RenewTick);
                bullet.AuraTicks = (int)Math.Max(0L, Math.Min(Math.Min(bullet.AuraTicks + 1, growth), left));
                if (bullet.AuraTicks > 0) world.Physics.SetBulletRadius(bullet.Body, Radius(bullet.Definition, bullet.AuraTicks));
            }
        }

        // The size of an aura created at the present tick, before its growth in it: the ticks it grew on the owner since
        // its spawn tick, held by its weapon.
        public static int StartTicks(BulletDefinition definition, long spawnTick, long tick) =>
            (int)Math.Max(0L, Math.Min(tick - spawnTick, definition.AuraGrowthTicks));

        // The share of the full size, legacy power.
        public static float Power(in BulletEntity bullet) =>
            bullet.Definition != null && bullet.Definition.IsAura ? (float)bullet.AuraTicks / bullet.Definition.AuraGrowthTicks : 1f;

        // A physics circle needs a positive radius; size 0 keeps the smallest one and takes no contacts.
        public static float Radius(BulletDefinition definition, int ticks) =>
            definition.Radius * Math.Max(ticks, 1) / definition.AuraGrowthTicks;
    }
}

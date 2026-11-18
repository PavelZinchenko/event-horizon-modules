using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One damage zone of the battle setup (exploration objects; legacy CreatePlanetaryGasCloud): a still circle that deals
    // damage per second of one type to every hostile ship, drone, missile and asteroid in it. The game scales the damage;
    // the target's resistances apply as for any bullet.
    public sealed class ZoneSetup
    {
        public ZoneSetup(long tag, Vec2 position, float radius, int damageType, float damagePerSecond,
            Allegiance allegiance = Allegiance.Enemy)
        {
            if (!position.IsFinite) throw new ArgumentOutOfRangeException(nameof(position), "The position must be finite.");
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius), "The radius must be finite and positive.");
            if (!(damagePerSecond >= 0f) || float.IsInfinity(damagePerSecond))
                throw new ArgumentOutOfRangeException(nameof(damagePerSecond), "The damage must be finite and not negative.");
            if (!Allegiances.IsValid(allegiance))
                throw new ArgumentOutOfRangeException(nameof(allegiance), "Unknown allegiance.");

            Tag = tag;
            Position = position;
            Allegiance = allegiance;
            Definition = BulletDefinition.Zone(radius, damageType, damagePerSecond);
        }

        // Opaque to the core: the mode maps it to its objective. BulletState.ZoneTag carries it.
        public long Tag { get; }
        public Vec2 Position { get; }
        public float Radius => Definition.Radius;
        public int DamageType => Definition.DamageType;
        public float DamagePerSecond => Definition.Damage;

        // Legacy UnitSide.Enemy: the zone damages the player and its allies.
        public Allegiance Allegiance { get; }

        internal BulletDefinition Definition { get; }
    }

    // The battle creates the setup's zones before tick 0, after the asteroids. A zone is a bullet without a ship: no
    // parent, no unit credit, no device. Its body never moves and stage 11 never removes it (expiry long.MaxValue). Its
    // damage goes through the contact pipeline as for any OverTime bullet.
    internal static class Zones
    {
        public static void Create(BattleWorld world)
        {
            var setup = world.Setup;
            for (var i = 0; i < setup.Zones.Count; ++i)
            {
                var zone = setup.Zones[i];
                var id = SpawnIdentity.CreateZone(setup.BattleId, (uint)i);
                var index = world.Bullets.Allocate();
                if (!world.TryRegister(id, world.Bullets.GetHandle(index)))
                {
                    world.Bullets.Release(index);
                    continue;
                }

                var definition = zone.Definition;
                var body = world.Physics.CreateBullet(new BulletDef
                {
                    Position = zone.Position,
                    PassThrough = true,
                    Filter = CollisionFilters.Bullet(definition, zone.Allegiance),
                }, new Circle(Vec2.Zero, definition.Radius));

                ref var bullet = ref world.Bullets[index];
                bullet.Id = id;
                bullet.Allegiance = zone.Allegiance;
                // A local owner, as an asteroid's: a battle with zones has no remote owners (BattleSetup).
                bullet.Owner = setup.Units.Count > 0 ? setup.Units[0].Owner : 0;
                bullet.DeviceIndex = -1;
                bullet.Definition = definition;
                bullet.SpeedScale = 1f;
                bullet.ExpiryTick = long.MaxValue;
                bullet.Confirmed = true;
                bullet.ZoneTag = zone.Tag;
                world.Bullets.SetBody(index, body);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Phase 5, step 11c. Stage 6: each running gravity wave finds the units in its circle, as the point-defense query does,
    // and adds one contact per unit that it has not touched in this use. Stage 9 destroys a bullet and pushes the rest.
    internal sealed class GravityWaveQueries
    {
        public void Run(Battle battle)
        {
            _candidates.Clear();
            for (var s = 0; s < battle.ShipEntryCount; ++s)
            {
                var ship = battle.GetShipEntry(s);
                if (ship == null) continue;
                for (var d = 0; d < ship.DeviceCount; ++d)
                    if (ship.GetDevice(d) is GravityGeneratorDevice device && device.IsWaving(battle.Tick))
                        Query(battle, ship, device);
            }
        }

        public void Collect(ContactResolver contacts)
        {
            for (var i = 0; i < _candidates.Count; ++i) contacts.Add(_candidates[i]);
        }

        private void Query(Battle battle, ShipEntity ship, GravityGeneratorDevice device)
        {
            var world = battle.World;
            var origin = device.Origin;
            int count;
            while (true)
            {
                count = world.OverlapCircle(origin, device.Definition.Range, CollisionFilters.GravityWave, _hits, out var overflow);
                if (!overflow) break;
                Array.Resize(ref _hits, _hits.Length * 2);
            }

            var first = _candidates.Count;
            _units.Clear();
            for (var h = 0; h < count; ++h)
            {
                var target = _hits[h].Target;
                if (target.IsBullet)
                {
                    if (!battle.Bullets.TryGetByBody(target.Bullet, out var index)) continue;
                    ref readonly var bullet = ref battle.Bullets[index];
                    // A beam has no collider in legacy; the bullets attached to the own ship are its parts.
                    if (bullet.IsBeam || bullet.Definition.IsBeamBolt || bullet.PendingRemoval != RemovalReason.None ||
                        bullet.ParentId == ship.Id && bullet.Definition.AttachedToParent || device.HasTouched(bullet.Id))
                        continue;
                    var point = world.GetBulletPosition(bullet.Body);
                    Add(ship, device, bullet.Id, battle.Bullets.GetHandle(index), point, Vec2.Zero);
                    continue;
                }

                // Legacy shield units drop the push (10d finding 3). An energy shield takes the hull's contacts, so its
                // ship is not pushed; a frontal shield leaves them, so its hull is. One contact per unit, also with
                // several hull parts or torus images.
                if (!battle.Shapes.TryGet(target.Shape, out var shape) || shape.Priority == ShapeHitPriority.Shield ||
                    shape.OwnerId == ship.Id || device.HasTouched(shape.OwnerId) || _units.Contains(shape.OwnerId)) continue;
                BodyId body;
                switch (shape.Owner.Kind)
                {
                    case EntityKind.Ship:
                        var unit = battle.FindShip(shape.OwnerId);
                        if (unit == null) continue;
                        body = unit.Body;
                        break;
                    case EntityKind.Segment:
                        var segment = battle.FindSegment(shape.OwnerId);
                        if (segment == null || segment.ShipId == ship.Id) continue;
                        body = segment.Body;
                        break;
                    case EntityKind.RepairBot:
                        var bot = battle.FindRepairBot(shape.OwnerId);
                        if (bot == null || bot.ShipId == ship.Id) continue;
                        body = bot.Body;
                        break;
                    case EntityKind.Asteroid:
                        var asteroid = battle.Asteroids.Get(shape.Owner);
                        if (asteroid == null) continue;
                        body = asteroid.Body;
                        break;
                    default:
                        continue;
                }

                var position = world.GetPosition(body);
                var impulse = device.Definition.Power * (float)Math.Sqrt(world.GetMass(body)) *
                              world.Torus.Delta(origin, position).Normalized;
                _units.Add(shape.OwnerId);
                Add(ship, device, shape.OwnerId, shape.Owner, position, impulse);
            }

            for (var i = first; i < _candidates.Count; ++i) device.Touch(_candidates[i].Target);
        }

        private void Add(ShipEntity ship, GravityGeneratorDevice device, EntityId target, EntityHandle handle, Vec2 point,
            Vec2 impulse)
        {
            _candidates.Add(new ContactCandidate
            {
                Time = 0f,
                Kind = ContactKind.Hit,
                Source = ContactSource.Overlap,
                Attacker = ship.Id,
                AttackerHandle = ship.Handle,
                GravityWave = device,
                AttackerPassThrough = true,
                Target = target,
                TargetHandle = handle,
                TargetPriority = ShapeHitPriority.Hull,
                Point = point,
                Impulse = impulse,
            });
        }

        private OverlapHit[] _hits = new OverlapHit[32];
        private readonly List<ContactCandidate> _candidates = new List<ContactCandidate>(32);
        private readonly HashSet<EntityId> _units = new HashSet<EntityId>();
    }
}

using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Queries only gather candidates. Energy and disarms are committed in contact order at stage 9.
    internal sealed class PointDefenseQueries
    {
        public void Run(Battle battle)
        {
            _candidates.Clear();
            for (var s = 0; s < battle.ShipEntryCount; ++s)
            {
                var ship = battle.GetShipEntry(s);
                if (ship == null) continue;
                for (var d = 0; d < ship.DeviceCount; ++d)
                {
                    if (!(ship.GetDevice(d) is PointDefenseDevice device) || !device.PrepareQuery(ship, battle.Tick))
                        continue;

                    var center = battle.World.GetPosition(ship.Body);
                    var filter = CollisionFilters.PointDefense(ship.Side);
                    int count;
                    while (true)
                    {
                        count = battle.World.OverlapCircle(center, device.Definition.Radius, filter, _hits, out var overflow);
                        if (!overflow) break;
                        Array.Resize(ref _hits, _hits.Length * 2);
                    }

                    // Each body is one drone, even with multiple hull parts or torus images.
                    _droneTargets.Clear();
                    for (var h = 0; h < count; ++h)
                    {
                        var target = _hits[h].Target;
                        if (!target.IsBullet)
                        {
                            if (!battle.Shapes.TryGet(target.Shape, out var shape)) continue;
                            var drone = battle.FindShip(shape.OwnerId);
                            if (drone == null || !drone.IsDrone || drone.Side == ship.Side ||
                                !_droneTargets.Add(drone.Id)) continue;
                            _candidates.Add(new ContactCandidate
                            {
                                Time = 0f,
                                Kind = ContactKind.Hit,
                                Source = ContactSource.Overlap,
                                Attacker = ship.Id,
                                AttackerHandle = ship.Handle,
                                PointDefense = device,
                                AttackerPassThrough = true,
                                Target = drone.Id,
                                TargetHandle = drone.Handle,
                                TargetPriority = ShapeHitPriority.Hull,
                                Damage = device.Definition.DroneDamage,
                                Point = battle.World.GetPosition(drone.Body),
                            });
                            continue;
                        }
                        if (!battle.Bullets.TryGetByBody(target.Bullet, out var index)) continue;
                        ref readonly var bullet = ref battle.Bullets[index];
                        if (bullet.IsBeam || bullet.Side == ship.Side || bullet.PendingRemoval != RemovalReason.None) continue;
                        _candidates.Add(new ContactCandidate
                        {
                            Time = 0f,
                            Kind = ContactKind.Disarm,
                            Source = ContactSource.Overlap,
                            Attacker = ship.Id,
                            AttackerHandle = ship.Handle,
                            PointDefense = device,
                            AttackerPassThrough = true,
                            Target = bullet.Id,
                            TargetHandle = battle.Bullets.GetHandle(index),
                            TargetPriority = ShapeHitPriority.Hull,
                            Point = battle.World.GetBulletPosition(bullet.Body),
                        });
                    }
                }
            }
        }

        public void Collect(ContactResolver contacts)
        {
            for (var i = 0; i < _candidates.Count; ++i) contacts.Add(_candidates[i]);
        }

        private OverlapHit[] _hits = new OverlapHit[32];
        private readonly List<ContactCandidate> _candidates = new List<ContactCandidate>(32);
        private readonly HashSet<EntityId> _droneTargets = new HashSet<EntityId>();
    }
}

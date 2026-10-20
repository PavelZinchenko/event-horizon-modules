using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using GameDiagnostics;

namespace CombatSimulation.Core
{
    // Query at stage 6, collect at stage 8, then publish the first accepted contact at stage 9.
    internal sealed class BeamQueries
    {
        public int ExhaustedRayCount { get; private set; }

        public void Run(Battle battle)
        {
            _candidates.Clear();
            var bullets = battle.Bullets;
            var world = battle.World;
            for (var i = 0; i < bullets.Capacity; ++i)
            {
                if (!bullets.IsAlive(i)) continue;
                ref var beam = ref bullets[i];
                if (!beam.IsBeam || beam.PendingRemoval != RemovalReason.None) continue;
                var ship = battle.FindShip(beam.ParentId);
                if (ship == null) continue;

                var definition = beam.BeamDefinition;
                var barrel = ship.Definition.Barrels[definition.BarrelIndex];
                beam.BeamOrigin = world.Torus.Wrap(world.GetPosition(ship.Body) +
                    world.GetRotation(ship.Body).Rotate(barrel.Position));
                beam.BeamAngle = world.GetAngle(ship.Body) + barrel.Angle;
                var direction = new Vec2((float)Math.Cos(beam.BeamAngle), (float)Math.Sin(beam.BeamAngle));
                // Match PhysicsWorld's torus ray-length cap, including the no-hit endpoint.
                var range = Math.Min(definition.Range, Math.Min(world.Torus.Width, world.Torus.Height));
                var translation = range * direction;
                beam.BeamEndPoint = world.Torus.Wrap(beam.BeamOrigin + translation);
                beam.TargetId = default;
                var count = world.RayCast(beam.BeamOrigin, translation, CollisionFilters.Energy(beam.Side),
                    ship.Body, true, _hits, out beam.RayOverflow);

                for (var h = 0; h < count; ++h)
                {
                    ref readonly var hit = ref _hits[h];
                    var candidate = new ContactCandidate
                    {
                        Time = 0f,
                        Kind = ContactKind.Hit,
                        Source = ContactSource.Ray,
                        Attacker = beam.Id,
                        AttackerHandle = bullets.GetHandle(i),
                        RayFraction = hit.Fraction,
                        AttackerPassThrough = false,
                        OncePerTargetEntity = true,
                        Damage = definition.DamagePerSecond * Battle.TickDuration,
                        Point = hit.Point,
                    };

                    if (hit.Target.IsBullet)
                    {
                        if (!bullets.TryGetByBody(hit.Target.Bullet, out var targetIndex)) continue;
                        ref readonly var target = ref bullets[targetIndex];
                        if (!target.Definition.IsMissile) continue;
                        candidate.Target = target.Id;
                        candidate.TargetHandle = bullets.GetHandle(targetIndex);
                        candidate.TargetPriority = ShapeHitPriority.Hull;
                        candidate.TargetHitPoints = target.HitPoints;
                    }
                    else
                    {
                        if (!battle.Shapes.TryGet(hit.Target.Shape, out var target) ||
                            battle.FindShip(target.OwnerId) == null) continue;
                        candidate.Target = target.OwnerId;
                        candidate.TargetHandle = target.Owner;
                        candidate.TargetPriority = target.Priority;
                        candidate.TargetShapeOrder = target.Order;
                    }
                    _candidates.Add(candidate);
                }
            }
        }

        public void Collect(ContactResolver contacts)
        {
            for (var i = 0; i < _candidates.Count; ++i) contacts.Add(_candidates[i]);
        }

        public void Complete(Battle battle, ContactResolver contacts)
        {
            var bullets = battle.Bullets;
            foreach (ref readonly var hit in contacts.Accepted)
            {
                if (hit.Source != ContactSource.Ray || hit.Kind != ContactKind.Hit ||
                    !bullets.TryGet(hit.AttackerHandle, out var index)) continue;
                ref var beam = ref bullets[index];
                if (!beam.IsBeam) continue;
                beam.TargetId = hit.Target;
                beam.BeamEndPoint = hit.Point;
            }

            for (var i = 0; i < bullets.Capacity; ++i)
            {
                if (!bullets.IsAlive(i)) continue;
                ref readonly var beam = ref bullets[i];
                if (!beam.IsBeam || beam.PendingRemoval != RemovalReason.None ||
                    !beam.RayOverflow || !beam.TargetId.IsNull) continue;
                ++ExhaustedRayCount;
                Trace.LogWarning($"Battle {battle.BattleId}, tick {battle.Tick}: beam {beam.Id} exhausted its ray buffer.");
            }
        }

        private readonly RayHit[] _hits = new RayHit[8];
        private readonly List<ContactCandidate> _candidates = new List<ContactCandidate>(32);
    }
}

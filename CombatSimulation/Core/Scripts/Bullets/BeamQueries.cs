using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using GameDiagnostics;
using Utilites;

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
                var carrier = battle.CarrierBody(ship, definition.BarrelIndex);
                beam.BeamOrigin = world.Torus.Wrap(world.GetPosition(carrier) +
                    world.GetRotation(carrier).Rotate(barrel.PositionAt(ship.GetBarrel(definition.BarrelIndex).Angle)));
                beam.BeamAngle = Aim(battle, beam, world.GetAngle(carrier) + ship.GetBarrel(definition.BarrelIndex).Angle);
                var direction = new Vec2((float)Math.Cos(beam.BeamAngle), (float)Math.Sin(beam.BeamAngle));
                // Match PhysicsWorld's torus ray-length cap, including the no-hit endpoint.
                var range = Math.Min(definition.Range, Math.Min(world.Torus.Width, world.Torus.Height));
                var translation = range * direction;
                beam.BeamEndPoint = world.Torus.Wrap(beam.BeamOrigin + translation);
                // A tick without a contact ends the ramp (legacy RayCastCollider clears ActiveCollision).
                if (beam.TargetId.IsNull) beam.RampTargetId = default;
                beam.TargetId = default;
                var effects = definition.Effects;
                var count = world.RayCast(beam.BeamOrigin, translation, CollisionFilters.Energy(beam.Allegiance, definition.FriendlyFire),
                    ship.Body, true, _hits, out beam.RayOverflow);

                // Source exclusions must not hide the first valid target beyond the fixed ray buffer.
                while (definition.FriendlyFire && beam.RayOverflow)
                {
                    Array.Resize(ref _hits, _hits.Length * 2);
                    count = world.RayCast(beam.BeamOrigin, translation,
                        CollisionFilters.Energy(beam.Allegiance, true), ship.Body, true, _hits, out beam.RayOverflow);
                }
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
                        Damage = definition.DamagePerSecond * (definition.Impact == BulletImpact.OverTime ? Battle.TickDuration : 1f),
                        DamageType = definition.DamageType,
                        Point = hit.Point,
                    };

                    Allegiance targetAllegiance;
                    if (hit.Target.IsBullet)
                    {
                        if (!bullets.TryGetByBody(hit.Target.Bullet, out var targetIndex)) continue;
                        ref readonly var target = ref bullets[targetIndex];
                        if (!target.Definition.IsMissile || definition.FriendlyFire &&
                            !beam.SourceShipId.IsNull && target.SourceShipId == beam.SourceShipId) continue;
                        targetAllegiance = target.Definition.FriendlyFire ? Allegiance.Neutral : target.Allegiance;
                        candidate.Target = target.Id;
                        candidate.TargetHandle = bullets.GetHandle(targetIndex);
                        candidate.TargetPriority = ShapeHitPriority.Hull;
                        candidate.TargetHitPoints = target.HitPoints;
                    }
                    else
                    {
                        if (!battle.Shapes.TryGet(hit.Target.Shape, out var target) ||
                            (battle.FindShip(target.OwnerId) == null && battle.FindSegment(target.OwnerId) == null)) continue;
                        var targetShip = battle.FindShip(target.OwnerId);
                        var segment = targetShip == null ? battle.FindSegment(target.OwnerId) : null;
                        if (definition.FriendlyFire && CollisionFilters.IsSource(beam.SourceShipId, targetShip, segment)) continue;
                        targetAllegiance = targetShip?.Allegiance ?? segment.Allegiance;
                        candidate.Target = target.OwnerId;
                        candidate.TargetHandle = target.Owner;
                        candidate.TargetPriority = target.Priority;
                        candidate.TargetShapeOrder = target.Order;
                    }
                    // Legacy ProgressiveDamageAction (phase 5, step 5c): the ramp grows while the first body stays
                    // the same; a new first body starts at 0. Any target takes it, as damage of the beam's type.
                    if (effects.Ramp > 0f)
                        candidate.Damage += ImpactEffects.RampTick(effects.Ramp, effects.RampTime,
                            candidate.Target == beam.RampTargetId ? battle.Tick - beam.RampTick : 0);
                    candidate.Damage += effects.DamageAgainst(beam.Allegiance, targetAllegiance) *
                        (definition.Impact == BulletImpact.OverTime ? Battle.TickDuration : 1f);
                    if (beam.BeamHitTargets != null && beam.BeamHitTargets.Contains(candidate.Target))
                        candidate.Damage = 0f;
                    _candidates.Add(candidate);
                }
            }
        }

        // Phase 5, step 4d. Legacy LookAtTargetController: a tracking beam points at the centre of the target it touched
        // in the previous tick, at most TrackingAngle from the barrel; without one it turns by up to the full spread
        // either way. Legacy BeamController: up to half the spread either way. The draw is counter-based on the beam ID,
        // so a remote copy draws the same angle; no spread draws nothing.
        private static float Aim(Battle battle, in BulletEntity beam, float barrelAngle)
        {
            var definition = beam.BeamDefinition;
            if (definition.TrackingAngle > 0f && TryGetTargetPosition(battle, beam.TargetId, out var target))
            {
                var delta = battle.World.Torus.Delta(beam.BeamOrigin, target);
                if (delta.X != 0f || delta.Y != 0f)
                {
                    var turn = Battle.WrapAngle((float)Math.Atan2(delta.Y, delta.X) - barrelAngle);
                    return barrelAngle + Math.Max(-definition.TrackingAngle, Math.Min(definition.TrackingAngle, turn));
                }
            }

            if (definition.Spread <= 0f) return barrelAngle;
            var random = CounterRandom.Create(battle.Setup.MatchSeed, beam.Id, battle.Tick, RandomPurpose.BeamJitter);
            var draw = random.NextFloat();
            return definition.TrackingAngle > 0f
                ? barrelAngle + (2f * draw - 1f) * definition.Spread
                : barrelAngle + (draw - 0.5f) * definition.Spread;
        }

        // Legacy Body.WorldPosition of the touched unit: a ship, a tail segment or a missile.
        private static bool TryGetTargetPosition(Battle battle, EntityId id, out Vec2 position)
        {
            position = Vec2.Zero;
            if (id.IsNull) return false;
            var ship = battle.FindShip(id);
            if (ship != null)
            {
                position = battle.World.GetPosition(ship.Body);
                return true;
            }
            var segment = battle.FindSegment(id);
            if (segment != null)
            {
                position = battle.World.GetPosition(segment.Body);
                return true;
            }
            if (!battle.TryGetBullet(id, out var index)) return false;
            position = battle.World.GetBulletPosition(battle.Bullets[index].Body);
            return true;
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
                if (beam.RampTargetId != hit.Target)
                {
                    beam.RampTargetId = hit.Target;
                    beam.RampTick = battle.Tick;
                }
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

        private RayHit[] _hits = new RayHit[8];
        private readonly List<ContactCandidate> _candidates = new List<ContactCandidate>(32);
    }
}

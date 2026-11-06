using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Beam bolts (phase 5, step 4e; legacy MovingBeamController and RayCastCollider). A bolt is a projectile whose body
    // touches nothing: stage 4 launches it when its ray has grown, stage 6 casts its ray on the poses at the start of the
    // step, stage 8 adds the contacts and stage 9 shortens the view of a ray that stopped at a hit. Its contacts are
    // projectile contacts (BulletSweep, time 0): a FirstTarget bolt stops at its first valid hit and runs its Hit
    // triggers, an AllTargets bolt hits each unit once when the unit enters its ray, an OverTime bolt hits every tick.
    internal sealed class BeamBolts
    {
        // Legacy MovingBeamController: the bolt starts with the parent velocity only (its Initial state cancels the
        // bullet's own speed).
        public static float LaunchSpeed(BulletDefinition definition, float speedScale) =>
            definition.IsBeamBolt ? 0f : definition.Speed * speedScale * definition.StartingVelocityMultiplier;

        // Stage 4. In the tick its ray reaches full length the bolt takes its speed along its heading, on top of the
        // inherited velocity (legacy Body.ApplyAcceleration).
        public static void Update(PhysicsWorld world, ref BulletEntity bullet, long tick)
        {
            if (tick - bullet.SpawnTick != bullet.Definition.GrowthTicks(bullet.SpeedScale)) return;
            var heading = world.GetBulletRotation(bullet.Body);
            world.SetBulletVelocity(bullet.Body, world.GetBulletVelocity(bullet.Body) + bullet.Speed * new Vec2(heading.C, heading.S));
        }

        // The ray length at stage 6 of a tick. Legacy MovingBeamController: no ray in the spawn tick; then speed x age,
        // at most the length and the range; in flight the remaining lifetime x speed when that is shorter than the
        // length, else the length capped by the range of the growth (the legacy collider keeps it).
        public static float RayLength(in BulletEntity bullet, long tick)
        {
            var age = tick - bullet.SpawnTick;
            if (age <= 0) return 0f;
            var definition = bullet.Definition;
            if (age <= definition.GrowthTicks(bullet.SpeedScale))
                return Math.Min(Math.Min(bullet.Speed * age * Battle.TickDuration, definition.BeamLength), definition.Range);

            var left = bullet.Speed * (bullet.ExpiryTick - tick + 1) * Battle.TickDuration;
            return left < definition.BeamLength ? left : Math.Min(definition.BeamLength, definition.Range);
        }

        // Stage 6.
        public void Run(Battle battle)
        {
            _candidates.Clear();
            var entered = _previous;
            _previous = _current;
            _current = entered;
            _current.Clear();

            var bullets = battle.Bullets;
            var world = battle.World;
            for (var i = 0; i < bullets.Capacity; ++i)
            {
                if (!bullets.IsAlive(i)) continue;
                ref var bolt = ref bullets[i];
                if (bolt.IsBeam || !bolt.Definition.IsBeamBolt) continue;
                bolt.RayLength = 0f;
                if (bolt.PendingRemoval != RemovalReason.None) continue;

                var length = RayLength(bolt, battle.Tick);
                if (!(length > 0f)) continue;
                bolt.RayLength = length;

                var definition = bolt.Definition;
                var origin = world.GetBulletPosition(bolt.Body);
                var heading = world.GetBulletRotation(bolt.Body);
                var ship = battle.FindShip(bolt.ShipId);
                var count = world.RayCast(origin, length * new Vec2(heading.C, heading.S),
                    CollisionFilters.Energy(bolt.Allegiance, definition.FriendlyFire), ship?.Body ?? default, true, _hits, out var overflow);
                while (definition.FriendlyFire && overflow)
                {
                    Array.Resize(ref _hits, _hits.Length * 2);
                    count = world.RayCast(origin, length * new Vec2(heading.C, heading.S),
                        CollisionFilters.Energy(bolt.Allegiance, true), ship?.Body ?? default, true, _hits, out overflow);
                }

                // Legacy RayCastCollider: a ray without pass-through stops at the first unit it meets.
                var first = !definition.Piercing && definition.Impact != BulletImpact.FirstTarget;
                for (var h = 0; h < count; ++h)
                {
                    if (!TryCreate(battle, bullets.GetHandle(i), bolt, origin, _hits[h], out var candidate)) continue;
                    // An AllTargets bolt hits a unit when the unit enters its ray (legacy OnTriggerEnter, as the sweep).
                    if (definition.Impact == BulletImpact.AllTargets &&
                        (!_current.Add(new EntityPair(bolt.Id, candidate.Target)) ||
                         _previous.Contains(new EntityPair(bolt.Id, candidate.Target))))
                    {
                        if (first) break;
                        continue;
                    }
                    _candidates.Add(candidate);
                    if (first) break;
                }
            }
        }

        // One ray hit as a projectile contact; false for a target the bolt does not hit.
        private static bool TryCreate(Battle battle, EntityHandle handle, in BulletEntity bolt, Vec2 origin, in RayHit hit,
            out ContactCandidate candidate)
        {
            var definition = bolt.Definition;
            var world = battle.World;
            candidate = new ContactCandidate
            {
                Time = 0f,
                Kind = ContactKind.Hit,
                Source = ContactSource.BulletSweep,
                Attacker = bolt.Id,
                AttackerHandle = handle,
                RayFraction = hit.Fraction,
                AttackerPassThrough = definition.Impact != BulletImpact.FirstTarget,
                OncePerTargetEntity = definition.Impact != BulletImpact.FirstTarget,
                DamageType = definition.DamageType,
                Point = hit.Point,
            };

            Allegiance targetAllegiance;
            Vec2 targetPosition;
            float targetScale;
            var bullets = battle.Bullets;
            if (hit.Target.IsBullet)
            {
                // Only a missile takes damage from a bolt (BulletStore.CollectHits).
                if (!bullets.TryGetByBody(hit.Target.Bullet, out var targetIndex)) return false;
                ref readonly var target = ref bullets[targetIndex];
                if (!target.Definition.IsMissile || target.PendingRemoval != RemovalReason.None) return false;
                if (definition.FriendlyFire && !bolt.SourceShipId.IsNull && target.SourceShipId == bolt.SourceShipId) return false;
                targetAllegiance = target.Definition.FriendlyFire ? Allegiance.Neutral : target.Allegiance;
                candidate.Target = target.Id;
                candidate.TargetHandle = bullets.GetHandle(targetIndex);
                candidate.TargetPriority = ShapeHitPriority.Hull;
                candidate.TargetHitPoints = target.HitPoints;
                targetPosition = world.GetBulletPosition(target.Body);
                targetScale = target.Definition.Scale;
            }
            else
            {
                if (!battle.Shapes.TryGet(hit.Target.Shape, out var target)) return false;
                var ship = battle.FindShip(target.OwnerId);
                var segment = ship == null ? battle.FindSegment(target.OwnerId) : null;
                if (ship == null && segment == null) return false;
                if (definition.FriendlyFire && CollisionFilters.IsSource(bolt.SourceShipId, ship, segment)) return false;
                targetAllegiance = ship?.Allegiance ?? segment.Allegiance;
                candidate.Target = target.OwnerId;
                candidate.TargetHandle = target.Owner;
                candidate.TargetPriority = target.Priority;
                candidate.TargetShapeOrder = target.Order;
                targetPosition = world.GetPosition(ship?.Body ?? segment.Body);
                targetScale = ship?.Definition.Scale ?? segment.Scale;
            }

            candidate.Falloff = definition.HasFalloff
                ? definition.Falloff(world.Torus.Delta(origin, targetPosition).Length, targetScale)
                : 1f;
            candidate.Damage = BulletStore.TotalDamage(definition, candidate.Falloff) +
                definition.Effects.DamageAgainst(bolt.Allegiance, targetAllegiance) *
                (definition.Impact == BulletImpact.OverTime ? Battle.TickDuration : 1f);
            return true;
        }

        public void Collect(ContactResolver contacts)
        {
            for (var i = 0; i < _candidates.Count; ++i) contacts.Add(_candidates[i]);
        }

        // Stage 9. Legacy RayCastCollider.UpdateLength: a ray without pass-through ends at its first hit. The first
        // accepted contact of a bolt is its nearest (contact order). The snapshot draws the ray from the bolt's pose
        // after the step, so the cut is the hit point's distance ahead of that pose; 0 once the bolt has passed it, as
        // a legacy ray that starts inside its target.
        public void Complete(Battle battle, ContactResolver contacts)
        {
            var bullets = battle.Bullets;
            var world = battle.World;
            foreach (ref readonly var hit in contacts.Accepted)
            {
                if (hit.Source != ContactSource.BulletSweep || hit.Kind != ContactKind.Hit ||
                    !bullets.TryGet(hit.AttackerHandle, out var index)) continue;
                ref var bolt = ref bullets[index];
                if (bolt.IsBeam || !bolt.Definition.IsBeamBolt || bolt.Definition.Piercing) continue;
                var heading = world.GetBulletRotation(bolt.Body);
                var ahead = Vec2.Dot(world.Torus.Delta(world.GetBulletPosition(bolt.Body), hit.Point),
                    new Vec2(heading.C, heading.S));
                bolt.RayLength = Math.Max(0f, Math.Min(bolt.RayLength, ahead));
            }
        }

        private RayHit[] _hits = new RayHit[8];
        private readonly List<ContactCandidate> _candidates = new List<ContactCandidate>(32);

        // The (bolt, unit) pairs on the rays of AllTargets bolts in this tick and the one before.
        private HashSet<EntityPair> _current = new HashSet<EntityPair>();
        private HashSet<EntityPair> _previous = new HashSet<EntityPair>();
    }
}

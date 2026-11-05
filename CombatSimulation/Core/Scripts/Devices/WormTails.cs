using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Worm tails (legacy WormTailDeviceV2, WormSegmentFactory, WormSegmentBase and its subclasses; phase 5, step 7d).
    // Stage 2: the owner grows segments and runs the self-destruct timers. Stage 3: the attached share of each tail
    // gives the ship its resistance share. Stage 4: both sides apply the side damping and the keep-behind rule.
    // Stage 9: hits on segments queue in contact order. Stage 10: the owner applies them, before the ship damage pass.
    // Stage 12: dead segments leave after the drones.
    //
    // Network (H8): a segment is a ship-parent spawn with the device index as source and its number as spawn slot. The
    // owner sends a bullet spawn record per segment and a removal record per segment that leaves; the copy builds and
    // removes its segments from them and never decides a segment's growth or damage. A contact on a segment counts if
    // either the attacker's or the owner's device sees it, as a contact on a bullet does (network plan 9d).
    //
    // Pushes, the bullet contact ledger and the ramming tracker belong to ContactSystem; the tails reach them through
    // Battle (phase 5, step 8b, decision 1).
    internal sealed class WormTails
    {
        public WormTails(BattleWorld world, Battle battle)
        {
            _world = world;
            _battle = battle;
        }

        private BattleSetup Setup => _world.Setup;
        private long Tick => _world.Tick;
        private PhysicsWorld Physics => _world.Physics;

        public TailSegment FindSegment(EntityId id) =>
            _world.Entities.TryGet(id, out var handle) ? _world.Segments.Get(handle) : null;

        // Stage 3: legacy Length counts the segments of the chain from the head.
        public int CountAttachedSegments(WormTailDevice device)
        {
            var count = 0;
            for (var segment = _world.Segments.Get(device.Head); segment != null; segment = _world.Segments.Get(segment.Next))
                ++count;
            return count;
        }

        // Stage 2, from WormTailDevice.Update. The owner grows the head at the first update after deployment, then each
        // segment grows the next one when its delay has passed and the place is free (P4 rule). Recent damage falls.
        public void GrowTail(ShipEntity ship, WormTailDevice device)
        {
            if (!Setup.IsLocalOwner(ship.Owner)) return;

            var head = _world.Segments.Get(device.Head);
            if (head == null)
            {
                if (Tick >= device.HeadGrowthTick) TryGrowSegment(ship, device, null);
                return;
            }

            for (var segment = head; segment != null; segment = _world.Segments.Get(segment.Next))
            {
                Recover(segment);
                if (_world.Segments.Get(segment.Next) != null || segment.Number >= device.Definition.Size ||
                    Tick < segment.NextGrowthTick) continue;
                if (TryGrowSegment(ship, device, segment)) break;
            }
        }

        // Stage 2, on the owner: legacy InitiateSelfDestruct runs out; the recent damage of a detached segment falls too.
        public void UpdateDetachedSegments()
        {
            for (var i = 0; i < _world.Segments.Capacity; ++i)
            {
                var segment = _world.Segments[i];
                if (!segment.IsAlive || !segment.Detached || !Setup.IsLocalOwner(segment.Owner)) continue;
                if (Tick >= segment.SelfDestructTick) KillSegment(segment);
                else Recover(segment);
            }
        }

        private static void Recover(TailSegment segment)
        {
            if (segment.RecentDamage > 0f)
                segment.RecentDamage = Math.Max(0f, segment.RecentDamage -
                    WormTailDefinition.TearRecoveryRate * Battle.TickDuration * segment.MaxHitPoints);
        }

        // The place behind the parent is checked against every body the segment would touch; own bodies never count.
        private bool TryGrowSegment(ShipEntity ship, WormTailDevice device, TailSegment parent)
        {
            var definition = device.Definition;
            var scale = (parent?.Scale ?? ship.Definition.Scale) * WormTailDefinition.ScaleFactor;
            var pose = HeldPose(ship, definition, parent, scale);
            var radius = definition.Radius * scale;
            var count = Physics.OverlapCircle(pose.P, radius, CollisionFilters.Missile(ship.Allegiance), _segmentOverlaps, out _);
            for (var i = 0; i < count; ++i)
                if (!_segmentOverlaps[i].Target.IsBullet && _segmentOverlaps[i].Distance < radius) return false;

            var number = (parent?.Number ?? 0) + 1;
            var id = SpawnIdentity.CreateChild(ship.Id, Tick, (uint)device.Index, (uint)number, 0);
            var segment = CreateSegment(id, ship, device, parent, scale, pose);
            if (segment == null) return false;

            // Legacy WormSegmentFactory: a factory that spawned before, or one made by such a factory, gives the slow
            // delay; the first tail of a deployment grows fast.
            segment.NextGrowthTick = Tick + (segment.GrowsSlowly ? WormTailDefinition.RegrowthTicks : WormTailDefinition.FirstGrowthTicks);
            if (parent != null) parent.HasGrown = true;
            _battle.CaptureSegmentSpawn(ship, device.Index, number, id);
            return true;
        }

        // Legacy CreateWormTailSegment puts a new segment behind the parent at the parent's angle, without velocity; the
        // core puts it where the hinge holds it (P4). Mass: the ship mass / Size for the head, then x 0.95.
        private TailSegment CreateSegment(EntityId id, ShipEntity ship, WormTailDevice device, TailSegment parent,
            float scale, in Pose pose)
        {
            var definition = device.Definition;
            var segment = _world.Segments.Allocate();
            if (!_world.TryRegister(id, segment.Handle))
            {
                _world.Segments.Release(segment);
                return null;
            }

            var mass = parent?.Mass * WormTailDefinition.ScaleFactor ?? ship.Definition.Mass / definition.Size;
            var body = Physics.CreateBody(new BodyDef { Mass = mass });
            var shapeDef = ShapeDef.Default;
            shapeDef.Filter = CollisionFilters.Missile(ship.Allegiance);
            var shape = Physics.AddCircle(body, new Circle(Vec2.Zero, definition.Radius * scale), shapeDef);
            Physics.SetPose(body, pose.P, pose.Q.Angle);

            var growsSlowly = parent != null && (parent.GrowsSlowly || parent.HasGrown);
            segment.Initialize(id, ship, device.Index, definition, (parent?.Number ?? 0) + 1, body, shape, scale, mass,
                growsSlowly);
            _world.Shapes.Register(shape, new ShapeEntry(segment.Handle, id, ShapeHitPriority.Hull, 0));

            var limit = parent == null ? WormTailDefinition.HeadLimit : WormTailDefinition.SegmentLimit;
            segment.Joint = Physics.CreateHinge(new HingeDef
            {
                BodyA = parent?.Body ?? ship.Body,
                BodyB = body,
                LocalAnchorA = new Vec2(-ParentOffset(definition, parent) * (parent?.Scale ?? ship.Definition.Scale), 0f),
                LocalAnchorB = new Vec2(definition.JointOffset * scale, 0f),
                ReferenceAngle = 0f,
                EnableLimit = true,
                LowerAngle = -limit,
                UpperAngle = limit,
            });

            if (parent == null) device.Head = segment.Handle;
            else
            {
                segment.Parent = parent.Handle;
                parent.Next = segment.Handle;
            }
            return segment;
        }

        private static float ParentOffset(WormTailDefinition definition, TailSegment parent) =>
            parent == null ? definition.ParentOffset + WormTailDefinition.HeadExtraOffset : definition.ParentOffset;

        // The pose at which the hinge holds a segment of the given scale straight behind its parent.
        private Pose HeldPose(ShipEntity ship, WormTailDefinition definition, TailSegment parent, float scale)
        {
            var parentBody = parent?.Body ?? ship.Body;
            var anchorA = new Vec2(-ParentOffset(definition, parent) * (parent?.Scale ?? ship.Definition.Scale), 0f);
            var anchorB = new Vec2(definition.JointOffset * scale, 0f);
            var rotation = Physics.GetRotation(parentBody);
            return new Pose(Physics.Torus.Wrap(Physics.GetPosition(parentBody) + rotation.Rotate(anchorA - anchorB)), rotation);
        }

        // Stage 4, on both sides. Legacy WormSegmentBase.OnUpdatePhysics for an attached segment: a segment too far from
        // its parent (squared distance at least 5 x its scale) or turned by 90° or more goes back behind it with the
        // parent's velocity and no spin, and skips the damping; else it loses 5 x its sideways speed per second.
        public void UpdateSegmentMotion()
        {
            for (var i = 0; i < _world.Segments.Capacity; ++i)
            {
                var segment = _world.Segments[i];
                if (!segment.IsAlive) continue;
                segment.PreStepVelocity = Physics.GetLinearVelocity(segment.Body);
                if (segment.Detached) continue;

                var parent = _world.Segments.Get(segment.Parent);
                var ship = parent == null ? _world.FindShip(segment.ShipId) : null;
                if (parent == null && ship == null) continue;
                var parentBody = parent?.Body ?? ship.Body;

                var delta = Physics.Torus.Delta(Physics.GetPosition(parentBody), Physics.GetPosition(segment.Body));
                var turn = Battle.WrapAngle(Physics.GetAngle(segment.Body) - Physics.GetAngle(parentBody));
                if (delta.LengthSquared >= segment.Scale * 5f || Math.Abs(turn) >= 0.5f * (float)Math.PI)
                {
                    var pose = HeldPose(ship, segment.Definition, parent, segment.Scale);
                    Physics.SetPose(segment.Body, pose.P, pose.Q.Angle);
                    Physics.SetLinearVelocity(segment.Body, Physics.GetLinearVelocity(parentBody));
                    Physics.SetAngularVelocity(segment.Body, 0f);
                    continue;
                }

                var direction = Physics.GetRotation(segment.Body).Rotate(new Vec2(1f, 0f));
                var side = new Vec2(direction.Y, -direction.X);
                var sideSpeed = Vec2.Dot(Physics.GetLinearVelocity(segment.Body), side);
                Physics.ApplyImpulseToCenter(segment.Body,
                    segment.Mass * (-WormTailDefinition.SideDamping * sideSpeed * Battle.TickDuration) * side);
            }
        }

        // Stage 9. A hit counts on the owner's device when the attacker is local or no copy of this contact counted
        // yet; a local attacker on a remote segment sends the contact to the owner. A ray, damage over time and a ram
        // count once per tick; a projectile once per target.
        public void ResolveSegmentHit(in ContactCandidate hit, TailSegment segment)
        {
            int attackerOwner, deviceIndex;
            HitImpactKind kind;
            var impulse = Vec2.Zero;
            var perTick = true;
            var isBullet = _world.Bullets.TryGet(hit.AttackerHandle, out var index);
            if (isBullet)
            {
                ref readonly var attacker = ref _world.Bullets[index];
                attackerOwner = attacker.Owner;
                deviceIndex = attacker.DeviceIndex;
                if (attacker.IsBeam && attacker.BeamDefinition.Impact != BulletImpact.OverTime)
                {
                    kind = HitImpactKind.Energy;
                    perTick = false;
                }
                else if (attacker.IsBeam || attacker.Definition.Impact == BulletImpact.OverTime)
                    kind = HitImpactKind.DamageOverTime;
                else
                {
                    kind = attacker.Definition.IsMissile ? HitImpactKind.Impact : HitImpactKind.Energy;
                    perTick = false;
                    impulse = _battle.PushImpulse(attacker, Physics.GetPosition(segment.Body),
                        attacker.Definition.HasFalloff ? hit.Falloff : 1f);
                }
            }
            else
            {
                var ship = _world.Ships.Get(hit.AttackerHandle);
                if (ship == null) return;
                attackerOwner = ship.Owner;
                deviceIndex = -1;
                kind = HitImpactKind.Impact;
            }

            if (Setup.IsLocalOwner(segment.Owner))
            {
                if (!Setup.IsLocalOwner(attackerOwner) && !_battle.RecordBulletContact(hit.Attacker, hit.Target, perTick ? Tick : -1))
                    return;
                QueueSegmentHit(segment, hit.Damage, impulse);
                // A remote projectile that stops at a local segment stops on its owner too.
                if (isBullet && !hit.AttackerPassThrough && hit.Source != ContactSource.Ray &&
                    !Setup.IsLocalOwner(attackerOwner))
                    _battle.ClaimBulletContact(segment.Id, segment.Owner, -1, _world.Bullets[index], HitImpactKind.Stop, 0f);
            }
            else if (Setup.IsLocalOwner(attackerOwner) && Setup.HasRemoteOwners)
            {
                _battle.SendBulletContact(new ResolvedHit(_world.BattleId, Tick, hit.Attacker, attackerOwner, deviceIndex,
                    segment.Id, segment.Owner, 0, HitContactState.Start, kind, hit.Damage, targetIsBullet: true));
            }
        }

        // The owner applies a received contact on its segment once, as ApplyBulletContact does for a bullet.
        public void ApplySegmentContact(in ResolvedHit hit, TailSegment segment)
        {
            if (segment == null || segment.Owner != hit.TargetOwner || !Setup.IsLocalOwner(segment.Owner) ||
                hit.ImpactKind == HitImpactKind.Disarm || hit.ImpactKind == HitImpactKind.Stop) return;
            var perTick = hit.ImpactKind == HitImpactKind.DamageOverTime || hit.DeviceIndex < 0;
            if (!_battle.RecordBulletContact(hit.AttackerId, hit.TargetId, perTick ? hit.Tick : -1)) return;
            QueueSegmentHit(segment, hit.Amount, Vec2.Zero);
            _world.Events.Add(BattleEvent.RemoteHit(_world.BattleId, Tick, _world.Events.Count, hit, hit.Amount));
        }

        // Stage 9 (and tests). Legacy segments take a tenth of the push at once (Impulse.Apply with the reaction factor).
        public void QueueSegmentHit(TailSegment segment, float damage, Vec2 impulse)
        {
            _segmentHits.Add(new SegmentHit(segment.Handle, damage));
            if (impulse.X != 0f || impulse.Y != 0f)
                Physics.ApplyImpulse(segment.Body, WormTailDefinition.ImpulseShare * impulse, Physics.GetPosition(segment.Body));
        }

        // Stage 10, on the owner, in the order stage 9 accepted the hits. Legacy WormSegmentDestroyable.Affect: a hit
        // that brings the recent damage above the hit points tears the segment off. Otherwise the damage goes to the tip
        // first. Legacy FixedWormSegment.Affect: the damage the tail cannot take passes to the ship as impact damage.
        public void ApplySegmentHits()
        {
            for (var i = 0; i < _segmentHits.Count; ++i)
            {
                var segment = _world.Segments.Get(_segmentHits[i].Segment);
                if (segment == null || segment.PendingDeath) continue;

                var damage = _segmentHits[i].Damage;
                if (!segment.IsFixed)
                {
                    segment.RecentDamage += damage;
                    if (segment.RecentDamage > segment.MaxHitPoints) KillSegment(segment);
                    else TryDealDamage(segment, ref damage);
                    continue;
                }

                if (TryDealDamage(segment, ref damage) || segment.Detached) continue;
                _world.FindShip(segment.ShipId)?.AddPendingDamage(Setup.DamageTypes.RammingDamageType, damage);
            }
            _segmentHits.Clear();
        }

        // Legacy TryDealDamage. A segment that absorbs the hit restarts its regrowth delay; one that cannot dies (a fixed
        // segment only when detached) and passes the rest to its parent.
        private bool TryDealDamage(TailSegment segment, ref float damage)
        {
            if (damage <= 0f) return true;
            if (segment.Detached)
            {
                segment.HitPoints -= damage;
                if (segment.HitPoints <= 0f) KillSegment(segment);
                return true;
            }

            var next = _world.Segments.Get(segment.Next);
            if (next != null && !next.PendingDeath && TryDealDamage(next, ref damage)) return true;
            if (segment.HitPoints <= 0f) return false;
            if (segment.HitPoints > damage)
            {
                segment.NextGrowthTick = Tick + segment.Definition.CooldownTicks;
                segment.HitPoints -= damage;
                damage = 0f;
                return true;
            }

            if (!segment.IsFixed) KillSegment(segment);
            damage -= segment.HitPoints;
            return false;
        }

        // Legacy WormSegmentBase.Destroy, on the owner: the segments behind it detach at once and destroy themselves
        // after 5 s. Stage 12 removes the segment itself.
        private void KillSegment(TailSegment segment)
        {
            if (segment.PendingDeath) return;
            segment.PendingDeath = true;
            DetachNext(segment, Tick + WormTailDefinition.SelfDestructTicks);
        }

        // The segments behind this one leave the chain; the first one loses its hinge. A timer that runs keeps its tick.
        private void DetachNext(TailSegment segment, long selfDestructTick)
        {
            var next = _world.Segments.Get(segment.Next);
            segment.Next = default;
            if (next == null) return;

            if (Physics.IsAlive(next.Joint)) Physics.DestroyJoint(next.Joint);
            next.Joint = default;
            next.Parent = default;
            for (; next != null; next = _world.Segments.Get(next.Next))
            {
                next.Detached = true;
                if (selfDestructTick < next.SelfDestructTick) next.SelfDestructTick = selfDestructTick;
            }
        }

        // Stage 12, after the drones. The parent regrows the lost segment after the cooldown (legacy UpdateChildren).
        public void RemoveDeadSegments()
        {
            for (var i = 0; i < _world.Segments.Capacity; ++i)
            {
                var segment = _world.Segments[i];
                if (segment.IsAlive && segment.PendingDeath && Setup.IsLocalOwner(segment.Owner))
                    RemoveSegment(segment, RemovalReason.Death);
            }
        }

        public void RemoveSegment(TailSegment segment, RemovalReason reason)
        {
            // On a copy, the record detaches the rest; the owner sends their removals.
            DetachNext(segment, Setup.IsLocalOwner(segment.Owner)
                ? Tick + WormTailDefinition.SelfDestructTicks : TailSegment.NoTimer);

            var parent = _world.Segments.Get(segment.Parent);
            if (parent != null)
            {
                if (parent.Next.Index == segment.Index && parent.Next.Generation == segment.Generation)
                {
                    parent.Next = default;
                    parent.NextGrowthTick = Tick + segment.Definition.CooldownTicks;
                }
            }
            else if (!segment.Detached && _world.FindShip(segment.ShipId)?.GetDevice(segment.DeviceIndex) is WormTailDevice device &&
                     device.Head.Index == segment.Index && device.Head.Generation == segment.Generation)
            {
                device.Head = default;
            }

            _battle.CaptureRemoval(segment.Id, segment.Owner, reason);
            ReleaseSegment(segment);
        }

        private void ReleaseSegment(TailSegment segment)
        {
            _world.Shapes.Release(segment.Shape);
            _battle.ForgetContacts(segment.Id);
            Physics.DestroyBody(segment.Body);
            _world.Entities.Remove(segment.Id);
            _world.Segments.Release(segment);
        }

        // Plan, WormTailV2 row 7 (owner, 2026-11-02). A ship that dies takes the head of its tail with it; the segments
        // behind it detach and destroy themselves after 5 s, as after a tear (legacy: the head finds its ship destroyed
        // first and calls Destroy). On a copy they wait for the owner's removal records. A ship that retreats or
        // vanishes takes every attached segment (legacy Vanish detaches nothing). Detached segments run out their timers.
        public void RemoveShipSegments(ShipEntity ship, RemovalReason reason)
        {
            for (var d = 0; d < ship.DeviceCount; ++d)
            {
                if (!(ship.GetDevice(d) is WormTailDevice device)) continue;
                var head = _world.Segments.Get(device.Head);
                if (head != null && reason == RemovalReason.Death)
                {
                    DetachNext(head, Setup.IsLocalOwner(head.Owner)
                        ? Tick + WormTailDefinition.SelfDestructTicks : TailSegment.NoTimer);
                }
                for (var segment = head; segment != null;)
                {
                    var next = _world.Segments.Get(segment.Next);
                    _battle.CaptureRemoval(segment.Id, segment.Owner, reason);
                    ReleaseSegment(segment);
                    segment = next;
                }
                device.Head = default;
            }
        }

        // The copy builds a segment from its owner's spawn record, behind the end of its own chain.
        public bool ApplyRemoteSegment(ShipEntity ship, WormTailDevice device, in OwnerOperation operation)
        {
            if (_world.Entities.TryGet(operation.EntityId, out _) || operation.SpawnSequence != 0) return false;
            TailSegment parent = null;
            for (var segment = _world.Segments.Get(device.Head); segment != null; segment = _world.Segments.Get(segment.Next))
                parent = segment;
            if ((parent?.Number ?? 0) + 1 != operation.BarrelIndex) return false;

            var scale = (parent?.Scale ?? ship.Definition.Scale) * WormTailDefinition.ScaleFactor;
            return CreateSegment(operation.EntityId, ship, device, parent, scale,
                HeldPose(ship, device.Definition, parent, scale)) != null;
        }

        private readonly struct SegmentHit
        {
            public readonly EntityHandle Segment;
            public readonly float Damage;

            public SegmentHit(EntityHandle segment, float damage)
            {
                Segment = segment;
                Damage = damage;
            }
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly List<SegmentHit> _segmentHits = new List<SegmentHit>();
        private readonly OverlapHit[] _segmentOverlaps = new OverlapHit[4];
    }
}

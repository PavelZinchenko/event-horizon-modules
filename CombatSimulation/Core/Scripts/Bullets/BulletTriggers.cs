using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using Utilites;

namespace CombatSimulation.Core
{
    // Bullet triggers (phase 5, step 2): child bullets and detonation. Only the owner of a bullet runs its triggers
    // (H8). It sends one spawn record per child, with the parent named as a bullet, the trigger index as the spawn
    // source and the child's launch pose. A receiver never derives children; it adds each child from its record.
    //
    // The spawn records and the due removals stay with Battle until the network part moves (phase 5, step 8b, item 5).
    internal sealed class BulletTriggers
    {
        public BulletTriggers(BattleWorld world, Battle battle)
        {
            _world = world;
            _battle = battle;
        }

        private BattleSetup Setup => _world.Setup;
        private long Tick => _world.Tick;
        private PhysicsWorld Physics => _world.Physics;

        // Legacy Bullet: Created triggers run when the bullet is made, at its spawn tick.
        public void RunCreatedTriggers(int index)
        {
            ref readonly var bullet = ref _world.Bullets[index];
            if ((bullet.Definition.TriggerConditions & BulletTriggerConditions.Created) == 0 ||
                !Setup.IsLocalOwner(bullet.Owner)) return;
            FireTriggers(index, BulletTriggerConditions.Created, bullet.SpawnTick);
        }

        // Stage 12, before the bullet leaves: one Hit run per pending contact, then the conditions of its removal
        // (legacy Bullet.OnCollision, Detonate, Destroy, Expire and Disarm). Children of stage 12 start to move in the
        // next tick, so that tick is their spawn tick.
        public void RunStructureTriggers(int index)
        {
            ref var bullet = ref _world.Bullets[index];
            var hits = bullet.PendingHits;
            bullet.PendingHits = 0;
            if (bullet.IsBeam || bullet.SkipTriggers || bullet.Definition.TriggerConditions == BulletTriggerConditions.None ||
                !Setup.IsLocalOwner(bullet.Owner)) return;

            var spawnTick = Tick + 1;
            for (var i = 0; i < hits; ++i)
            {
                // Legacy DetonateAction on Hit returns Destroy: the bullet stops.
                if (FireTriggers(index, BulletTriggerConditions.Hit, spawnTick) &&
                    _world.Bullets[index].PendingRemoval == RemovalReason.None)
                    _world.Bullets[index].PendingRemoval = RemovalReason.Death;
            }

            switch (_world.Bullets[index].PendingRemoval)
            {
                case RemovalReason.Death:
                    FireTriggers(index, BulletTriggerConditions.Destroyed, spawnTick);
                    if (_world.Bullets[index].Definition.DetonateWhenDestroyed)
                        FireTriggers(index, BulletTriggerConditions.Detonated, spawnTick);
                    break;
                case RemovalReason.Expiry:
                    // Legacy Expire: a Detonate trigger among the Expired ones makes the bullet detonate.
                    var detonate = FireTriggers(index, BulletTriggerConditions.Expired, spawnTick);
                    FireTriggers(index, BulletTriggerConditions.Destroyed, spawnTick);
                    if (detonate) FireTriggers(index, BulletTriggerConditions.Detonated, spawnTick);
                    break;
                case RemovalReason.Disposal:
                    FireTriggers(index, BulletTriggerConditions.Disarmed, spawnTick);
                    break;
            }
        }

        // Owner only: counts a contact of a local projectile for its Hit triggers (stage 12).
        public void AddPendingHit(int index)
        {
            ref var bullet = ref _world.Bullets[index];
            if (!bullet.IsBeam && Setup.IsLocalOwner(bullet.Owner) &&
                (bullet.Definition.TriggerConditions & BulletTriggerConditions.Hit) != 0)
                ++bullet.PendingHits;
        }

        // Runs the triggers of one condition in order. True if a Detonate trigger is among them.
        private bool FireTriggers(int index, BulletTriggerConditions condition, long spawnTick)
        {
            var triggers = _world.Bullets[index].Definition.Triggers;
            var detonate = false;
            for (var t = 0; t < triggers.Count; ++t)
            {
                var trigger = triggers[t];
                if ((trigger.Conditions & condition) == 0) continue;
                if (trigger.IsDetonate) detonate = true;
                else SpawnChildren(index, t, trigger, spawnTick);
            }
            return detonate;
        }

        // Legacy SpawnBulletsAction and BulletFactory.ConfigureBody: each child turns from the parent's heading by its
        // pattern rotation, starts at its pattern offset in that frame and inherits the parent's velocity by its own
        // ParentVelocityEffect. A child spawned by a child runs its own Created triggers at once; the mapper bounds the
        // nesting.
        private void SpawnChildren(int parentIndex, int triggerIndex, BulletTriggerDefinition trigger, long spawnTick)
        {
            var parent = _world.Bullets[parentIndex];
            var position = Physics.GetBulletPosition(parent.Body);
            var parentAngle = Physics.GetBulletRotation(parent.Body).Angle;
            var parentVelocity = Physics.GetBulletVelocity(parent.Body);
            var definition = trigger.Bullet;
            var pattern = trigger.Pattern;
            for (var i = 0; i < pattern.Count; ++i)
            {
                // Each spawn can grow the store, so the parent is read again.
                ref var counter = ref _world.Bullets[parentIndex];
                if (counter.ChildTick != spawnTick)
                {
                    counter.ChildTick = spawnTick;
                    counter.ChildCount = 0;
                }
                var sequence = counter.ChildCount++;

                var id = SpawnIdentity.CreateChild(parent.Id, spawnTick, (uint)triggerIndex, 0, sequence);
                var draw = DrawChild(id, spawnTick, trigger);
                var slot = pattern[i];
                var angle = parentAngle + slot.Rotation + slot.RotationSpan * draw.Rotation;
                var rotation = Rot.FromAngle(angle);
                var offset = new Vec2(slot.Offset.X + slot.OffsetSpan.X * draw.OffsetX,
                    slot.Offset.Y + slot.OffsetSpan.Y * draw.OffsetY);
                var start = Physics.Torus.Wrap(position + rotation.Rotate(offset));

                var index = CreateChild(id, parent, parentVelocity, definition, draw, spawnTick, start, angle);
                if (index < 0) continue;
                ref readonly var child = ref _world.Bullets[index];
                _world.Events.Add(BattleEvent.BulletSpawn(_world.BattleId, Tick, _world.Events.Count, child, _world.Authority(child.Owner)));
                _battle.CaptureBulletSpawn(child, triggerIndex, 0, sequence, spawnTick, true, start, angle);
                RunCreatedTriggers(index);
            }
        }

        // A child's body at its launch pose, moved on a line from its spawn tick to the present (a late record).
        // Returns -1 on an ID collision.
        private int CreateChild(EntityId id, in BulletEntity parent, Vec2 parentVelocity, BulletDefinition definition,
            in ChildDraw draw, long spawnTick, Vec2 launch, float angle)
        {
            var index = _world.Bullets.Allocate();
            if (!_world.TryRegister(id, _world.Bullets.GetHandle(index)))
            {
                _world.Bullets.Release(index);
                return -1;
            }

            var direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            var velocity = definition.Speed * draw.SpeedScale * definition.StartingVelocityMultiplier * direction +
                           definition.ParentVelocityEffect * parentVelocity;
            var position = launch;
            for (var tick = spawnTick; tick < Tick; ++tick)
                position = Physics.Torus.Wrap(position + Battle.TickDuration * velocity);

            var ship = _world.FindShip(parent.ShipId);
            var body = Physics.CreateBullet(new BulletDef
            {
                Position = position,
                Angle = angle,
                LinearVelocity = velocity,
                Owner = ship?.Body ?? default,
                PassThrough = true,
                Filter = CollisionFilters.Bullet(definition, parent.Allegiance),
            }, new Circle(Vec2.Zero, definition.Radius));

            ref var bullet = ref _world.Bullets[index];
            bullet.Id = id;
            bullet.ParentId = parent.Id;
            bullet.ShipId = parent.ShipId;
            bullet.Side = parent.Side;
            bullet.Allegiance = parent.Allegiance;
            bullet.Owner = parent.Owner;
            bullet.DeviceIndex = parent.DeviceIndex;
            bullet.Definition = definition;
            bullet.SpeedScale = draw.SpeedScale;
            bullet.ExpiryTick = spawnTick + draw.LifetimeTicks - 1;
            bullet.HitPoints = definition.HitPoints;
            bullet.SpawnTick = spawnTick;
            bullet.Confirmed = true;
            bullet.NextSearchTick = spawnTick + 1;
            _world.Bullets.SetBody(index, body);
            return index;
        }

        // The draws of one child, in a fixed order, from its identity: both devices get the same speed and lifetime.
        private ChildDraw DrawChild(EntityId id, long spawnTick, BulletTriggerDefinition trigger)
        {
            var random = CounterRandom.Create(Setup.MatchSeed, id, spawnTick, RandomPurpose.ChildSpawn);
            var draw = new ChildDraw
            {
                Rotation = random.NextFloat(),
                OffsetX = random.NextFloat(),
                OffsetY = random.NextFloat(),
            };
            var speed = random.NextFloat();
            var lifetime = random.NextFloat();

            // Legacy BulletStats.GetBulletSpeed and GetBulletLifetime: each varies by (r - 0.5) x RandomFactor.
            var factor = trigger.RandomFactor;
            draw.SpeedScale = factor > 0f ? 1f + (speed - 0.5f) * factor : 1f;
            draw.LifetimeTicks = factor > 0f
                ? TickTime.After(trigger.Bullet.Lifetime * (1f + (lifetime - 0.5f) * factor))
                : trigger.Bullet.LifetimeTicks;
            return draw;
        }

        private struct ChildDraw
        {
            public float Rotation, OffsetX, OffsetY;
            public float SpeedScale;
            public int LifetimeTicks;
        }

        // A receiver adds the owner's child at its launch pose. The parent is the live copy, or a remote bullet that this
        // device has removed already (prediction, expiry) but whose owner may still send children.
        public bool ApplyRemoteChildSpawn(in OwnerOperation operation)
        {
            if (!operation.HasLaunchPose || operation.BarrelIndex != 0) return false;

            TriggerParent parent;
            if (_world.Entities.TryGet(operation.ParentId, out var handle) && _world.Bullets.TryGet(handle, out var parentIndex))
            {
                ref readonly var copy = ref _world.Bullets[parentIndex];
                if (copy.IsBeam) return false;
                parent = new TriggerParent(copy, Physics.GetBulletVelocity(copy.Body));
            }
            else if (!_triggerParents.TryGetValue(operation.ParentId, out parent)) return false;

            var triggers = parent.Bullet.Definition.Triggers;
            if (parent.Bullet.Owner != operation.Owner || operation.DeviceIndex >= triggers.Count ||
                triggers[operation.DeviceIndex].IsDetonate || _world.Entities.TryGet(operation.EntityId, out _)) return false;

            var trigger = triggers[operation.DeviceIndex];
            var draw = DrawChild(operation.EntityId, operation.Tick, trigger);
            if (operation.Tick + draw.LifetimeTicks - 1 < Tick || _battle.HasDueRemoval(operation.EntityId))
            {
                // The child is gone already; its own children may still arrive.
                RememberTriggerParent(operation.EntityId, parent.Bullet, trigger.Bullet, draw.SpeedScale, Vec2.Zero);
                return true;
            }

            var index = CreateChild(operation.EntityId, parent.Bullet, parent.Velocity, trigger.Bullet, draw,
                operation.Tick, Physics.Torus.Wrap(operation.Pose.Position), operation.Pose.Angle);
            if (index < 0) return false;
            ref readonly var child = ref _world.Bullets[index];
            RememberTriggerParent(child);
            _world.Events.Add(BattleEvent.BulletSpawn(_world.BattleId, Tick, _world.Events.Count, child));
            return true;
        }

        // Receiver: a remote bullet that can spawn children, from its spawn record until a tick after its owner's removal
        // record. Its children's records come before that removal, but a late batch applies them in tick order, after it.
        public void RememberTriggerParent(in BulletEntity bullet)
        {
            if (!bullet.IsBeam && bullet.Definition.HasSpawnTriggers && !Setup.IsLocalOwner(bullet.Owner))
                _triggerParents[bullet.Id] = new TriggerParent(bullet, Vec2.Zero);
        }

        // A child that this device skipped (it had expired): the parent's fields with the child's definition.
        private void RememberTriggerParent(EntityId id, in BulletEntity parent, BulletDefinition definition,
            float speedScale, Vec2 velocity)
        {
            if (!definition.HasSpawnTriggers) return;
            var bullet = parent;
            bullet.Id = id;
            bullet.ParentId = parent.Id;
            bullet.Definition = definition;
            bullet.SpeedScale = speedScale;
            _triggerParents[id] = new TriggerParent(bullet, velocity);
        }

        // A skipped ship shot whose bullet can spawn children, with its launch velocity.
        public void RememberTriggerParent(ShipEntity ship, int deviceIndex, BulletDefinition definition, EntityId id,
            Vec2 velocity)
        {
            if (!definition.HasSpawnTriggers) return;
            var bullet = new BulletEntity
            {
                Id = id,
                ParentId = ship.Id,
                ShipId = ship.Id,
                Side = ship.Side,
                Allegiance = ship.Allegiance,
                Owner = ship.Owner,
                DeviceIndex = deviceIndex,
                Definition = definition,
                SpeedScale = 1f,
            };
            _triggerParents[id] = new TriggerParent(bullet, velocity);
        }

        // A released copy keeps its last velocity for the children that its owner still sends.
        public void ForgetTriggerParentCopy(in BulletEntity bullet)
        {
            if (_triggerParents.Count == 0 || bullet.IsBeam || !_triggerParents.TryGetValue(bullet.Id, out var parent))
                return;
            parent.Velocity = Physics.GetBulletVelocity(bullet.Body);
            parent.Bullet.Allegiance = bullet.Allegiance;
            _triggerParents[bullet.Id] = parent;
        }

        public void MarkTriggerParentRemoved(EntityId id, long tick)
        {
            if (!_triggerParents.TryGetValue(id, out var parent)) return;
            parent.RemovedTick = tick;
            _triggerParents[id] = parent;
        }

        // After the bullet spawns of stage 5: a parent removed before this tick can have no more children.
        public void PruneTriggerParents()
        {
            if (_triggerParents.Count == 0) return;
            _prunedTriggerParents.Clear();
            foreach (var pair in _triggerParents)
                if (pair.Value.RemovedTick < Tick) _prunedTriggerParents.Add(pair.Key);
            foreach (var id in _prunedTriggerParents)
                _triggerParents.Remove(id);
        }

        public int TriggerParentCount => _triggerParents.Count;

        private struct TriggerParent
        {
            public BulletEntity Bullet;
            public Vec2 Velocity;
            public long RemovedTick;

            public TriggerParent(in BulletEntity bullet, Vec2 velocity)
            {
                Bullet = bullet;
                Velocity = velocity;
                RemovedTick = long.MaxValue;
            }
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly Dictionary<EntityId, TriggerParent> _triggerParents = new Dictionary<EntityId, TriggerParent>();
        private readonly List<EntityId> _prunedTriggerParents = new List<EntityId>();
    }
}

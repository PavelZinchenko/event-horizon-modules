using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One projectile or beam in the struct store. Only projectiles have a physics bullet.
    internal struct BulletEntity
    {
        public int Generation;
        public bool IsAlive;

        public EntityId Id;
        // The direct parent: a ship, or a bullet for a child bullet (phase 5).
        public EntityId ParentId;
        // The ship that fired the bullet or its first ancestor. Allegiance changes follow it.
        public EntityId ShipId;
        public int Side;
        public Allegiance Allegiance;
        public int Owner;
        // The weapon of ShipId, for children too: hits name it.
        public int DeviceIndex;
        public BulletDefinition Definition;
        public BulletId Body;

        // Legacy RandomFactor of a child bullet: the share of the definition's speed and thrust (1 otherwise).
        public float SpeedScale;
        public float Speed => Definition.Speed * SpeedScale;
        public float Acceleration => Definition.Acceleration * SpeedScale;

        // Owner only: contacts of this tick whose Hit triggers stage 12 runs.
        public int PendingHits;

        // The children spawned for one spawn tick, across triggers: the sequence of the next child's identity.
        public long ChildTick;
        public uint ChildCount;

        // Exactly one definition is set. Beams keep the ray pose sampled at stage 6.
        public BeamDefinition BeamDefinition;
        public bool IsBeam => BeamDefinition != null;
        public Vec2 BeamOrigin;
        public float BeamAngle;
        public Vec2 BeamEndPoint;
        public bool RayOverflow;

        // A beam's progressive ramp (phase 5, step 5c): the first body on the ray and the tick its contact started.
        public EntityId RampTargetId;
        public long RampTick;

        // A beam bolt's ray length in this tick (phase 5, step 4e): cast at stage 6, cut at its first hit at stage 9.
        public float RayLength;

        // The last tick the bullet flies in; stage 11 of that tick marks its expiry.
        public long ExpiryTick;

        // A restoring bullet on its owner (phase 5, step 5d): its full lifetime in ticks, which caps the restore, and the
        // fraction of a tick restored but not yet added to the expiry.
        public int LifetimeCapTicks;
        public float RestoreCarry;

        // An aura (phase 5, step 4g): the tick its lifetime last started (its spawn, or its weapon's renewal), and its
        // size in ticks of growth (Auras).
        public long RenewTick;
        public int AuraTicks;

        // A missile: its hit points, and the damage accepted at stage 9 that stage 10 applies.
        public float HitPoints;
        public float PendingDamage;

        // Homing target, the beam's selected ray target, or a sticky bullet's target (null without one).
        public EntityId TargetId;
        // Homing only: the tick of the next scheduled search.
        public long NextSearchTick;

        // Set at stage 9 (stopped at a hit) or stage 11 (expired); stage 12 applies it.
        public RemovalReason PendingRemoval;

        // An attached bullet that leaves with its ship runs no triggers (phase 5, step 3; owner, 2026-11-02).
        public bool SkipTriggers;

        // Legacy Bullet.Detonate (phase 5, step 4b): a Death that runs the Detonated triggers whatever
        // DetonateWhenDestroyed.
        public bool Detonates;

        // A sticky bullet rides on its TargetId's body (phase 5, step 4b); the owner detonates it at stage 4 of
        // DetonationTick.
        public bool Stuck;
        public long DetonationTick;

        // The owner's spawn tick. A remote bullet is Confirmed once its owner's fire record arrives; local bullets
        // are confirmed at creation.
        public long SpawnTick;
        public bool Confirmed;

        // Homing projectiles in a network battle (network plan, P3). On the owner: the batch tick of the last
        // correction sent. On a remote copy: the tick of the last correction applied, and one held for stage 1.
        public bool Corrected;
        public long CorrectedTick;
        public bool HasCorrection;
        public long CorrectionTick;
        public MissileCorrection Correction;
    }

    // Bullets in a struct array with a LIFO free list (about 2,000 at R2). Allocation order never affects public IDs.
    internal sealed class BulletStore
    {
        public int AliveCount { get; private set; }

        // Upper bound of the indices; iterate with IsAlive.
        public int Capacity => _count;

        public ref BulletEntity this[int index] => ref _items[index];

        public bool IsAlive(int index) => _items[index].IsAlive;

        public EntityHandle GetHandle(int index) => new EntityHandle(EntityKind.Bullet, index, _items[index].Generation);

        public int Allocate()
        {
            int index;
            if (_freeCount > 0)
            {
                index = _free[--_freeCount];
            }
            else
            {
                // The free list grows with the store, so a tick that frees many bullets late allocates nothing.
                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, _items.Length * 2);
                    Array.Resize(ref _free, _items.Length);
                }
                index = _count++;
            }

            ref var bullet = ref _items[index];
            var generation = bullet.Generation + 1;
            bullet = default;
            bullet.Generation = generation;
            bullet.IsAlive = true;
            ++AliveCount;
            return index;
        }

        public void Release(int index)
        {
            ref var bullet = ref _items[index];
            var generation = bullet.Generation;
            bullet = default;
            bullet.Generation = generation;

            _free[_freeCount++] = index;
            --AliveCount;
        }

        // False for a stale or foreign handle.
        public bool TryGet(EntityHandle handle, out int index)
        {
            index = handle.Index;
            return handle.Kind == EntityKind.Bullet && index >= 0 && index < _count && _items[index].IsAlive &&
                   _items[index].Generation == handle.Generation;
        }

        // Links the physics bullet, so that a physics hit resolves to the store entry.
        public void SetBody(int index, BulletId body)
        {
            _items[index].Body = body;
            if (body.Index >= _byBody.Length) Array.Resize(ref _byBody, Math.Max(body.Index + 1, _byBody.Length * 2));
            _byBody[body.Index] = index;
        }

        public bool TryGetByBody(BulletId body, out int index)
        {
            index = body.Index >= 0 && body.Index < _byBody.Length ? _byBody[body.Index] : -1;
            return index >= 0 && _items[index].IsAlive && !_items[index].IsBeam && _items[index].Body.Equals(body);
        }

        // Stage 9. A non-bullet handle is ignored. The first reason wins.
        public void MarkRemoval(EntityHandle handle, RemovalReason reason)
        {
            if (TryGet(handle, out var index) && _items[index].PendingRemoval == RemovalReason.None)
                _items[index].PendingRemoval = reason;
        }

        // Owner only, before the physics step (phase 5, step 4b): a manageable weapon's released key (stage 2) or a sticky
        // bullet's timer (stage 4). Legacy Bullet.Detonate destroys the bullet at once, so it takes no contacts in this
        // tick; stage 12 runs its Destroyed and Detonated triggers and removes it.
        public void MarkDetonation(int index)
        {
            ref var bullet = ref _items[index];
            if (bullet.PendingRemoval != RemovalReason.None) return;
            bullet.PendingRemoval = RemovalReason.Death;
            bullet.Detonates = true;
        }

        // Stage 8. Each physics hit on a live ship or missile becomes a candidate. Every physics bullet is
        // pass-through, so physics reports the first hit on each target unit; the resolver keeps the first valid one
        // (contact ordering). Phase 5, step 3: an AllTargets bullet takes a unit only when it enters (physics Begin);
        // an OverTime bullet takes every touching unit, with one tick of its damage. Neither stops at a hit. Step 4b: a
        // bullet marked for removal before the step (a detonation) takes no contacts and is no target; a sticky bullet
        // records the first ship, drone or tail segment it touches.
        public void CollectHits(PhysicsWorld world, ShapeTable shapes, ShipStore ships, TailSegmentStore segments,
            ContactResolver resolver)
        {
            var hits = world.BulletHits;
            for (var i = 0; i < hits.Length; ++i)
            {
                ref readonly var hit = ref hits[i];
                if (!TryGetByBody(hit.Bullet, out var index)) continue;
                ref readonly var bullet = ref _items[index];
                if (bullet.PendingRemoval != RemovalReason.None) continue;
                var definition = bullet.Definition;
                // An aura of size 0 (the last tick of its lifetime) touches nothing.
                if (definition.IsAura && bullet.AuraTicks == 0) continue;
                if (definition.IsSticky && !bullet.Stuck && bullet.TargetId.IsNull && !hit.Target.IsBullet &&
                    shapes.TryGet(hit.Target.Shape, out var caught))
                    _items[index].TargetId = caught.OwnerId;
                if (definition.Impact == BulletImpact.AllTargets && !hit.Begin) continue;

                var candidate = new ContactCandidate
                {
                    Time = hit.Fraction,
                    Kind = ContactKind.Hit,
                    Attacker = bullet.Id,
                    Source = ContactSource.BulletSweep,
                    AttackerHandle = GetHandle(index),

                    // Q1 and Q2b: a bolt and a missile both stop at their first valid contact.
                    AttackerPassThrough = definition.Impact != BulletImpact.FirstTarget,
                    // One hit per target entity and step, whatever its units (D16).
                    OncePerTargetEntity = definition.Impact != BulletImpact.FirstTarget,
                    DamageType = definition.DamageType,
                    Point = hit.Point,
                };

                Vec2 targetPosition;
                float targetScale;
                if (hit.Target.IsBullet)
                {
                    // Legacy: only a missile takes damage from a bullet (MissileDamageHandler), and a missile does not
                    // detonate on an energy bolt (DetonateAtTargetAction). A missile flies through bolts unharmed.
                    if (!TryGetByBody(hit.Target.Bullet, out var targetIndex)) continue;
                    ref readonly var targetBullet = ref _items[targetIndex];
                    if (!targetBullet.Definition.IsMissile || targetBullet.PendingRemoval != RemovalReason.None) continue;

                    candidate.TargetPriority = ShapeHitPriority.Hull;
                    candidate.Target = targetBullet.Id;
                    candidate.TargetHandle = GetHandle(targetIndex);
                    candidate.TargetHitPoints = targetBullet.HitPoints;
                    targetPosition = world.GetBulletPosition(targetBullet.Body);
                    targetScale = targetBullet.Definition.Scale;
                }
                else
                {
                    if (!shapes.TryGet(hit.Target.Shape, out var target)) continue;
                    var ship = ships.Get(target.Owner);
                    var segment = ship == null ? segments.Get(target.Owner) : null;
                    if (ship == null && segment == null) continue;

                    candidate.TargetPriority = target.Priority;
                    candidate.Target = target.OwnerId;
                    candidate.TargetShapeOrder = target.Order;
                    candidate.TargetHandle = target.Owner;
                    targetPosition = world.GetPosition(ship?.Body ?? segment.Body);
                    targetScale = ship?.Definition.Scale ?? segment.Scale;
                }

                // The total: a missile target sums every type; stage 9 splits it again for a ship target.
                candidate.Falloff = definition.HasFalloff
                    ? definition.Falloff(world.Torus.Delta(world.GetBulletPosition(bullet.Body), targetPosition).Length,
                        targetScale)
                    : 1f;
                candidate.Damage = TotalDamage(definition, candidate.Falloff);
                resolver.Add(candidate);
            }
        }

        // Stage 8 (phase 5, step 4b). Physics never hits the body a bullet rides on, but legacy keeps a stuck sticky bullet
        // inside its target's collider, so its damage over time goes on. Each step a stuck OverTime bullet adds one
        // contact on its target's first shape, at time 0, without a physics query.
        public void CollectStuckHits(PhysicsWorld world, EntityRegistry entities, ShapeTable shapes, ShipStore ships,
            TailSegmentStore segments, ContactResolver resolver)
        {
            for (var i = 0; i < _count; ++i)
            {
                ref readonly var bullet = ref _items[i];
                if (!bullet.IsAlive || !bullet.Stuck || bullet.PendingRemoval != RemovalReason.None ||
                    bullet.Definition.Impact != BulletImpact.OverTime || !entities.TryGet(bullet.TargetId, out var handle))
                    continue;

                var ship = ships.Get(handle);
                var segment = ship == null ? segments.Get(handle) : null;
                ShapeId shape;
                if (ship != null && ship.ShapeCount > 0) shape = ship.GetShape(0);
                else if (segment != null) shape = segment.Shape;
                else continue;
                if (!shapes.TryGet(shape, out var target)) continue;

                resolver.Add(new ContactCandidate
                {
                    Time = 0f,
                    Kind = ContactKind.Hit,
                    Attacker = bullet.Id,
                    Source = ContactSource.Overlap,
                    AttackerHandle = GetHandle(i),
                    AttackerPassThrough = true,
                    OncePerTargetEntity = true,
                    DamageType = bullet.Definition.DamageType,
                    Point = world.GetBulletPosition(bullet.Body),
                    TargetPriority = target.Priority,
                    Target = target.OwnerId,
                    TargetShapeOrder = target.Order,
                    TargetHandle = target.Owner,
                    Falloff = 1f,
                    Damage = TotalDamage(bullet.Definition, 1f),
                });
            }
        }

        // One hit's damage of each effect, after the falloff; one tick of it for OverTime. A ship takes them one by one.
        public static float EffectDamage(BulletDefinition definition, float amount, bool falloff, float factor) =>
            amount * (falloff ? factor : 1f) * (definition.Impact == BulletImpact.OverTime ? Battle.TickDuration : 1f);

        internal static float TotalDamage(BulletDefinition definition, float falloff)
        {
            if (!definition.HasFalloff && definition.Impact != BulletImpact.OverTime) return definition.TotalDamage;
            var total = EffectDamage(definition, definition.Damage, definition.DamageFalloff, falloff);
            var extra = definition.ExtraDamage;
            for (var e = 0; e < extra.Count; ++e)
                total += EffectDamage(definition, extra[e].Amount, extra[e].Falloff, falloff);
            return total;
        }

        // Stage 9. Damage of an accepted hit on a missile; a stale or non-bullet handle is ignored.
        public void AddDamage(EntityHandle handle, float damage)
        {
            if (TryGet(handle, out var index)) _items[index].PendingDamage += damage;
        }

        // Stage 10. One pass, in the order stage 9 accepted the hits, as the resolver summed them.
        public void ApplyDamage(BattleSetup setup)
        {
            for (var i = 0; i < _count; ++i)
            {
                ref var bullet = ref _items[i];
                if (!bullet.IsAlive || !(bullet.PendingDamage > 0f)) continue;
                if (setup.IsLocalOwner(bullet.Owner)) bullet.HitPoints -= bullet.PendingDamage;
                bullet.PendingDamage = 0f;
            }
        }

        // Stage 11. A missile without hit points dies; a bullet expires after the physics of its last tick.
        // A death in the same tick wins over the expiry. A shot beam expires as a bullet does (phase 5, step 4d). A remote
        // aura (step 4g) or restoring bullet (step 5d) waits for its owner's removal record.
        public void MarkDeaths(long tick, BattleSetup setup)
        {
            for (var i = 0; i < _count; ++i)
            {
                ref var bullet = ref _items[i];
                if (!bullet.IsAlive || bullet.PendingRemoval != RemovalReason.None) continue;

                if (bullet.IsBeam)
                {
                    if (bullet.ExpiryTick == tick) bullet.PendingRemoval = RemovalReason.Expiry;
                }
                else if (bullet.Definition.IsMissile && bullet.HitPoints <= 0f && setup.IsLocalOwner(bullet.Owner))
                    bullet.PendingRemoval = RemovalReason.Death;
                else if (bullet.ExpiryTick == tick && (!bullet.Definition.HasOwnerLifetime || setup.IsLocalOwner(bullet.Owner)))
                    bullet.PendingRemoval = RemovalReason.Expiry;
            }
        }

        // Stage 12. The indices of marked bullets and of bullets with pending Hit triggers, in entity ID order, so the
        // events do not depend on storage. Insertion sort: it allocates nothing, and few bullets leave in one tick.
        public void CollectRemovals(List<int> result)
        {
            result.Clear();
            for (var i = 0; i < _count; ++i)
            {
                if (!_items[i].IsAlive || (_items[i].PendingRemoval == RemovalReason.None && _items[i].PendingHits == 0))
                    continue;

                var id = _items[i].Id.Value;
                var position = result.Count;
                result.Add(i);
                while (position > 0 && _items[result[position - 1]].Id.Value > id)
                {
                    result[position] = result[position - 1];
                    --position;
                }
                result[position] = i;
            }
        }

        private BulletEntity[] _items = new BulletEntity[64];
        private int _count;
        private int[] _free = new int[64];
        private int _freeCount;

        // Store index per physics bullet index. Valid only when the entry's Body matches.
        private int[] _byBody = new int[64];
    }
}

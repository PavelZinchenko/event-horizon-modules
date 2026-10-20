using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One bullet in the struct store (Storage). Physics owns its pose and velocity.
    internal struct BulletEntity
    {
        public int Generation;
        public bool IsAlive;

        public EntityId Id;
        public EntityId ParentId;
        public int Side;
        public int Owner;
        public BulletDefinition Definition;
        public BulletId Body;

        // The last tick the bullet flies in; stage 11 of that tick marks its expiry.
        public long ExpiryTick;

        // Set at stage 9 (stopped at a hit) or stage 11 (expired); stage 12 applies it.
        public RemovalReason PendingRemoval;
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
                if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
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

            if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
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
            return index >= 0 && _items[index].IsAlive && _items[index].Body.Equals(body);
        }

        // Stage 9. A non-bullet handle is ignored. The first reason wins.
        public void MarkRemoval(EntityHandle handle, RemovalReason reason)
        {
            if (TryGet(handle, out var index) && _items[index].PendingRemoval == RemovalReason.None)
                _items[index].PendingRemoval = reason;
        }

        // Stage 8. Each physics hit on a live ship becomes a candidate. Every physics bullet is pass-through, so
        // physics reports the first hit on each target unit; the resolver keeps the first valid one (contact ordering).
        public void CollectHits(PhysicsWorld world, ShapeTable shapes, ShipStore ships, ContactResolver resolver)
        {
            var hits = world.BulletHits;
            for (var i = 0; i < hits.Length; ++i)
            {
                ref readonly var hit = ref hits[i];
                if (!TryGetByBody(hit.Bullet, out var index)) continue;

                // Energy bolts never pair with bullets (CollisionFilters). Bullet targets come with missiles in step 7.
                if (hit.Target.IsBullet) continue;
                if (!shapes.TryGet(hit.Target.Shape, out var target) || ships.Get(target.Owner) == null) continue;

                ref readonly var bullet = ref _items[index];
                resolver.Add(new ContactCandidate
                {
                    Time = hit.Fraction,
                    Kind = ContactKind.Hit,
                    Attacker = bullet.Id,
                    TargetPriority = target.Priority,
                    Target = target.OwnerId,
                    TargetShapeOrder = target.Order,
                    Source = ContactSource.BulletSweep,
                    AttackerHandle = GetHandle(index),
                    TargetHandle = target.Owner,

                    // Q1: a cannon bullet stops at its first valid contact.
                    AttackerPassThrough = false,
                    Damage = bullet.Definition.Damage,
                    Point = hit.Point,
                });
            }
        }

        // Stage 11. A bullet expires after the physics of its last tick.
        public void MarkExpired(long tick)
        {
            for (var i = 0; i < _count; ++i)
            {
                ref var bullet = ref _items[i];
                if (bullet.IsAlive && bullet.ExpiryTick == tick && bullet.PendingRemoval == RemovalReason.None)
                    bullet.PendingRemoval = RemovalReason.Expiry;
            }
        }

        // Stage 12. The indices of marked bullets, in entity ID order, so the removal events do not depend on storage.
        // Insertion sort: it allocates nothing, and few bullets leave in one tick.
        public void CollectRemovals(List<int> result)
        {
            result.Clear();
            for (var i = 0; i < _count; ++i)
            {
                if (!_items[i].IsAlive || _items[i].PendingRemoval == RemovalReason.None) continue;

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
        private int[] _free = new int[16];
        private int _freeCount;

        // Store index per physics bullet index. Valid only when the entry's Body matches.
        private int[] _byBody = new int[64];
    }
}

using System;

namespace CombatSimulation.Core
{
    internal static class SpawnIdentity
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        // FNV-1a, little-endian fields, with a one-byte domain tag, then the Murmur3 fmix64 finalizer.
        // Root: tag 1, battle u64, roster u32, deployment u32, tick u64.
        // A roster deployment creates one root, so its spawn slot is implicit.
        internal static EntityId CreateRoot(BattleId battleId, RosterId rosterId, uint deploymentNumber, long spawnTick)
        {
            if (spawnTick < 0) throw new ArgumentOutOfRangeException(nameof(spawnTick));

            var hash = AppendByte(OffsetBasis, 1);
            hash = Append(hash, battleId.Value, 8);
            hash = Append(hash, rosterId.Value, 4);
            hash = Append(hash, deploymentNumber, 4);
            hash = Append(hash, (ulong)spawnTick, 8);
            return Finish(hash);
        }

        // Child: tag 2, parent u64, tick u64, source u32, slot u32, sequence u32.
        // Source is the device index on a ship parent, or the trigger index on a bullet parent.
        // Slot is the barrel or bay index; a bullet parent uses 0.
        // Sequence counts the spawns with the same parent, tick, source and slot, in creation order.
        internal static EntityId CreateChild(EntityId parentId, long spawnTick, uint sourceIndex, uint spawnSlot, uint sequence)
        {
            if (parentId.IsNull) throw new ArgumentException("A child requires a parent entity.", nameof(parentId));
            if (spawnTick < 0) throw new ArgumentOutOfRangeException(nameof(spawnTick));

            var hash = AppendByte(OffsetBasis, 2);
            hash = Append(hash, parentId.Value, 8);
            hash = Append(hash, (ulong)spawnTick, 8);
            hash = Append(hash, sourceIndex, 4);
            hash = Append(hash, spawnSlot, 4);
            hash = Append(hash, sequence, 4);
            return Finish(hash);
        }

        private static ulong Append(ulong hash, ulong value, int byteCount)
        {
            for (var i = 0; i < byteCount; ++i)
            {
                hash = AppendByte(hash, unchecked((byte)value));
                value >>= 8;
            }
            return hash;
        }

        private static ulong AppendByte(ulong hash, byte value) => unchecked((hash ^ value) * Prime);

        // FNV-1a leaves each low output bit a function of the same low input bits only.
        // fmix64 is a bijection, so it spreads every input bit without adding collisions.
        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value ^= value >> 33;
                value *= 0xFF51AFD7ED558CCDUL;
                value ^= value >> 33;
                value *= 0xC4CEB9FE1A85EC53UL;
                value ^= value >> 33;
                return value;
            }
        }

        private static EntityId Finish(ulong hash)
        {
            var value = Mix(hash);
            return new EntityId(value == 0 ? 1UL : value);
        }
    }
}

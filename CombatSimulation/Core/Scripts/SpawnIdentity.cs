using System;

namespace CombatSimulation.Core
{
    internal static class SpawnIdentity
    {
        private const byte RootTag = 1;
        private const byte ChildTag = 2;

        // Root: tag 1, battle u64, roster u32, deployment u32, tick u64.
        // A roster deployment creates one root, so its spawn slot is implicit.
        internal static EntityId CreateRoot(BattleId battleId, RosterId rosterId, uint deploymentNumber, long spawnTick)
        {
            if (spawnTick < 0) throw new ArgumentOutOfRangeException(nameof(spawnTick));

            var hash = StableHash.Begin(RootTag);
            hash = StableHash.Append(hash, battleId.Value, 8);
            hash = StableHash.Append(hash, rosterId.Value, 4);
            hash = StableHash.Append(hash, deploymentNumber, 4);
            hash = StableHash.Append(hash, (ulong)spawnTick, 8);
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

            var hash = StableHash.Begin(ChildTag);
            hash = StableHash.Append(hash, parentId.Value, 8);
            hash = StableHash.Append(hash, (ulong)spawnTick, 8);
            hash = StableHash.Append(hash, sourceIndex, 4);
            hash = StableHash.Append(hash, spawnSlot, 4);
            hash = StableHash.Append(hash, sequence, 4);
            return Finish(hash);
        }

        private static EntityId Finish(ulong hash)
        {
            var value = StableHash.Mix(hash);
            return new EntityId(value == 0 ? 1UL : value);
        }
    }
}

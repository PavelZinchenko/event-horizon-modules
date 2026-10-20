using System;
using Utilites;

namespace CombatSimulation.Core
{
    // What a random draw is for. A new purpose is appended; existing values never change.
    internal enum RandomPurpose : uint
    {
        SpawnPlacement = 1,
    }

    // Counter-based random (investigation 6.7): a draw is a function of match seed, entity, tick and purpose.
    // A shared sequence would diverge after one extra call on one device; a counter-based state cannot.
    internal static class CounterRandom
    {
        private const byte Tag = 3;

        // Tag 3, seed u64, entity u64, tick u64, purpose u32. The caller draws a fixed sequence from the state.
        internal static RandomState Create(ulong matchSeed, EntityId entityId, long tick, RandomPurpose purpose)
        {
            if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));

            var hash = StableHash.Begin(Tag);
            hash = StableHash.Append(hash, matchSeed, 8);
            hash = StableHash.Append(hash, entityId.Value, 8);
            hash = StableHash.Append(hash, (ulong)tick, 8);
            hash = StableHash.Append(hash, (uint)purpose, 4);
            return new RandomState { Value = PcgRandomAlgorithm.InitializeState(StableHash.Mix(hash)) };
        }
    }
}

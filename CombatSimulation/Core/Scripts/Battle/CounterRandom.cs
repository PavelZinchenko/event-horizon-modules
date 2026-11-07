using System;
using Utilites;

namespace CombatSimulation.Core
{
    // What a random draw is for. A new purpose is appended; existing values never change.
    internal enum RandomPurpose : uint
    {
        SpawnPlacement = 1,
        WeaponSpread = 2,

        // A child bullet (phase 5, step 2): pattern rotation, offset X, offset Y, speed, lifetime, in this order.
        ChildSpawn = 3,

        // A clone (phase 5, step 7c): its direction from the mothership, in whole degrees.
        CloneSpawn = 4,

        // A shot of a weapon with a random factor (phase 5, step 4a): speed, lifetime, in this order.
        ShotVariation = 5,

        // A beam's spread angle in one tick (phase 5, step 4d).
        BeamJitter = 6,

        // A homing search's camouflage roll for one candidate (phase 5, step 9a).
        MissileDodge = 7,
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

        // A draw that also depends on a second entity: the same fields, then other u64.
        internal static RandomState Create(ulong matchSeed, EntityId entityId, long tick, RandomPurpose purpose, EntityId other)
        {
            if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));

            var hash = StableHash.Begin(Tag);
            hash = StableHash.Append(hash, matchSeed, 8);
            hash = StableHash.Append(hash, entityId.Value, 8);
            hash = StableHash.Append(hash, (ulong)tick, 8);
            hash = StableHash.Append(hash, (uint)purpose, 4);
            hash = StableHash.Append(hash, other.Value, 8);
            return new RandomState { Value = PcgRandomAlgorithm.InitializeState(StableHash.Mix(hash)) };
        }
    }
}

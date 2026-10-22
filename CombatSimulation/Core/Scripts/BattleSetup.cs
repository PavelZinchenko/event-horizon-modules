using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Immutable setup shared by battle instances. It contains no mutable physics settings.
    public sealed class BattleSetup
    {
        public const int SideCount = 2;

        public BattleSetup(
            BattleId battleId,
            ulong matchSeed,
            IReadOnlyList<RosterSlotSetup> roster,
            float width = 200f,
            float height = 200f,
            long timeLimitTicks = 0,
            int maxActiveShipsPerSide = 1,
            IReadOnlyCollection<int> localOwners = null)
        {
            if (roster == null) throw new ArgumentNullException(nameof(roster));
            if (!(width > 0f) || float.IsInfinity(width))
                throw new ArgumentOutOfRangeException(nameof(width), "Arena width must be finite and positive.");
            if (!(height > 0f) || float.IsInfinity(height))
                throw new ArgumentOutOfRangeException(nameof(height), "Arena height must be finite and positive.");
            if (timeLimitTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(timeLimitTicks), "Time limit must be zero (none) or positive.");
            if (maxActiveShipsPerSide < 1)
                throw new ArgumentOutOfRangeException(nameof(maxActiveShipsPerSide), "A side must allow at least one active ship.");

            var slots = new RosterSlotSetup[roster.Count];
            var rosterIds = new HashSet<RosterId>();
            var slotsPerSide = new int[SideCount];
            for (var i = 0; i < slots.Length; ++i)
            {
                var slot = roster[i] ?? throw new ArgumentException("Roster contains a null slot.", nameof(roster));
                if (!rosterIds.Add(slot.RosterId))
                    throw new ArgumentException($"Duplicate roster ID {slot.RosterId}.", nameof(roster));
                slotsPerSide[slot.Side]++;
                slots[i] = slot;
            }

            for (var side = 0; side < SideCount; ++side)
                if (slotsPerSide[side] == 0)
                    throw new ArgumentException($"Side {side} has no roster slot.", nameof(roster));

            BattleId = battleId;
            MatchSeed = matchSeed;
            Roster = Array.AsReadOnly(slots);
            Width = width;
            Height = height;
            TimeLimitTicks = timeLimitTicks;
            MaxActiveShipsPerSide = maxActiveShipsPerSide;
            _localOwners = new HashSet<int>();
            if (localOwners == null)
            {
                foreach (var slot in slots) _localOwners.Add(slot.Owner);
            }
            else
            {
                foreach (var owner in localOwners)
                {
                    if (owner < 0) throw new ArgumentOutOfRangeException(nameof(localOwners));
                    _localOwners.Add(owner);
                }
            }
        }

        public BattleId BattleId { get; }
        public ulong MatchSeed { get; }

        // Slots in setup order, as a read-only copy; the caller's list can change without effect.
        public IReadOnlyList<RosterSlotSetup> Roster { get; }

        public float Width { get; }
        public float Height { get; }

        // Ticks both sides must have an active ship before the battle ends in a draw. Zero means no limit.
        public long TimeLimitTicks { get; }

        public int MaxActiveShipsPerSide { get; }

        public bool IsLocalOwner(int owner) => _localOwners.Contains(owner);

        private readonly HashSet<int> _localOwners;
    }
}

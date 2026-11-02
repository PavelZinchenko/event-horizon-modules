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
            IReadOnlyCollection<int> localOwners = null,
            IReadOnlyDictionary<int, int> ownerDelays = null,
            DamageTypeTable damageTypes = null)
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

            DamageTypes = damageTypes ?? LegacyDamageModel.Types;
            var checkedShips = new HashSet<ShipDefinition>();
            foreach (var slot in slots) ValidateDamageTypes(slot.Ship, checkedShips);

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
            foreach (var slot in slots)
            {
                _owners.Add(slot.Owner);
                if (!IsLocalOwner(slot.Owner)) HasRemoteOwners = true;
            }
            if (ownerDelays != null)
                foreach (var pair in ownerDelays)
                {
                    if (!_owners.Contains(pair.Key) || pair.Value < 0 || pair.Value > 5)
                        throw new ArgumentOutOfRangeException(nameof(ownerDelays), "Known owners require delays from 0 to 5 ticks.");
                    _ownerDelays.Add(pair.Key, pair.Value);
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

        // D4. The legacy table by default.
        public DamageTypeTable DamageTypes { get; }

        public bool IsLocalOwner(int owner) => _localOwners.Contains(owner);
        public bool HasRemoteOwners { get; }
        public bool HasOwner(int owner) => _owners.Contains(owner);
        public int OwnerDelay(int owner) => _ownerDelays.TryGetValue(owner, out var delay) ? delay : 0;

        // Every damage type a ship, its devices or its drones name must exist in the table.
        private void ValidateDamageTypes(ShipDefinition ship, HashSet<ShipDefinition> checkedShips)
        {
            if (!checkedShips.Add(ship)) return;
            if (ship.Resistances.Count != 0 && ship.Resistances.Count != DamageTypes.Count)
                throw new ArgumentException(
                    $"A ship has {ship.Resistances.Count} resistances; the battle has {DamageTypes.Count} damage types.");
            foreach (var device in ship.Devices)
            {
                var type = device switch
                {
                    CannonDefinition cannon => cannon.Bullet.DamageType,
                    BeamDefinition beam => beam.DamageType,
                    PointDefenseDefinition pointDefense => pointDefense.DamageType,
                    _ => 0,
                };
                if (!DamageTypes.Contains(type))
                    throw new ArgumentException($"Damage type {type} does not exist in the battle's table.");
                if (device is DroneBayDefinition bay) ValidateDamageTypes(bay.Drone, checkedShips);
            }
        }

        private readonly HashSet<int> _localOwners;
        private readonly HashSet<int> _owners = new HashSet<int>();
        private readonly Dictionary<int, int> _ownerDelays = new Dictionary<int, int>();
    }
}

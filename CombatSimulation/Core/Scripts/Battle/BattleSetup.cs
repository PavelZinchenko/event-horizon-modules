using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Immutable setup shared by battle instances. It contains no mutable physics settings and no game rules: the rules
    // are a separate object (IBattleRules) that it holds.
    public sealed class BattleSetup
    {
        public const int SideCount = 2;

        // D9: above about 4096 one float step is a tenth of the contact slop. 2048 keeps a margin.
        public const float MaxWorldSize = 2048f;

        public BattleSetup(
            BattleId battleId,
            ulong matchSeed,
            IReadOnlyList<UnitSetup> roster,
            float width = 200f,
            float height = 200f,
            IBattleRules rules = null,
            IReadOnlyCollection<int> localOwners = null,
            IReadOnlyDictionary<int, int> ownerDelays = null,
            DamageTypeTable damageTypes = null,
            IReadOnlyList<AsteroidSetup> asteroids = null,
            IReadOnlyList<ZoneSetup> zones = null,
            bool continueAfterResult = false)
        {
            if (roster == null) throw new ArgumentNullException(nameof(roster));
            if (!(width > 0f) || width > MaxWorldSize)
                throw new ArgumentOutOfRangeException(nameof(width), $"Arena width must be positive and at most {MaxWorldSize}.");
            if (!(height > 0f) || height > MaxWorldSize)
                throw new ArgumentOutOfRangeException(nameof(height), $"Arena height must be positive and at most {MaxWorldSize}.");

            var slots = new UnitSetup[roster.Count];
            var rosterIds = new HashSet<UnitId>();
            for (var i = 0; i < slots.Length; ++i)
            {
                var slot = roster[i] ?? throw new ArgumentException("Roster contains a null slot.", nameof(roster));
                if (!rosterIds.Add(slot.UnitId))
                    throw new ArgumentException($"Duplicate roster ID {slot.UnitId}.", nameof(roster));
                slots[i] = slot;
            }

            DamageTypes = damageTypes ?? LegacyDamageModel.Types;
            var checkedShips = new HashSet<ShipDefinition>();
            foreach (var slot in slots) ValidateDamageTypes(slot.Ship, checkedShips);

            BattleId = battleId;
            MatchSeed = matchSeed;
            Units = Array.AsReadOnly(slots);
            Width = width;
            Height = height;
            Rules = rules;
            _localOwners = new HashSet<int>();
            if (localOwners == null)
            {
                // Owner 0 too: a mode adds units of owner 0 to a setup that starts without units.
                _localOwners.Add(0);
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

            var rocks = new AsteroidSetup[asteroids?.Count ?? 0];
            for (var i = 0; i < rocks.Length; ++i)
                rocks[i] = asteroids[i] ?? throw new ArgumentException("Asteroids contain a null entry.", nameof(asteroids));
            // Owner question 2 (a): nobody owns an asteroid, so a battle with a remote owner has none until a network task
            // gives them an authority.
            if (rocks.Length > 0 && HasRemoteOwners)
                throw new ArgumentException("A battle with remote owners has no asteroids.", nameof(asteroids));
            Asteroids = Array.AsReadOnly(rocks);

            var areas = new ZoneSetup[zones?.Count ?? 0];
            for (var i = 0; i < areas.Length; ++i)
            {
                areas[i] = zones[i] ?? throw new ArgumentException("Zones contain a null entry.", nameof(zones));
                if (!DamageTypes.Contains(areas[i].DamageType))
                    throw new ArgumentException($"Damage type {areas[i].DamageType} does not exist in the battle's table.",
                        nameof(zones));
            }
            // As an asteroid, a zone has no owner to give it an authority.
            if (areas.Length > 0 && HasRemoteOwners)
                throw new ArgumentException("A battle with remote owners has no zones.", nameof(zones));
            Zones = Array.AsReadOnly(areas);

            // D12: Main's online duel stops at its result; the peers would have to agree on the ticks after it.
            if (continueAfterResult && HasRemoteOwners)
                throw new ArgumentException("A battle with remote owners stops at its result.", nameof(continueAfterResult));
            ContinueAfterResult = continueAfterResult;

            rules?.Validate(this);
        }

        public BattleId BattleId { get; }
        public ulong MatchSeed { get; }

        // The initial units in setup order, as a read-only copy; the caller's list can change without effect. It can be
        // empty: a mode adds units during the battle.
        public IReadOnlyList<UnitSetup> Units { get; }

        public float Width { get; }
        public float Height { get; }

        // The game rules (battle modes). Null: an open battle without a deployment limit or a result.
        public IBattleRules Rules { get; }

        // Phase 5, step 14: created before tick 0, in this order. Empty with remote owners.
        public IReadOnlyList<AsteroidSetup> Asteroids { get; }

        // Damage zones (exploration objects): created before tick 0 after the asteroids, in this order. Empty with remote
        // owners.
        public IReadOnlyList<ZoneSetup> Zones { get; }

        // Battle continuity (D12): the battle steps and takes commands after its result. The first result stays; the
        // mode keeps running. False: Step and Submit throw after the result. Local battles only.
        public bool ContinueAfterResult { get; }

        // D4. The legacy table by default.
        public DamageTypeTable DamageTypes { get; }

        public bool IsLocalOwner(int owner) => _localOwners.Contains(owner);
        public bool HasRemoteOwners { get; }
        public bool HasOwner(int owner) => _owners.Contains(owner);
        public int OwnerDelay(int owner) => _ownerDelays.TryGetValue(owner, out var delay) ? delay : 0;

        // A ship that a mode adds (ModeContext.AddUnit).
        internal void ValidateDamageTypes(ShipDefinition ship) => ValidateDamageTypes(ship, new HashSet<ShipDefinition>());

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
                var effects = device switch
                {
                    CannonDefinition cannon => cannon.Bullet.Effects,
                    BeamDefinition beam => beam.Effects,
                    _ => default,
                };
                if (effects.RepairDamage > 0f && !DamageTypes.Contains(effects.RepairDamageType))
                    throw new ArgumentException($"Repair damage type {effects.RepairDamageType} does not exist in the battle's table.");
                if (device.ChildShip != null) ValidateDamageTypes(device.ChildShip, checkedShips);
            }
        }

        private readonly HashSet<int> _localOwners;
        private readonly HashSet<int> _owners = new HashSet<int>();
        private readonly Dictionary<int, int> _ownerDelays = new Dictionary<int, int>();
    }
}

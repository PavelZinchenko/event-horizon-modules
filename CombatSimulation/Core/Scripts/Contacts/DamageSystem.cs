using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Damage (phase 5, step 8b, item 7). Stage 10: tail segment hits, the pending damage of each ship, the overtime
    // drain, the reactions to lethal damage, then bullet damage. Stage 11: deaths and bullet expiry.
    internal sealed class DamageSystem
    {
        public DamageSystem(BattleWorld world, Battle battle, WormTails tails)
        {
            _world = world;
            _battle = battle;
            _tails = tails;
        }

        private BattleSetup Setup => _world.Setup;
        private long Tick => _world.Tick;

        // Requests a death at stage 11 of the current tick. The damage stage adds its deaths to the same list.
        public void QueueDeath(EntityId id) => _deathRequests.Add(id);

        // Stage 10. Tail segments first: the damage a tail cannot take passes to its ship (phase 5, step 7d). Then one
        // pass, shield first, then armor (legacy Impact.ApplyShield and ShipStats.ApplyDamage).
        // Received hits add to a local target's pending damage. A remote target's pending damage is only the damage
        // this device sent it (N3): a prediction that corrections replace.
        public void Apply()
        {
            _tails.ApplySegmentHits();
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null || !ship.HasPendingDamage) continue;

                ApplyShieldFirst(ship);
                ship.ClearPendingDamage();
            }

            DrainPlayerShips();
            ReactToLethalDamage();
            _world.Bullets.ApplyDamage(Setup);
        }

        private void ApplyShieldFirst(ShipEntity ship) =>
            ApplyShieldFirst(ref ship.Shield, ref ship.Armor, ref ship.Energy, ship.PendingDamage, ship.PendingEffects,
                ship.Definition, ship.ResistanceShare);

        // Legacy Impact.ApplyShield and ShipStats.ApplyDamage, by damage type (D4). A shield drain takes the shield
        // first (phase 5, step 5a). The shield left meets the types group by group in shield order; each type's load is
        // its damage less its shield resistance. A group larger than the shield left keeps the same share of each type.
        // Armor then takes what passed, less its armor resistance and the repair, and energy gains its share of what
        // passed before the energy drain. damage holds raw amounts by type; this changes it to what passed.
        public void ApplyShieldFirst(ref ResourcePool shield, ref ResourcePool armor, ref ResourcePool energy,
            float[] damage, in PendingEffects effects, ShipDefinition definition, float resistanceShare)
        {
            var types = Setup.DamageTypes;
            var walk = types.ShieldWalk;
            var shieldDrain = effects.ShieldDrain - effects.ShieldRecharge;
            // A recharge nets against shield loss, but cannot absorb damage until the next tick (legacy ApplyShield).
            var drainBeforeDamage = shieldDrain > 0f ? shieldDrain : 0f;
            var left = shield.Max > 0f && shield.Value > drainBeforeDamage ? shield.Value - drainBeforeDamage : 0f;
            var absorbed = 0f;
            for (var start = 0; start < walk.Length && left > 0f;)
            {
                var order = types[walk[start]].ShieldOrder;
                var end = start;
                var load = 0f;
                for (; end < walk.Length && types[walk[end]].ShieldOrder == order; ++end)
                    load += damage[walk[end]] * (1f - definition.Resistance(walk[end]).Shield);

                if (load > 0f)
                {
                    if (load <= left)
                    {
                        for (var i = start; i < end; ++i) damage[walk[i]] = 0f;
                        absorbed += load;
                        left -= load;
                    }
                    else
                    {
                        for (var i = start; i < end; ++i) damage[walk[i]] -= left * damage[walk[i]] / load;
                        absorbed += left;
                        left = 0f;
                    }
                }
                start = end;
            }
            var shieldLoss = absorbed + shieldDrain;
            if (shieldLoss != 0f) shield.Consume(shieldLoss);

            var armorDamage = 0f;
            var energyGain = 0f;
            for (var type = 0; type < damage.Length; ++type)
            {
                if (!(damage[type] > 0f)) continue;
                armorDamage += damage[type] * (1f - ArmorResistance(types, definition, type, resistanceShare));
                energyGain += damage[type] * definition.Resistance(type).EnergyGain;
            }
            armor.Consume(armorDamage - effects.Repair);
            // A gain does not restart the energy delay (legacy Energy.Get with a negative amount).
            if (energyGain > 0f) energy.Consume(-energyGain);
            var energyDrain = effects.EnergyDrain - effects.EnergyRecharge;
            if (energyDrain != 0f) energy.Consume(energyDrain);
        }

        // A resistance share (ShipStatus) moves the armor resistance toward the ceiling of its type.
        public static float ArmorResistance(DamageTypeTable types, ShipDefinition definition, int type,
            float resistanceShare)
        {
            var armor = definition.Resistance(type).Armor;
            return resistanceShare > 0f ? armor + resistanceShare * (types[type].ResistanceCeiling - armor) : armor;
        }

        // End of stage 10 (phase 5, step 7, H3): every hit of the tick counts first. A device may save a local ship
        // that has no armor left; stage 11 then finds none to mark. A remote copy follows its owner's record.
        private void ReactToLethalDamage()
        {
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null || !Setup.IsLocalOwner(ship.Owner) || ship.Armor.Value > 0f) continue;
                for (var d = 0; d < ship.DeviceCount; ++d)
                    if (ship.GetDevice(d).OnLethalDamage(_battle.DeviceContext, ship)) break;
            }
        }

        // Legacy CombatTimer, DrainPlayerHp (TM3): after the limit, each active player roster ship loses k % of its
        // maximum armor and of its maximum shield per second, k being the whole overtime seconds already passed.
        // No resistances; both restart their delay. It reads the timer of the previous tick. Only the owner drains;
        // a remote copy follows through corrections. Drones are not drained.
        private void DrainPlayerShips()
        {
            if (Setup.TimerMode != TimerMode.DrainPlayerHp || Setup.TimeLimitTicks <= 0) return;
            var overtime = _battle.TimerElapsedTicks - Setup.TimeLimitTicks;
            if (overtime < DrainTicksPerSecond) return;

            var fraction = overtime / DrainTicksPerSecond * 0.01f * Battle.TickDuration;
            foreach (var slot in _world.Roster.Slots)
            {
                var ship = slot.ActiveShip;
                if (slot.Side != Setup.PlayerSide || ship == null || !Setup.IsLocalOwner(ship.Owner)) continue;
                ship.Armor.Consume(fraction * ship.Armor.Max);
                ship.Shield.Consume(fraction * ship.Shield.Max);
            }
        }

        private const long DrainTicksPerSecond = 50;

        // Stage 11. Marks deaths and bullet expiry; stage 12 applies them. Armor 0 is a death; so are missile hit points 0.
        public void MarkDeaths()
        {
            _world.Bullets.MarkDeaths(Tick, Setup);

            for (var i = 0; i < _deathRequests.Count; ++i)
            {
                var ship = _world.FindShip(_deathRequests[i]);
                if (ship != null && Setup.IsLocalOwner(ship.Owner)) ship.PendingDeath = true;
            }

            _deathRequests.Clear();

            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship != null && Setup.IsLocalOwner(ship.Owner) && ship.Armor.Value <= 0f) ship.PendingDeath = true;
            }
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly WormTails _tails;
        private readonly List<EntityId> _deathRequests = new List<EntityId>();
    }
}

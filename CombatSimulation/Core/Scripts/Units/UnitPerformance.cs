using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // What a unit did in the battle, as legacy Frontier's ShipPerformance counts it: for experience and the battle
    // statistics. A unit's drones and clones count for it. The counts only read the results of stage 10, so they never
    // change the battle. The application derives the combat contribution (dealt + repaired - allies) and experience.
    public struct UnitPerformance
    {
        // Damage to hostile ships and drones after resistances. An energy shield device's absorption is shield damage.
        public float ArmorDamageDealt { get; internal set; }
        public float ShieldDamageDealt { get; internal set; }

        // Armor and shield damage to friendly ships and drones.
        public float DamageToAllies { get; internal set; }

        // The repair amount of friendly hits (ImpactEffects.Repair).
        public float DamageRepaired { get; internal set; }

        // Damage that the unit's ships took, from any source. Legacy adds the armor damage to the received shield damage
        // (a bug); the core counts the shield damage (owner, 2026-11-17).
        public float ArmorDamageReceived { get; internal set; }
        public float ShieldDamageReceived { get; internal set; }

        // Hostile unit ships that died in a tick where this unit dealt their last armor damage. Drones do not count.
        public int Kills { get; internal set; }

        // Deaths of the unit's ship (Killed transitions).
        public int Deaths { get; internal set; }
    }

    // Stage 9 credits each accepted hit on a ship to the attacker's unit; stage 10 splits each target's shield and armor
    // loss among its attackers by their share of the target's raw damage in the tick. With one attacker in a tick, the
    // usual case, the split is exact. Damage without an attacking unit (an asteroid, a tail segment, a remote hit, the
    // mode's drain) is counted as received only. Only local targets settle: a remote copy's damage is a prediction.
    internal sealed class PerformanceLedger
    {
        public void AddDamage(ShipEntity target, Unit attacker, float amount, bool hostile)
        {
            if (attacker == null || target.IsDecoy || !(amount > 0f)) return;
            _credits.Add(new Credit(target, attacker, amount, hostile));
        }

        public static void AddRepair(Unit attacker, float amount)
        {
            if (attacker == null || !(amount > 0f)) return;
            attacker.Performance.DamageRepaired += amount;
        }

        // Stage 10, after the target's damage applied. rawDamage: all damage the target took in the tick before shields
        // and resistances; shieldLoss includes an energy shield device's absorption.
        public void Settle(ShipEntity target, long tick, float rawDamage, float shieldLoss, float armorLoss)
        {
            if (shieldLoss < 0f) shieldLoss = 0f;
            if (armorLoss < 0f) armorLoss = 0f;
            var unit = target.Slot;
            if (unit != null)
            {
                unit.Performance.ArmorDamageReceived += armorLoss;
                unit.Performance.ShieldDamageReceived += shieldLoss;
            }
            if (!(rawDamage > 0f)) return;

            Unit last = null;
            foreach (var credit in _credits)
            {
                if (credit.Target != target) continue;
                var share = credit.Amount / rawDamage;
                if (credit.Hostile)
                {
                    credit.Attacker.Performance.ArmorDamageDealt += share * armorLoss;
                    credit.Attacker.Performance.ShieldDamageDealt += share * shieldLoss;
                    last = credit.Attacker;
                }
                else credit.Attacker.Performance.DamageToAllies += share * (armorLoss + shieldLoss);
            }
            if (last != null && armorLoss > 0f)
            {
                target.KillCredit = last;
                target.KillCreditTick = tick;
            }
        }

        // End of stage 10.
        public void Clear() => _credits.Clear();

        // Stage 12: a unit ship died. The unit counts the death; the last hostile attacker of the tick, the kill.
        public static void RecordDeath(ShipEntity ship, long tick)
        {
            if (ship.Slot == null) return;
            ship.Slot.Performance.Deaths++;
            if (ship.KillCreditTick == tick && ship.KillCredit != null) ship.KillCredit.Performance.Kills++;
        }

        private readonly struct Credit
        {
            public readonly ShipEntity Target;
            public readonly Unit Attacker;
            public readonly float Amount;
            public readonly bool Hostile;

            public Credit(ShipEntity target, Unit attacker, float amount, bool hostile)
            {
                Target = target;
                Attacker = attacker;
                Amount = amount;
                Hostile = hostile;
            }
        }

        private readonly List<Credit> _credits = new List<Credit>();
    }
}

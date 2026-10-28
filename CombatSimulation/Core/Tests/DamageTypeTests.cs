using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // D4: damage types and resistances with the legacy formula. The examples are the agreed table in
    // TODO/battle_simulator_phase4.md, "Damage examples (D4)".
    public class DamageTypeTests
    {
        private const float Tolerance = 1e-3f;

        // Kinetic 0.5, energy 0.2, heat 0.4 (corrosive 0.1), shield corrosive 0.9.
        private static DamageResistance[] Resistances(float energyAbsorption = 0f) =>
            LegacyDamageModel.Resistances(0.5f, 0.2f, 0.4f, 0.9f, energyAbsorption);

        // Ship 1 (owner 10, local) takes hits from owner 20 at tick 0. Nothing recharges.
        private static Driver Target(float shield = 20f, DamageResistance[] resistances = null)
        {
            var ship = Fixtures.ShipWith(armor: 200f, shield: new ResourceDefinition(shield),
                energy: new ResourceDefinition(300f), resistances: resistances ?? Resistances());
            var setup = new BattleSetup(new BattleId(77), 13, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, 20),
            }, localOwners: new[] { 10 });
            var driver = new Driver(setup);
            driver.Deploy(1);
            driver.Step();
            return driver;
        }

        // The hits arrive together, so stage 10 applies them in one pass.
        private static ShipEntity Apply(Driver target, params (int Type, float Amount)[] hits)
        {
            for (var i = 0; i < hits.Length; ++i)
            {
                var hit = new ResolvedHit(target.BattleId, 0, new EntityId(999), 20, 0, target.Entity(1), 10, 0,
                    HitContactState.Start, HitImpactKind.Impact, hits[i].Amount, damageType: (byte)hits[i].Type);
                Assert.IsTrue(target.Battle.Submit(new InboundHit(20, hit.WithSequence((ulong)i + 1))));
            }
            target.Step();
            return target.Ship(1);
        }

        private static void AssertState(ShipEntity ship, float shield, float armor)
        {
            Assert.AreEqual(shield, ship.Shield.Value, Tolerance, "shield");
            Assert.AreEqual(armor, ship.Armor.Value, Tolerance, "armor");
        }

        [Test]
        public void LegacyModelBuildsTheCorrosiveRowFromTheOtherResistances()
        {
            var r = LegacyDamageModel.Resistances(0.5f, 0.2f, 0.4f, 0.9f, 0.25f, 2f);
            Assert.AreEqual(4, r.Length);
            Assert.AreEqual(0.5f, r[LegacyDamageModel.Impact].Armor);
            Assert.AreEqual(0.2f, r[LegacyDamageModel.Energy].Armor);
            Assert.AreEqual(0.125f, r[LegacyDamageModel.Energy].EnergyGain, "Absorption over the armor multiplier.");
            Assert.AreEqual(0.4f, r[LegacyDamageModel.Heat].Armor);
            Assert.AreEqual(0.1f, r[LegacyDamageModel.Corrosive].Armor, 1e-6f, "Half of the smallest resistance.");
            Assert.AreEqual(0.9f, r[LegacyDamageModel.Corrosive].Shield);
            Assert.AreEqual(0f, r[LegacyDamageModel.Impact].Shield);
            Assert.AreEqual(0f, LegacyDamageModel.Resistances(0.5f, 0.2f, 0.4f, 0.9f, 0.01f)[1].EnergyGain,
                "Legacy ignores absorption up to 0.01.");
        }

        [Test]
        public void Example1ArmorTakesImpactLessItsResistance() =>
            AssertState(Apply(Target(shield: 0f), (LegacyDamageModel.Impact, 10f)), 0f, 195f);

        [Test]
        public void Example2TheShieldTakesTheWholeHit() =>
            AssertState(Apply(Target(), (LegacyDamageModel.Impact, 10f)), 10f, 200f);

        [Test]
        public void Example3ArmorTakesWhatPassesTheShield() =>
            AssertState(Apply(Target(), (LegacyDamageModel.Impact, 30f)), 0f, 195f);

        [Test]
        public void Example4TypesInOneShieldGroupKeepTheirShares() =>
            AssertState(Apply(Target(), (LegacyDamageModel.Impact, 20f), (LegacyDamageModel.Energy, 20f)), 0f, 187f);

        [Test]
        public void Example5CorrosiveMeetsTheShieldLessItsShieldResistance() =>
            AssertState(Apply(Target(), (LegacyDamageModel.Corrosive, 100f)), 10f, 200f);

        [Test]
        public void Example6CorrosiveMeetsTheShieldFirst()
        {
            // The impact hit comes first in the stream; the shield order still puts corrosive damage first.
            var ship = Apply(Target(), (LegacyDamageModel.Impact, 20f), (LegacyDamageModel.Corrosive, 300f));
            AssertState(ship, 0f, 100f);
        }

        [Test]
        public void Example7EnergyDamageAfterTheShieldRefillsEnergy()
        {
            var target = Target(shield: 0f, resistances: Resistances(energyAbsorption: 0.25f));
            var ship = target.Ship(1);
            ship.Energy.Value = 100f;
            Apply(target, (LegacyDamageModel.Energy, 20f));
            AssertState(ship, 0f, 184f);
            Assert.AreEqual(105f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(0f, ship.Energy.DelayRemaining, "A gain does not restart the delay.");
        }

        [Test]
        public void Example8AShipWithoutResistancesTakesEveryTypeInFull()
        {
            var target = Target(shield: 0f, resistances: Array.Empty<DamageResistance>());
            AssertState(Apply(target, (LegacyDamageModel.Heat, 10f), (LegacyDamageModel.Corrosive, 5f)), 0f, 185f);
        }

        [Test]
        public void RammingDealsTheTableRammingType()
        {
            // DamageTests: 600 ramming damage per ship, 500 to the shield. Kinetic 0.5 halves the 100 that pass.
            var heavy = Fixtures.ShipWith(mass: 100f, resistances: LegacyDamageModel.Resistances(0.5f, 0f, 0f, 0.9f));
            var driver = RammingTests.Duel(heavy, heavy);
            driver.Place(1, new Vec2(50f, 50f), new Vec2(60f, 0f));
            driver.Place(101, new Vec2(52.2f, 50f), Vec2.Zero);
            driver.Step(3);
            AssertState(driver.Ship(101), 0f, 950f);
        }

        [Test]
        public void PredictionAndReplayUseTheCopyResistances()
        {
            // 50 corrosive meets the shield as 5. Without the copy's resistances the replay would restore 450.
            var gun = Fixtures.CannonShip(new BulletDefinition(0.25f, 20f, 10f, 50f, damageType: LegacyDamageModel.Corrosive),
                energyCost: 0f);
            var target = Fixtures.ShipWith(shield: new ResourceDefinition(500f), resistances: Resistances());
            var p = new NetPair(gun, target);
            foreach (var driver in new[] { p.A, p.B })
            {
                driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            }
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            p.Step();
            p.B.Control(1, systems: 0);
            var copy = p.B.Ship(101);
            while (copy.Shield.Value == 500f) p.Step();
            Assert.AreEqual(495f, copy.Shield.Value, Tolerance);
            Assert.AreEqual(LegacyDamageModel.Corrosive, p.HeldHits[0].DamageType);

            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            Assert.AreEqual(495f, copy.Shield.Value, Tolerance);

            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step();
            Assert.AreEqual(495f, p.A.Ship(101).Shield.Value, Tolerance, "The owner applies the type it received.");
        }

        [Test]
        public void SetupRejectsUnknownTypesAndWrongResistanceCounts()
        {
            BattleSetup Setup(ShipDefinition ship) => Fixtures.Setup(ship);

            Assert.Throws<ArgumentException>(() => Setup(Fixtures.ShipWith(resistances: new DamageResistance[2])));
            Assert.Throws<ArgumentException>(() => Setup(Fixtures.CannonShip(new BulletDefinition(0.25f, 20f, 10f, 5f, damageType: 4))));
            var bay = new DroneBayDefinition(1, 0f, Fixtures.ShipWith(resistances: new DamageResistance[3]));
            Assert.Throws<ArgumentException>(() => Setup(Fixtures.ShipWith(devices: new DeviceDefinition[] { bay })));
            Assert.DoesNotThrow(() => Setup(Fixtures.ShipWith(resistances: Resistances())));

            // Point defense deals energy (type 1) by default, which a one-type table does not have.
            var single = new DamageTypeTable(new[] { new DamageTypeDefinition(0) }, 0);
            var defender = Fixtures.ShipWith(devices: new DeviceDefinition[] { new PointDefenseDefinition(1f, 1f, 1f) });
            Assert.Throws<ArgumentException>(() => new BattleSetup(new BattleId(1), 7, new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, defender),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
            }, damageTypes: single));
            Assert.DoesNotThrow(() => new BattleSetup(new BattleId(1), 7, Fixtures.Roster(1, 1), damageTypes: single));

            Assert.Throws<ArgumentOutOfRangeException>(() => new DamageTypeTable(Array.Empty<DamageTypeDefinition>(), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DamageTypeTable(new[] { new DamageTypeDefinition(0) }, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(resistances: new[] { new DamageResistance(1.5f) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BulletDefinition(0.25f, 20f, 10f, 5f, damageType: -1));
        }
    }
}

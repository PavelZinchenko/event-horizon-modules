using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 6a: the saw's circle and shield bypass, including remote ownership.
    public class ShieldBypassTests
    {
        private static readonly BulletDefinition Saw = new BulletDefinition(2f, 0f, 0f, 500f,
            impact: BulletImpact.OverTime, lifetime: 1f, scale: 4f, attachedToParent: true, aura: true,
            effects: new ImpactEffects(ignoreShield: true), circleCenter: new Vec2(1.6f, 0f));

        private static ShipDefinition SawShip() => Fixtures.ShipWith(
            barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
            devices: new[] { new CannonDefinition(0, 2f, 0f, 0f, Saw, CannonMode.Continuous) });

        private static ShipDefinition Target(float resistance = 0f) => Fixtures.ShipWith(armor: 2000f,
            shield: new ResourceDefinition(100f), resistances: LegacyDamageModel.Resistances(resistance, 0f, 0f, 0f));

        [Test]
        public void TheSawGrowsForwardAndDamagesArmorOnlyWhileTouching()
        {
            var driver = CannonTests.Duel(SawShip(), Target(0.2f));
            // Muzzle x=51; target's near edge x=54. Full reach is 3.6, so contact starts at growth tick 42.
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 60);
            var hits = driver.StepCollecting(120).Where(e => e.Kind == BattleEventKind.Hit).ToArray();
            Assert.AreEqual(43, hits.First().Tick);
            Assert.AreEqual(67, hits.Last().Tick);
            Assert.AreEqual(25, hits.Length);
            Assert.IsTrue(hits.All(h => System.Math.Abs(h.Damage - 10f) < 0.001f));
            Assert.AreEqual(1800f, driver.Ship(101).Armor.Value, 0.001f);
            Assert.AreEqual(100f, driver.Ship(101).Shield.Value);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ARegularHitInTheSameTickStillMeetsTheShield(bool reverse)
        {
            var driver = CannonTests.Duel(Fixtures.Ship, Target());
            var ship = driver.Ship(101);
            ship.AddPendingDamage(LegacyDamageModel.Impact, reverse ? 30f : 10f, !reverse);
            ship.AddPendingDamage(LegacyDamageModel.Impact, reverse ? 10f : 30f, reverse);
            driver.Step();
            Assert.AreEqual(70f, ship.Shield.Value);
            Assert.AreEqual(1990f, ship.Armor.Value);
            driver.Step();
            Assert.AreEqual(1990f, ship.Armor.Value, "Pending bypass damage must clear after the tick.");
        }

        [Test]
        public void BypassStillUsesArmorResistanceAndEnergyAbsorption()
        {
            var target = Fixtures.ShipWith(shield: new ResourceDefinition(100f),
                energy: new ResourceDefinition(100f), resistances: LegacyDamageModel.Resistances(0f, 0.5f, 0f, 0f, 0.25f));
            var driver = CannonTests.Duel(Fixtures.Ship, target);
            var ship = driver.Ship(101);
            ship.Energy.Value = 0f;
            ship.AddPendingDamage(LegacyDamageModel.Energy, 40f, true);
            driver.Step();
            Assert.AreEqual(100f, ship.Shield.Value);
            Assert.AreEqual(980f, ship.Armor.Value);
            Assert.AreEqual(10f, ship.Energy.Value);
        }

        [Test]
        public void AChargedSawKeepsBypassAndScalesItsCircle()
        {
            var charged = Saw.WithCharge(0.5f);
            Assert.IsTrue(charged.Effects.IgnoreShield);
            Assert.AreEqual(charged.Radius * 0.8f, charged.CircleCenter.X, 0.001f);
            Assert.Less(charged.Radius, Saw.Radius);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemoteHitsAndCorrectionsKeepBypass(bool overTime)
        {
            var bullet = overTime ? Saw : new BulletDefinition(0.25f, 20f, 10f, 10f,
                damageType: LegacyDamageModel.Heat, push: 0.1f, effects: new ImpactEffects(ignoreShield: true),
                extraDamage: new[] { new DamageAmount(LegacyDamageModel.Energy, 5f) });
            var pair = new NetPair(overTime ? SawShip() : Fixtures.CannonShip(bullet, cooldown: 10f, energyCost: 0f), Target());
            foreach (var driver in new[] { pair.A, pair.B })
            {
                driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            }
            pair.HoldHitsFromB = true;
            pair.B.Control(1, systems: 1);
            pair.Step(55);
            pair.B.Control(1, systems: 0);
            pair.Step(60);
            Assert.IsNotEmpty(pair.HeldHits);
            Assert.IsTrue(pair.HeldHits.All(h => h.Effect == HitEffect.DamageIgnoringShield));
            Assert.AreEqual(overTime ? 500f : 10f, pair.HeldHits[0].Amount);
            var predicted = pair.B.Ship(101).Armor.Value;
            Assert.Less(predicted, 2000f);
            Assert.AreEqual(100f, pair.B.Ship(101).Shield.Value);
            Assert.AreEqual(2000f, pair.A.Ship(101).Armor.Value);
            Assert.AreEqual(1, pair.Correct(pair.A, pair.B));
            pair.Step();
            Assert.AreEqual(predicted, pair.B.Ship(101).Armor.Value, 0.001f);
            Assert.AreEqual(100f, pair.B.Ship(101).Shield.Value);
            pair.ReleaseHits();
            pair.HoldHitsFromB = false;
            pair.Step(3);
            Assert.AreEqual(predicted, pair.A.Ship(101).Armor.Value, 0.001f);
            Assert.AreEqual(100f, pair.A.Ship(101).Shield.Value);
            pair.Correct(pair.A, pair.B);
            pair.Step();
            Assert.AreEqual(predicted, pair.B.Ship(101).Armor.Value, 0.001f);
            Assert.AreEqual(0, pair.B.Battle.PredictedHitCount);
        }

        [Test]
        public void TheSawDealsTenDamagePerContactTickToAMissile()
        {
            var missile = new BulletDefinition(0.2f, 15f, 30f, 0f, hitPoints: 1000f);
            var driver = CannonTests.Duel(SawShip(), Fixtures.CannonShip(missile, cooldown: 10f, energyCost: 0f));
            driver.Place(101, new Vec2(58f, 50f), Vec2.Zero, (float)System.Math.PI);
            driver.Control(1, systems: 1);
            driver.StepUntil(54);
            driver.Control(101, systems: 1);
            for (var tick = 0; tick < 30; ++tick)
            {
                driver.Step();
                var bullets = driver.Battle.Bullets;
                for (var i = 0; i < bullets.Capacity; ++i)
                    if (bullets.IsAlive(i) && bullets[i].Definition == missile && bullets[i].HitPoints < 1000f)
                    {
                        Assert.AreEqual(990f, bullets[i].HitPoints);
                        return;
                    }
            }
            Assert.Fail("The missile did not touch the saw.");
        }
    }
}

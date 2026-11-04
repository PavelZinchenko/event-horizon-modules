using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 5c: ProgressiveDamage on a beam (ImpactEffects.Ramp), the examples of "Progressive damage (5c)" in
    // the phase 5 plan, and its interval record.
    public class ProgressiveDamageTests
    {
        private const float Tolerance = 1e-3f;

        private static readonly Vec2 Shooter = new Vec2(50f, 50f);
        private static readonly Vec2 Target = new Vec2(54f, 50f);
        private static readonly Vec2 Away = new Vec2(80f, 80f);

        // LaserBeam_L_2 (HeavyLaserBeam): 50 heat per second, plus a ramp of 50 heat per second over 3 s.
        private static readonly BeamDefinition HeavyLaser = new BeamDefinition(0, 0.1f, 2f, 0f, 20f, 50f,
            LegacyDamageModel.Heat, effects: new ImpactEffects(ramp: 50f, rampTime: 3f));

        private static ShipDefinition BeamShip(BeamDefinition beam) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) }, devices: new DeviceDefinition[] { beam });

        // No shield, so every tick's damage reaches the armor.
        private static readonly ShipDefinition Hull = Fixtures.ShipWith(armor: 1000f, shield: new ResourceDefinition(0f));

        private static float Ramp(long tick) => ImpactEffects.RampTick(50f, 3f, tick);

        private static float StepDamage(Driver driver)
        {
            var armor = driver.Ship(101).Armor.Value;
            driver.Step();
            return armor - driver.Ship(101).Armor.Value;
        }

        private static void StepUntilHit(Driver driver)
        {
            for (var i = 0; i < 10; ++i)
                if (driver.StepCollecting().Any(e => e.Kind == BattleEventKind.Hit)) return;
            Assert.Fail("The beam did not hit.");
        }

        // Row 1: at T the base damage only; from T + 1, 50 + 50 x min(1, t / 3) per second; from T + 150, 100.
        [Test]
        public void TheRampGrowsFromTheSecondContactTickToItsFullPower()
        {
            var driver = CannonTests.Duel(BeamShip(HeavyLaser), Hull);
            driver.Place(101, Target, Vec2.Zero);
            driver.Control(1, systems: 1);
            StepUntilHit(driver);
            Assert.AreEqual(1f, 1000f - driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(1f + 1f / 150f, StepDamage(driver), Tolerance);
            Assert.AreEqual(1f + 2f / 150f, StepDamage(driver), Tolerance);
            driver.Step(146);
            Assert.AreEqual(1f + Ramp(149), StepDamage(driver), Tolerance);
            Assert.AreEqual(2f, StepDamage(driver), Tolerance);
            driver.Step(20);
            Assert.AreEqual(2f, StepDamage(driver), Tolerance);
        }

        // Row 2: one tick without the contact restarts the ramp. Row 3: a new beam starts at 0.
        [Test]
        public void ALostContactOrANewBeamRestartsTheRamp()
        {
            var driver = CannonTests.Duel(BeamShip(HeavyLaser), Hull);
            driver.Place(101, Target, Vec2.Zero);
            driver.Control(1, systems: 1);
            StepUntilHit(driver);
            driver.Step(50);
            Assert.AreEqual(1f + Ramp(51), StepDamage(driver), Tolerance);

            driver.Place(101, Away, Vec2.Zero);
            Assert.AreEqual(0f, StepDamage(driver));
            driver.Place(101, Target, Vec2.Zero);
            Assert.AreEqual(1f, StepDamage(driver), Tolerance);
            Assert.AreEqual(1f + Ramp(1), StepDamage(driver), Tolerance);
            driver.Step(30);

            driver.Control(1, systems: 0);
            driver.Step(10);
            driver.Control(1, systems: 1);
            StepUntilHit(driver);
            Assert.AreEqual(1f + Ramp(1), StepDamage(driver), Tolerance);
        }

        // Another first body on the ray is a new contact, even without a tick between.
        [Test]
        public void ANewFirstBodyStartsItsOwnRamp()
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, BeamShip(HeavyLaser)),
                new RosterSlotSetup(new RosterId(101), 1, Hull),
                new RosterSlotSetup(new RosterId(102), 1, Hull),
            }, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, Target, Vec2.Zero);
            driver.Place(102, Away, Vec2.Zero);
            driver.Control(1, systems: 1);
            StepUntilHit(driver);
            driver.Step(20);

            // Ship 102 moves between the beam and ship 101.
            driver.Place(102, new Vec2(52.5f, 50f), Vec2.Zero);
            var armor101 = driver.Ship(101).Armor.Value;
            var armor102 = driver.Ship(102).Armor.Value;
            driver.Step();
            Assert.AreEqual(armor101, driver.Ship(101).Armor.Value);
            Assert.AreEqual(1f, armor102 - driver.Ship(102).Armor.Value, Tolerance);
        }

        // The ramp is damage: a missile takes it too (legacy adds it to the target's impact).
        [Test]
        public void AMissileTakesTheRamp()
        {
            var missile = new BulletDefinition(0.5f, 0f, 0f, 0f, hitPoints: 1000f, lifetime: 10f);
            var driver = CannonTests.Duel(BeamShip(HeavyLaser),
                Fixtures.CannonShip(missile, cooldown: 100f, energyCost: 0f));
            driver.Place(101, Away, Vec2.Zero);
            driver.Control(101, systems: 1);
            var index = -1;
            for (var i = 0; i < 5 && index < 0; ++i)
            {
                driver.Step();
                index = MissileIndex(driver, missile);
            }
            Assert.GreaterOrEqual(index, 0, "No missile.");
            driver.Control(101, systems: 0);
            driver.Battle.World.SetBulletPose(driver.Battle.Bullets[index].Body, Target, 0f);
            driver.Control(1, systems: 1);
            for (var i = 0; i < 10 && driver.Battle.Bullets[index].HitPoints == 1000f; ++i) driver.Step();
            Assert.AreEqual(999f, driver.Battle.Bullets[index].HitPoints, Tolerance);
            var hitPoints = driver.Battle.Bullets[index].HitPoints;
            driver.Step();
            Assert.AreEqual(hitPoints - 1f - Ramp(1), driver.Battle.Bullets[index].HitPoints, Tolerance);
        }

        private static int MissileIndex(Driver driver, BulletDefinition definition)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && bullets[i].Definition == definition) return i;
            return -1;
        }

        // Network: the ramp is its own interval on shape 0. The target owner counts it from the interval's first tick,
        // also when the records arrive late; a correction before the acknowledgement replays the ramp, not a flat rate.
        [Test]
        public void ARemoteTargetOwnerCountsTheRampFromTheIntervalStart()
        {
            var p = new NetPair(BeamShip(HeavyLaser), Hull);
            foreach (var driver in new[] { p.A, p.B })
            {
                driver.Place(1, Shooter, Vec2.Zero);
                driver.Place(101, Target, Vec2.Zero);
            }
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            p.Step(100);
            p.B.Control(1, systems: 0);
            p.Step(5);

            var ramp = p.HeldHits.Single(h => h.Effect == HitEffect.ProgressiveDamage && h.ContactState == HitContactState.Start);
            var rampEnd = p.HeldHits.Single(h => h.Effect == HitEffect.ProgressiveDamage && h.ContactState == HitContactState.End);
            var damage = p.HeldHits.First(h => h.Effect == HitEffect.Damage && h.ContactState == HitContactState.Start);
            Assert.AreEqual(50f, ramp.Amount);
            Assert.AreEqual(3f, ramp.RampTime);
            Assert.AreEqual(0, ramp.TargetShape);
            Assert.AreEqual(HitImpactKind.DamageOverTime, ramp.ImpactKind);
            Assert.AreEqual(LegacyDamageModel.Heat, ramp.DamageType);
            Assert.AreEqual(50f, damage.Amount);
            Assert.AreEqual(damage.Tick, ramp.Tick);

            var ticks = rampEnd.Tick - ramp.Tick;
            var expected = (float)(ticks * 1.0 + ImpactEffects.RampDamage(50f, 3f, 0, ticks));
            Assert.AreEqual(1000f - expected, p.B.Ship(101).Armor.Value, 0.01f, "The attacker's copy predicts the ramp.");

            p.Correct(p.A, p.B);
            p.Step();
            Assert.AreEqual(1000f - expected, p.B.Ship(101).Armor.Value, 0.01f, "The replay keeps the ramp.");

            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step(3);
            Assert.AreEqual(1000f - expected, p.A.Ship(101).Armor.Value, 0.01f);
        }

        // The closed form the target owner and the replay use equals the owner's sum of single ticks.
        [TestCase(3f, 0, 400)]
        [TestCase(3f, 37, 151)]
        [TestCase(0.5f, 0, 30)]
        [TestCase(0f, 0, 10)]
        public void TheRampSumMatchesItsTicks(float rampTime, long from, long to)
        {
            var sum = 0.0;
            for (var k = from; k < to; ++k) sum += ImpactEffects.RampTick(50f, rampTime, k);
            Assert.AreEqual(sum, ImpactEffects.RampDamage(50f, rampTime, from, to), 1e-3);
        }

        [Test]
        public void AChargeScalesTheRampButNotItsTime()
        {
            var beam = BeamDefinition.Charged(0, 1f, 10f, 10f, 50f, 0.5f, effects: new ImpactEffects(ramp: 50f, rampTime: 3f));
            var half = beam.WithCharge(0.5f).Effects;
            Assert.AreEqual(25f, half.Ramp);
            Assert.AreEqual(3f, half.RampTime);
        }

        [Test]
        public void TheRampIsABeamEffectOnly()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ImpactEffects(ramp: -1f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ImpactEffects(ramp: 1f, rampTime: float.NaN));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new BulletDefinition(4f, 0f, 0f, 8f,
                impact: BulletImpact.OverTime, lifetime: 1f, effects: new ImpactEffects(ramp: 1f, rampTime: 1f)));
        }

        // A received ramp is damage of a type over time on shape 0, with a finite ramp time; no other record has one.
        [Test]
        public void AReceivedRampIsAnIntervalOnShapeZero()
        {
            var p = new NetPair();
            var ship = p.A.Entity(101);
            ResolvedHit Ramp(bool bullet = false, HitImpactKind kind = HitImpactKind.DamageOverTime, int shape = 0,
                float rampTime = 3f, Vec2 impulse = default, HitEffect effect = HitEffect.ProgressiveDamage) =>
                new ResolvedHit(p.A.BattleId, 0, new EntityId(999), NetPair.OwnerB, 0,
                    bullet ? new EntityId(12345) : ship, NetPair.OwnerA, shape, HitContactState.Start, kind, 50f, 1, bullet,
                    LegacyDamageModel.Heat, impulse, effect, rampTime);
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Ramp(bullet: true))));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Ramp(kind: HitImpactKind.Energy))));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Ramp(shape: 1))));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Ramp(rampTime: -1f))));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Ramp(rampTime: float.PositiveInfinity))));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Ramp(effect: HitEffect.Damage))));
            Assert.IsTrue(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Ramp())));
        }
    }
}

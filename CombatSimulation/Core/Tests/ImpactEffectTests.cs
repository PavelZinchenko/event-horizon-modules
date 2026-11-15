using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 4, step 4b (E1, E4): recoil from the recoil field, push, and several damage effects per bullet.
    public class ImpactEffectTests
    {
        private const float Tolerance = 1e-3f;

        private static ShipDefinition MotionTarget(float mass) =>
            Fixtures.ShipWith(mass: mass, engine: new EngineDefinition(0f, 0f, 0f, 0f));

        [TestCase(36f, 0.5f)]
        [TestCase(9f, 0f)]
        [TestCase(4f, 0f)]
        public void StasisScalesBothVelocityAndSpin(float mass, float factor)
        {
            var bolt = new BulletDefinition(0.25f, 20f, 20f, 4f, effects: new ImpactEffects(slowDown: 3f));
            var driver = CannonTests.Duel(Fixtures.CannonShip(bolt, cooldown: 10f), MotionTarget(mass));
            driver.Place(101, Target, new Vec2(2f, 0f));
            driver.Battle.World.SetAngularVelocity(driver.Ship(101).Body, -1f);
            driver.Control(1, systems: 1);
            StepUntilHit(driver);
            Assert.AreEqual(2f * factor, driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body).X, Tolerance);
            Assert.AreEqual(-factor, driver.Battle.World.GetAngularVelocity(driver.Ship(101).Body), Tolerance);
        }

        [Test]
        public void AWebCompoundsSlowDownEveryTick()
        {
            var web = new BulletDefinition(4f, 0f, 0f, 8f, impact: BulletImpact.OverTime, lifetime: 10f,
                effects: new ImpactEffects(slowDown: 4f));
            var driver = CannonTests.Duel(Fixtures.CannonShip(web, cooldown: 20f), MotionTarget(16f));
            driver.Place(101, Target, new Vec2(0.1f, 0f));
            driver.Battle.World.SetAngularVelocity(driver.Ship(101).Body, 1f);
            driver.Control(1, systems: 1);
            StepUntilHit(driver);
            var hits = 1 + driver.StepCollecting(49).Count(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(50, hits);
            var factor = (float)System.Math.Pow(0.98f, hits);
            Assert.AreEqual(0.1f * factor, driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body).X, Tolerance);
            Assert.AreEqual(factor, driver.Battle.World.GetAngularVelocity(driver.Ship(101).Body), Tolerance);
        }

        [Test]
        public void SlowDownPrecedesPushLocallyAndOnTheTargetOwner()
        {
            var bolt = new BulletDefinition(0.25f, 20f, 20f, 0f, push: 0.5f, effects: new ImpactEffects(slowDown: 3f));
            var pair = new NetPair(Fixtures.CannonShip(bolt, cooldown: 10f), MotionTarget(36f));
            foreach (var driver in new[] { pair.A, pair.B })
            {
                driver.Place(1, Shooter, Vec2.Zero);
                driver.Place(101, Target, new Vec2(2f, 0f));
            }
            pair.HoldHitsFromB = true;
            pair.B.Control(1, systems: 1);
            for (var i = 0; i < 30 && pair.HeldHits.Count == 0; ++i) pair.Step();
            pair.B.Control(1, systems: 0);
            Assert.AreEqual(2, pair.HeldHits.Count);
            Assert.AreEqual(HitEffect.SlowDown, pair.HeldHits[0].Effect);
            Assert.AreEqual(HitEffect.Damage, pair.HeldHits[1].Effect);
            Assert.AreEqual(10f, pair.HeldHits[1].Impulse.X, Tolerance);
            var expected = 1f + 10f / 36f;
            Assert.AreEqual(expected, pair.B.Battle.World.GetLinearVelocity(pair.B.Ship(101).Body).X, Tolerance);
            Assert.AreEqual(2f, pair.A.Battle.World.GetLinearVelocity(pair.A.Ship(101).Body).X, Tolerance);

            // A correction before acknowledgement must not replay slow-down as damage.
            Assert.AreEqual(1, pair.Correct(pair.A, pair.B));
            pair.Step();
            Assert.AreEqual(1000f, pair.B.Ship(101).Armor.Value);
            Assert.AreEqual(500f, pair.B.Ship(101).Shield.Value);
            Assert.AreEqual(300f, pair.B.Ship(101).Energy.Value);
            pair.ReleaseHits();
            pair.HoldHitsFromB = false;
            pair.Step();
            Assert.AreEqual(expected, pair.A.Battle.World.GetLinearVelocity(pair.A.Ship(101).Body).X, Tolerance);
        }

        [Test]
        public void ADelayedWebIntervalCompoundsInsteadOfAddingItsMissedTicks()
        {
            var web = new BulletDefinition(4f, 0f, 0f, 0f, impact: BulletImpact.OverTime, lifetime: 1f,
                effects: new ImpactEffects(slowDown: 4f));
            var pair = new NetPair(Fixtures.CannonShip(web, cooldown: 20f), MotionTarget(16f));
            foreach (var driver in new[] { pair.A, pair.B })
            {
                driver.Place(1, Shooter, Vec2.Zero);
                driver.Place(101, Target, new Vec2(0.1f, 0f));
                driver.Battle.World.SetAngularVelocity(driver.Ship(101).Body, 1f);
            }
            pair.HoldHitsFromB = true;
            pair.B.Control(1, systems: 1);
            pair.Step(60);
            pair.B.Control(1, systems: 0);
            var start = pair.HeldHits.Single(h => h.Effect == HitEffect.SlowDown && h.ContactState == HitContactState.Start);
            var end = pair.HeldHits.Single(h => h.Effect == HitEffect.SlowDown && h.ContactState == HitContactState.End);
            Assert.AreEqual(4f, start.Amount);
            Assert.GreaterOrEqual(end.Tick - start.Tick, 49);
            var factor = (float)System.Math.Pow(0.98f, end.Tick - start.Tick);
            pair.ReleaseHits();
            pair.HoldHitsFromB = false;
            pair.Step();
            Assert.AreEqual(0.1f * factor, pair.A.Battle.World.GetLinearVelocity(pair.A.Ship(101).Body).X, Tolerance);
            Assert.AreEqual(factor, pair.A.Battle.World.GetAngularVelocity(pair.A.Ship(101).Body), Tolerance);
            pair.Step(5);
            Assert.AreEqual(factor, pair.A.Battle.World.GetAngularVelocity(pair.A.Ship(101).Body), Tolerance);
        }

        // One Roll test: the legacy formula, zero spin, receiver direction, and no repeated kick while touching.
        [Test]
        public void RollRunsOnContactEntryAndTheOwnerUsesItsOwnSpinDirection()
        {
            var area = new BulletDefinition(4f, 0f, 0f, 0f, impact: BulletImpact.OverTime, lifetime: 10f,
                effects: new ImpactEffects(roll: 1.25f));
            var pair = new NetPair(Fixtures.CannonShip(area, cooldown: 20f), MotionTarget(16f));
            foreach (var driver in new[] { pair.A, pair.B })
            {
                driver.Place(1, Shooter, Vec2.Zero);
                driver.Place(101, Target, Vec2.Zero);
            }
            pair.A.Battle.World.SetAngularVelocity(pair.A.Ship(101).Body, -0.5f);
            pair.HoldHitsFromB = true;
            pair.B.Control(1, systems: 1);
            pair.Step(4);
            pair.B.Control(1, systems: 0);
            var roll = pair.HeldHits.Single(h => h.Effect == HitEffect.Roll);
            Assert.AreEqual(56.25f, roll.Amount, Tolerance);
            Assert.AreEqual(HitImpactKind.Energy, roll.ImpactKind);
            var change = 56.25f * Fixtures.Degrees;
            Assert.AreEqual(change, pair.B.Battle.World.GetAngularVelocity(pair.B.Ship(101).Body), Tolerance);
            pair.Correct(pair.A, pair.B);
            pair.Step();
            Assert.AreEqual(500f, pair.B.Ship(101).Shield.Value, "Roll is not predicted damage.");
            pair.ReleaseHits();
            pair.HoldHitsFromB = false;
            pair.Step(3);
            Assert.AreEqual(-0.5f - change, pair.A.Battle.World.GetAngularVelocity(pair.A.Ship(101).Body), Tolerance);
            // Leaving and entering again is a new contact, even though the same area stays alive.
            pair.B.Place(101, new Vec2(80f, 80f), Vec2.Zero);
            pair.Step();
            pair.B.Place(101, Target, Vec2.Zero);
            pair.Step(2);
            Assert.AreEqual(-0.5f - 2f * change, pair.A.Battle.World.GetAngularVelocity(pair.A.Ship(101).Body), Tolerance);
        }

        [TestCase(HitEffect.SlowDown)]
        [TestCase(HitEffect.Roll)]
        public void MotionRecordsRejectBulletTargetsDamageTypesAndPush(HitEffect effect)
        {
            var pair = new NetPair();
            ResolvedHit Hit(bool bullet = false, byte damageType = 0, Vec2 impulse = default,
                HitImpactKind kind = HitImpactKind.Energy, HitContactState state = HitContactState.Start) =>
                new ResolvedHit(pair.A.BattleId, 0, new EntityId(999), NetPair.OwnerB, 0, pair.A.Entity(101),
                    NetPair.OwnerA, 0, state, kind, 1f, 1, bullet, damageType, impulse, effect);
            Assert.IsFalse(pair.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Hit(bullet: true))));
            Assert.IsFalse(pair.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Hit(damageType: 1))));
            Assert.IsFalse(pair.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Hit(impulse: new Vec2(1f, 0f)))));
            if (effect == HitEffect.Roll)
            {
                Assert.IsFalse(pair.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Hit(kind: HitImpactKind.DamageOverTime))));
                Assert.IsFalse(pair.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Hit(state: HitContactState.Stay))));
            }
            Assert.IsTrue(pair.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Hit())));
        }

        private static readonly Vec2 Shooter = new Vec2(50f, 50f);
        private static readonly Vec2 Target = new Vec2(54f, 50f);

        [Test]
        public void TheWeightAloneGivesNoRecoil()
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, weight: 0.5f);
            var driver = CannonTests.Duel(Fixtures.CannonShip(bullet, mass: 10f));
            driver.Control(1, systems: 1);

            StepUntilShot(driver);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(driver.Ship(1).Body).Length, Tolerance);
        }

        [Test]
        public void RecoilUsesTheBulletVelocityRelativeToTheShip()
        {
            // Legacy BulletFactory.ApplyRecoil. Without inherited velocity the bullet moves at (20, 0) and the ship at
            // (0, 4): impulse -0.5 x (20, -4) on mass 10.
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, parentVelocityEffect: 0f, recoil: 0.5f);
            var driver = CannonTests.Duel(Fixtures.CannonShip(bullet, mass: 10f));
            driver.Place(1, Shooter, new Vec2(0f, 4f));
            driver.Control(1, systems: 1);

            StepUntilShot(driver);
            var velocity = driver.Battle.World.GetLinearVelocity(driver.Ship(1).Body);
            Assert.AreEqual(-1f, velocity.X, Tolerance);
            Assert.AreEqual(4.2f, velocity.Y, Tolerance);
        }

        [Test]
        public void PushMovesTheTargetAlongTheBulletWithoutTurningIt()
        {
            // Legacy PushAction: 0.5 x the bullet velocity (20, 0) on a target of mass 2.
            var driver = LocalDuel(new BulletDefinition(0.25f, 20f, 10f, 5f, push: 0.5f));

            var hit = StepUntilHit(driver);
            Assert.AreEqual(5f, hit.Damage, Tolerance);
            var body = driver.Ship(101).Body;
            Assert.AreEqual(5f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Y, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(body), Tolerance);
        }

        [Test]
        public void EachDamageEffectMeetsItsOwnResistance()
        {
            // Kinetic 0.5 and energy 0.2, no shield: 20 impact deals 10, 20 energy deals 16.
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 20f, damageType: LegacyDamageModel.Impact,
                extraDamage: new[] { new DamageAmount(LegacyDamageModel.Energy, 20f) });
            var driver = LocalDuel(bullet);

            var hit = StepUntilHit(driver);
            Assert.AreEqual(40f, hit.Damage, Tolerance, "The hit event reports the total.");
            Assert.AreEqual(174f, driver.Ship(101).Armor.Value, Tolerance);
        }

        [Test]
        public void ThePushOnARemoteTargetTravelsWithTheFirstHitRecord()
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 20f, push: 0.5f,
                extraDamage: new[] { new DamageAmount(LegacyDamageModel.Energy, 20f) });
            var p = new NetPair(Fixtures.CannonShip(bullet, energyCost: 0f), TargetShip());
            foreach (var driver in new[] { p.A, p.B })
            {
                driver.Place(1, Shooter, Vec2.Zero);
                driver.Place(101, Target, Vec2.Zero);
            }
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            for (var i = 0; i < 20 && p.HeldHits.Count == 0; ++i) p.Step();
            p.B.Control(1, systems: 0);

            // One record per damage effect; only the first carries the push.
            Assert.AreEqual(2, p.HeldHits.Count);
            Assert.AreEqual(LegacyDamageModel.Impact, p.HeldHits[0].DamageType);
            Assert.AreEqual(10f, p.HeldHits[0].Impulse.X, Tolerance);
            Assert.AreEqual(0f, p.HeldHits[0].Impulse.Y, Tolerance);
            Assert.AreEqual(LegacyDamageModel.Energy, p.HeldHits[1].DamageType);
            Assert.AreEqual(0f, p.HeldHits[1].Impulse.Length);

            // The attacker's copy moves at once; the owner's ship waits for the record.
            Assert.AreEqual(5f, p.B.Battle.World.GetLinearVelocity(p.B.Ship(101).Body).X, Tolerance);
            var owner = p.A.Ship(101);
            Assert.AreEqual(0f, p.A.Battle.World.GetLinearVelocity(owner.Body).X, Tolerance);

            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step();
            Assert.AreEqual(5f, p.A.Battle.World.GetLinearVelocity(owner.Body).X, Tolerance);
            Assert.AreEqual(174f, owner.Armor.Value, Tolerance, "The owner applies both damage records.");
        }

        [Test]
        public void ARemotePushOnABulletOrARayIsRejected()
        {
            var p = new NetPair();
            var target = p.A.Entity(101);
            var ray = new ResolvedHit(p.A.BattleId, 0, new EntityId(999), NetPair.OwnerB, 0, target, NetPair.OwnerA, 0,
                HitContactState.Start, HitImpactKind.DamageOverTime, 1f, 1, impulse: new Vec2(1f, 0f));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, ray)));
            var nan = new ResolvedHit(p.A.BattleId, 0, new EntityId(999), NetPair.OwnerB, 0, target, NetPair.OwnerA, 0,
                HitContactState.Start, HitImpactKind.Energy, 1f, 1, impulse: new Vec2(float.NaN, 0f));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, nan)));
            var bolt = new ResolvedHit(p.A.BattleId, 0, new EntityId(999), NetPair.OwnerB, 0, target, NetPair.OwnerA, 0,
                HitContactState.Start, HitImpactKind.Energy, 1f, 1, impulse: new Vec2(1f, 0f));
            Assert.IsTrue(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, bolt)));
        }

        // Kinetic 0.5, energy 0.2; 200 armor, no shield; mass 2.
        private static ShipDefinition TargetShip() => Fixtures.ShipWith(mass: 2f, armor: 200f,
            shield: new ResourceDefinition(0f), resistances: LegacyDamageModel.Resistances(0.5f, 0.2f, 0.4f, 0.9f));

        private static Driver LocalDuel(BulletDefinition bullet)
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(bullet, energyCost: 0f), TargetShip());
            driver.Place(101, Target, Vec2.Zero);
            driver.Control(1, systems: 1);
            return driver;
        }

        private static void StepUntilShot(Driver driver)
        {
            for (var i = 0; i < 10; ++i)
            {
                driver.Step();
                if (driver.Battle.Snapshot.Bullets.Length > 0) return;
            }
            Assert.Fail("The cannon did not fire.");
        }

        // Steps until the first hit and releases the trigger, so later shots do not change the target.
        private static BattleEvent StepUntilHit(Driver driver)
        {
            for (var i = 0; i < 20; ++i)
            {
                var hit = driver.StepCollecting().Where(e => e.Kind == BattleEventKind.Hit).ToList();
                if (hit.Count == 0) continue;
                driver.Control(1, systems: 0);
                return hit[0];
            }
            Assert.Fail("The bullet did not hit.");
            return default;
        }
    }
}

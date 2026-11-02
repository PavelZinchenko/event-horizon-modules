using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 4, step 4b (E1, E4): recoil from the recoil field, push, and several damage effects per bullet.
    public class ImpactEffectTests
    {
        private const float Tolerance = 1e-3f;

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

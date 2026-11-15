using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class HarpoonTests
    {
        internal static readonly BulletDefinition Hook = new BulletDefinition(0.5f, 24f, 12f, 1f,
            hitPoints: 150f, impact: BulletImpact.OverTime, lifetime: 0.5f, harpoon: true,
            effects: new ImpactEffects(energyDrain: 100f));

        internal static ShipDefinition Shooter(float mass = 1f) => Fixtures.ShipWith(mass: mass,
            energy: new ResourceDefinition(1000f), barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
            devices: new[] { new CannonDefinition(0, 2f, 0f, 0f, Hook, CannonMode.Continuous) });

        internal static Driver Start(bool target = true, float targetMass = 1f, ShipDefinition enemy = null)
        {
            var driver = CannonTests.Duel(Shooter(), enemy ?? Fixtures.ShipWith(mass: targetMass,
                shield: new ResourceDefinition(0f), energy: new ResourceDefinition(1000f)));
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            if (target) driver.Place(101, new Vec2(58f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(20);
            return driver;
        }

        internal static int Index(Driver driver) => Enumerable.Range(0, driver.Battle.Bullets.Capacity)
            .Single(i => driver.Battle.Bullets.IsAlive(i));

        [TestCase(0f)]
        [TestCase(100f)]
        public void ZeroCostHarpoonLaunchesAndStaysHeldAfterEnemyDrainsEnergy(float energyCapacity)
        {
            var shooter = Fixtures.ShipWith(energy: new ResourceDefinition(energyCapacity),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new[] { new CannonDefinition(0, 2f, 0f, 0f, Hook, CannonMode.Continuous) });
            var d = CannonTests.Duel(shooter, Shooter());
            d.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            d.Place(101, new Vec2(58f, 50f), Vec2.Zero, (float)Math.PI);
            d.Control(101, systems: 1);
            d.Step(100);
            Assert.AreEqual(0f, d.Ship(1).Energy.Value);
            Assert.AreEqual(d.Entity(1), d.Battle.Snapshot.Bullets[0].AttachedTargetId,
                "The enemy harpoon must reach the ship before it returns fire.");

            d.Control(1, systems: 1);
            d.Step();
            var hook = d.Battle.Snapshot.Bullets.ToArray().Single(b => b.ParentId == d.Entity(1));
            d.Step(60); // Longer than two unrenewed hook lifetimes.
            Assert.IsTrue(d.Battle.Snapshot.Bullets.ToArray().Any(b => b.Id == hook.Id));
            Assert.AreEqual(0f, d.Ship(1).Energy.Value);
        }

        [TestCase(0f)]
        [TestCase(100f)]
        public void PositiveCostContinuousWeaponStillNeedsEnergy(float energyCapacity)
        {
            var shooter = Fixtures.ShipWith(energy: new ResourceDefinition(energyCapacity),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new[] { new CannonDefinition(0, 2f, 5f, 0f, Hook, CannonMode.Continuous) });
            var d = CannonTests.Duel(shooter);
            d.Ship(1).Energy.Consume(energyCapacity);
            d.Control(1, systems: 1);
            d.Step(10);
            Assert.IsEmpty(d.Battle.Snapshot.Bullets.ToArray());
            Assert.AreEqual(0f, d.Ship(1).Energy.Value);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HeldFreeHookStopsAtRangeIncludingAcrossTheSeam(bool seam)
        {
            var d = Start(false);
            if (seam)
            {
                // Reset the free hook beside a ship near the horizontal seam.
                d.Place(1, new Vec2(d.Battle.World.Torus.Width - 5f, 50f), Vec2.Zero);
                var body = d.Battle.Bullets[Index(d)].Body;
                d.Battle.World.SetBulletPose(body, new Vec2(d.Battle.World.Torus.Width - 4f, 50f), 0f);
                d.Battle.World.SetBulletVelocity(body, new Vec2(24f, 0f));
            }
            d.Step(80);
            var bullet = d.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.AreEqual(12f, d.Battle.World.Torus.Delta(d.Battle.World.GetPosition(d.Ship(1).Body), bullet.Position).Length, 0.001f);
            Assert.Less(bullet.LinearVelocity.Length, 0.001f);
            Assert.IsTrue(bullet.AttachedTargetId.IsNull);
        }

        [Test]
        public void HookSticksAndAppliesDamageAndDrainEveryTick()
        {
            var d = Start();
            Assert.IsTrue(d.Battle.Bullets[Index(d)].Stuck);
            Assert.AreEqual(d.Entity(101), d.Battle.Snapshot.Bullets[0].AttachedTargetId);
            var armor = d.Ship(101).Armor.Value;
            var energy = d.Ship(101).Energy.Value;
            d.Step(10);
            Assert.AreEqual(armor - 0.2f, d.Ship(101).Armor.Value, 0.001f);
            Assert.AreEqual(energy - 20f, d.Ship(101).Energy.Value, 0.001f);
        }

        [Test]
        public void TautRopePullsBothShipsAccordingToTheirMasses()
        {
            var d = Start(targetMass: 3f);
            d.Place(101, new Vec2(64f, 50f), new Vec2(8f, 0f));
            d.Step();
            var a = d.Battle.World.GetLinearVelocity(d.Ship(1).Body).X;
            var b = d.Battle.World.GetLinearVelocity(d.Ship(101).Body).X;
            Assert.Greater(a, 0f);
            Assert.Less(b, 8f);
            Assert.AreEqual(24f, a + 3f * b, 0.01f, "The rope conserves linear momentum.");
        }

        [Test]
        public void StretchBeyondToleranceDetonatesAndRemovesRope()
        {
            var d = Start();
            var rope = d.Battle.Bullets[Index(d)].HarpoonRope;
            d.Place(101, new Vec2(70f, 50f), Vec2.Zero);
            var events = d.StepCollecting();
            Assert.IsEmpty(d.Battle.Snapshot.Bullets.ToArray());
            Assert.IsFalse(d.Battle.World.IsAlive(rope));
            Assert.AreEqual(RemovalReason.Death, CannonTests.BulletRemovals(events).Single().RemovalReason);
        }

        [Test]
        public void ReleasingKeyExpiresHookAfterHalfASecondAndRemovesRope()
        {
            var d = Start();
            var rope = d.Battle.Bullets[Index(d)].HarpoonRope;
            var expiry = d.Battle.Snapshot.Bullets[0].ExpiryTick;
            d.Control(1, systems: 0);
            d.StepUntil(expiry);
            Assert.AreEqual(1, d.Battle.Snapshot.Bullets.Length);
            var events = d.StepCollecting();
            Assert.AreEqual(RemovalReason.Expiry, CannonTests.BulletRemovals(events).Single().RemovalReason);
            Assert.IsFalse(d.Battle.World.IsAlive(rope));
        }

        [Test]
        public void LosingTargetFreesHookAndAllowsAnotherStick()
        {
            var d = Start();
            var index = Index(d);
            var rope = d.Battle.Bullets[index].HarpoonRope;
            // Warp disables contacts without removing the roster ship.
            d.Ship(101).Status |= ShipStatusFlags.NoContacts;
            Harpoons.Update(d.Battle, index);
            Assert.IsFalse(d.Battle.Bullets[index].Stuck);
            Assert.IsFalse(d.Battle.World.IsAlive(rope));
            d.Ship(101).Status = ShipStatusFlags.None;
            Harpoons.ApplyTarget(d.Battle, index, d.Entity(101));
            Assert.IsTrue(d.Battle.Bullets[index].Stuck);
        }

        [Test]
        public void ReleasingHookClearsItsRopeAndPooledSlot()
        {
            var d = Start();
            var index = Index(d);
            var rope = d.Battle.Bullets[index].HarpoonRope;
            d.Battle.ReleaseBullet(index);
            Assert.IsFalse(d.Battle.World.IsAlive(rope));
            d.Step();
            Assert.IsTrue(d.Battle.Bullets[Index(d)].HarpoonRope.IsNull);
        }

        [Test]
        public void ShooterDeathRemovesItsHookInTheSameTick()
        {
            var d = Start();
            var rope = d.Battle.Bullets[Index(d)].HarpoonRope;
            d.Kill(1);
            d.Step();
            Assert.IsEmpty(d.Battle.Snapshot.Bullets.ToArray());
            Assert.IsFalse(d.Battle.World.IsAlive(rope));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EnemyBulletOrPointDefenseDestroysTheHookAndRope(bool pointDefense)
        {
            var weapon = pointDefense ? (DeviceDefinition)new PointDefenseDefinition(4f, 0f, 0f)
                : new CannonDefinition(0, 2f, 0f, 0f, new BulletDefinition(0.1f, 24f, 20f, 200f));
            var enemy = Fixtures.ShipWith(shield: new ResourceDefinition(0f),
                barrels: new[] { new BarrelDefinition(new Vec2(-1f, 0f), (float)Math.PI) },
                devices: new[] { weapon });
            var d = Start(enemy: enemy);
            var hook = d.Battle.Bullets[Index(d)];
            d.Control(101, systems: 1);
            var events = d.StepCollecting(5);
            Assert.IsFalse(d.Battle.World.IsAlive(hook.Body));
            Assert.IsFalse(d.Battle.World.IsAlive(hook.HarpoonRope));
            Assert.IsTrue(CannonTests.BulletRemovals(events).Any(e => e.EntityId == hook.Id));
        }

        [Test]
        public void CopyWaitsForStickAndDetachRecords()
        {
            var slots = new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Shooter(), 20),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, 20),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship, 10),
            };
            var owner = new Driver(new BattleSetup(new BattleId(77), 13, slots, localOwners: new[] { 20 }));
            var copy = new Driver(new BattleSetup(new BattleId(77), 13, slots, localOwners: new[] { 10 }));
            ulong sequence = 0;
            var pending = new List<OwnerOperation>();
            void Capture()
            {
                foreach (var op in owner.Battle.Snapshot.OutgoingOperations)
                {
                    pending.Add(op);
                    sequence = op.Sequence;
                }
            }
            void Deliver()
            {
                foreach (var op in pending) Assert.IsTrue(copy.Battle.Submit(20, op));
                pending.Clear();
                Assert.IsTrue(copy.Battle.Submit(20, new OwnerWatermark(copy.BattleId, 20, owner.Tick - 1, sequence)));
            }
            owner.Deploy(1);
            owner.Deploy(101);
            owner.Step(); Capture(); Deliver(); copy.Step();
            foreach (var d in new[] { owner, copy })
            {
                d.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                d.Place(101, new Vec2(58f, 50f), Vec2.Zero);
            }
            owner.Control(1, systems: 1);
            while (owner.Tick < 35)
            {
                owner.Step(); Capture();
                if (pending.Any(op => op.Kind == OwnerOperationKind.MissileTarget)) break;
                Deliver(); copy.Step();
            }
            Assert.IsTrue(owner.Battle.Bullets[Index(owner)].Stuck);
            copy.Step(35); // Beyond the unrenewed lifetime: a copy waits for the owner's removal.
            Assert.IsFalse(copy.Battle.Bullets[Index(copy)].Stuck);
            Deliver(); copy.Step();
            Assert.IsTrue(copy.Battle.Bullets[Index(copy)].Stuck);
            Assert.IsTrue(copy.Battle.World.IsAlive(copy.Battle.Bullets[Index(copy)].HarpoonRope));
            // The target's removal is followed by a null-target record. Both pass through the public network API.
            owner.Kill(101);
            owner.Step(); Capture(); Deliver(); copy.Step();
            owner.Step(); Capture();
            Assert.IsTrue(pending.Any(op => op.Kind == OwnerOperationKind.MissileTarget && op.TargetId.IsNull));
            Deliver(); copy.Step();
            Assert.IsFalse(copy.Battle.Bullets[Index(copy)].Stuck);
        }
    }
}

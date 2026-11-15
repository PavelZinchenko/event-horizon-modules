using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 4f (H7): legacy ChargeableCannon (phase 5 plan, "Charging weapons (4f, H7)"). The barrel of a ship
    // deployed at tick 0 is first ready at tick 2, so a key held from tick 1 starts the charge at tick 2 (T).
    public class ChargeTests
    {
        private const float Tolerance = 1e-3f;
        private const long T = 2;
        private const int Owner = 20;
        private const int Receiver = 10;

        // ChargedLaser_M_1: fire rate 0.2 (5 s, 250 ticks), energy 50 for a full charge, range 25, lifetime 1 s, 200 heat
        // per second.
        private static readonly BeamDefinition Laser = BeamDefinition.Charged(0, 5f, 50f, 25f, 200f, 1f);

        // AntiMatterBomb_L_1: fire rate 0.4 (2.5 s, 125 ticks), energy 20. The carrier (size 3, attached, 0.5 s) spawns
        // the explosion at creation: size 14, 75 heat with falloff to all targets, 0.3 s.
        private static readonly BulletDefinition Explosion = new BulletDefinition(7f, 0f, 0f, 75f, parentVelocityEffect: 0f,
            impact: BulletImpact.AllTargets, damageFalloff: true, lifetime: 0.3f, scale: 14f);

        private static readonly BulletDefinition Carrier = new BulletDefinition(1.5f, 0f, 0f, 0f, lifetime: 0.5f,
            scale: 3f, attachedToParent: true,
            triggers: new[] { BulletTriggerDefinition.Spawn(BulletTriggerConditions.Created, Explosion) });

        private static readonly CannonDefinition Bomb = CannonDefinition.Charged(0, 2.5f, 20f, 0f, Carrier);

        [Test]
        public void AChargedCopyScalesByItsCharge()
        {
            var bolt = new BulletDefinition(0.25f, 100f, 80f, 500f, weight: 0.4f, recoil: 2f, push: 3f,
                extraDamage: new[] { new DamageAmount(1, 10f) }, radialPush: 4f, radialPushWithFalloff: 5f, scale: 4f,
                beamLength: 100f, impact: BulletImpact.AllTargets,
                triggers: new[] { BulletTriggerDefinition.Spawn(BulletTriggerConditions.Hit, Explosion) });
            var half = bolt.WithCharge(0.5f);

            Assert.AreSame(bolt, half.Uncharged);
            Assert.AreEqual(0.5f, half.Charge);
            Assert.AreEqual(250f, half.Damage);
            Assert.AreEqual(5f, half.ExtraDamage[0].Amount);
            Assert.AreEqual(1.5f, half.Push);
            Assert.AreEqual(2f, half.RadialPush);
            Assert.AreEqual(2.5f, half.RadialPushWithFalloff);
            Assert.AreEqual(40f, half.Range);
            Assert.AreEqual(0.4f, half.Lifetime, 1e-6f); // range / speed
            Assert.AreEqual(100f, half.Speed);
            Assert.AreEqual(0.1875f, half.Radius);
            Assert.AreEqual(3f, half.Scale);
            Assert.AreEqual(0.3f, half.Weight, 1e-6f);
            Assert.AreEqual(1.5f, half.Recoil);
            Assert.AreEqual(75f, half.BeamLength);

            // Each child takes the charge of its parent.
            var child = half.Triggers[0].Bullet;
            Assert.AreSame(Explosion, child.Uncharged);
            Assert.AreEqual(37.5f, child.Damage);
            Assert.AreEqual(10.5f, child.Scale);
            Assert.AreEqual(0.3f, child.Lifetime); // a body lifetime stays

            // A full charge is the uncharged definition; a charge of 0.1 or less has no range.
            Assert.AreSame(bolt, bolt.WithCharge(1f));
            Assert.AreSame(bolt, half.WithCharge(1f));
            Assert.AreEqual(0f, bolt.WithCharge(0.1f).Range);
            Assert.AreEqual(1, bolt.WithCharge(0.1f).LifetimeTicks - bolt.WithCharge(0.1f).GrowthTicks(1f));
        }

        // Row 3 and G9: a charged beam scales its damage and range by c, its lifetime by 0.5 + 0.5c.
        [Test]
        public void AChargedBeamScalesItsDamageRangeAndLifetime()
        {
            Assert.IsTrue(Laser.IsCharged && Laser.IsShot);
            Assert.AreEqual(5f, Laser.ChargeTime);
            Assert.AreEqual(50f, Laser.ActivationCost);

            var half = Laser.WithCharge(0.5f);
            Assert.AreSame(Laser, half.Uncharged);
            Assert.AreEqual(12.5f, half.Range);
            Assert.AreEqual(100f, half.DamagePerSecond);
            Assert.AreEqual(0.75f, half.Lifetime);
            Assert.AreEqual(38, half.LifetimeTicks);
            Assert.IsTrue(half.IsCharged);
            Assert.AreEqual(0f, Laser.WithCharge(0.05f).Range);
            Assert.AreSame(Laser, Laser.WithCharge(1f));

            Assert.Throws<ArgumentOutOfRangeException>(() => BeamDefinition.Charged(0, 0f, 50f, 25f, 200f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => BeamDefinition.Charged(0, 5f, 50f, 25f, 200f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => CannonDefinition.Charged(0, float.PositiveInfinity, 20f, 0f, Carrier));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CannonDefinition(0, 1f, 20f, 0f, Carrier, CannonMode.Charging));
        }

        // Rows 1 and 2: the start spends nothing; each later tick spends 0.2 and adds a charge tick; the full charge comes
        // at T + 249, and above it the key holds the charge without cost.
        [Test]
        public void AHeldKeyChargesForTheFullCostThenHoldsTheCharge()
        {
            var driver = Duel(Ship(Laser));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(T);
            Assert.AreEqual(1000f, driver.Ship(1).Energy.Value);
            Assert.AreEqual(1f / 250f, State(driver).Charge, 1e-6f);
            Assert.IsTrue(State(driver).Active);

            events.AddRange(driver.StepCollecting(1));
            Assert.AreEqual(999.8f, driver.Ship(1).Energy.Value, Tolerance);

            driver.StepUntil(T + 249);
            Assert.Less(State(driver).Charge, 1f);
            driver.Step();
            Assert.AreEqual(1f, State(driver).Charge);
            Assert.AreEqual(950.2f, driver.Ship(1).Energy.Value, 0.01f);

            events.AddRange(driver.StepCollecting(100));
            Assert.AreEqual(950f, driver.Ship(1).Energy.Value, 0.01f);
            Assert.IsEmpty(CannonTests.BulletSpawns(events));
        }

        // Row 3: the release at T + 125 (c = 0.5) fires at the release tick: 100 heat per second, range 12.5, 0.75 s.
        [Test]
        public void TheReleaseFiresTheBeamOfTheCharge()
        {
            var driver = Duel(Ship(Laser), Target());
            driver.Place(101, new Vec2(61f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: T + 125);
            var events = driver.StepCollecting(T + 170);

            var spawn = CannonTests.BulletSpawns(events).Single();
            Assert.AreEqual(T + 125, spawn.Tick);
            Assert.AreEqual(0.5f, spawn.BeamDefinition.Charge);
            Assert.AreEqual(12.5f, spawn.BeamDefinition.Range);
            Assert.AreSame(Laser, spawn.BeamDefinition.Uncharged);

            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(T + 125 + 38 - 1, removal.Tick);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);

            var hits = events.Where(e => e.Kind == BattleEventKind.Hit && e.AttackerId == spawn.EntityId).ToList();
            Assert.AreEqual(38, hits.Count);
            Assert.That(hits.Select(e => e.Damage), Is.All.EqualTo(2f).Within(Tolerance));
        }

        // Row 4: a charge of 5 ticks or fewer fires nothing, and its energy is lost; 6 ticks fire.
        [Test]
        public void AChargeOfAtMostFiveTicksFiresNothing()
        {
            var driver = Duel(Ship(Laser));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: T + 5);
            var events = driver.StepCollecting(T + 10);
            Assert.IsEmpty(CannonTests.BulletSpawns(events));
            Assert.AreEqual(999.2f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(0f, State(driver).Charge);

            driver = Duel(Ship(Laser));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: T + 6);
            var spawn = CannonTests.BulletSpawns(driver.StepCollecting(T + 10)).Single();
            Assert.AreEqual(T + 6, spawn.Tick);
            Assert.AreEqual(6f / 250f, spawn.BeamDefinition.Charge, 1e-6f);
            Assert.AreEqual(0f, spawn.BeamDefinition.Range);
        }

        // Row 5: when the energy runs out the weapon fires at once with its charge. Energy 30: about 150 paid ticks.
        [Test]
        public void TheWeaponFiresWhenTheEnergyRunsOut()
        {
            var driver = Duel(Ship(Laser, 30f));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(T + 200);

            var spawn = CannonTests.BulletSpawns(events).Single();
            Assert.AreEqual(spawn.Tick - T, (long)Math.Round(spawn.BeamDefinition.Charge * 250f));
            Assert.AreEqual(151f / 250f, spawn.BeamDefinition.Charge, 1.5f / 250f);
            Assert.Less(driver.Ship(1).Energy.Value, 15f); // the key starts no new charge below half the cost
        }

        // Row 6: ChargedLaser_L_1 (10 s) released after 0.5 s: c = 0.05, range 0, the beam hits nothing.
        [Test]
        public void AChargeOfATenthOrLessHasNoRange()
        {
            var laser = BeamDefinition.Charged(0, 10f, 150f, 40f, 300f, 2f);
            var driver = Duel(Ship(laser), Target());
            driver.Place(101, new Vec2(52.5f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: T + 25);
            var events = driver.StepCollecting(T + 60);

            var spawn = CannonTests.BulletSpawns(events).Single();
            Assert.AreEqual(0.05f, spawn.BeamDefinition.Charge, 1e-6f);
            Assert.AreEqual(0f, spawn.BeamDefinition.Range);
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.Hit && e.AttackerId == spawn.EntityId));
        }

        // Row 7: after the shot only the barrel cooldown; the state shows the charge, the active bullet and the barrel.
        [Test]
        public void AfterTheShotOnlyTheBarrelCooldownWaits()
        {
            var driver = Duel(Ship(Laser));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: T + 10);
            driver.Control(1, systems: 1, tick: T + 11);
            driver.StepUntil(T + 11);

            var state = State(driver);
            Assert.AreEqual(0f, state.Charge);
            Assert.IsFalse(state.Ready);
            Assert.Greater(state.Cooldown, 0f);
            Assert.AreEqual(driver.Battle.Snapshot.Bullets.ToArray().Single().Id, state.ActiveBullet);

            var start = T + 10 + ShipEntity.BarrelCooldownTicks;
            driver.StepUntil(start);
            Assert.AreEqual(0f, State(driver).Charge);
            driver.Step();
            Assert.AreEqual(1f / 250f, State(driver).Charge, 1e-6f);
            Assert.AreEqual(0f, State(driver).Cooldown);
            Assert.IsTrue(State(driver).Ready);
        }

        // Row 8: a warp blocks shot weapons only; a charging weapon may charge and fire.
        [Test]
        public void AChargingWeaponIsNotAShotWeapon()
        {
            Assert.IsFalse(Laser.CreateDevice(0).IsShotWeapon);
            Assert.IsFalse(Bomb.CreateDevice(0).IsShotWeapon);
            Assert.IsTrue(BeamDefinition.Shot(0, 0.1f, 1f, 10f, 20f, 0.1f).CreateDevice(0).IsShotWeapon);
        }

        // Row 9: the attached carrier spawns its explosion at creation, and the explosion takes the carrier's charge:
        // damage x c, size x (0.5 + 0.5c); both lifetimes stay.
        [Test]
        public void TheChildOfACarrierTakesTheCarriersCharge()
        {
            var driver = Duel(Ship(Bomb));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: T + 75);
            var events = CannonTests.BulletSpawns(driver.StepCollecting(T + 76));
            Assert.AreEqual(2, events.Count);

            var carrier = events.Single(e => e.ParentId == driver.Entity(1)).BulletDefinition;
            var explosion = events.Single(e => e.ParentId != driver.Entity(1)).BulletDefinition;
            Assert.AreEqual(0.6f, carrier.Charge, 1e-6f);
            Assert.AreEqual(2.4f, carrier.Scale, 1e-5f);
            Assert.AreEqual(Carrier.LifetimeTicks, carrier.LifetimeTicks);
            Assert.AreSame(Explosion, explosion.Uncharged);
            Assert.AreEqual(45f, explosion.Damage, 1e-4f);
            Assert.AreEqual(11.2f, explosion.Scale, 1e-5f);
            Assert.AreEqual(Explosion.LifetimeTicks, explosion.LifetimeTicks);

            // The same charge gives the same definitions; a full charge gives the uncharged ones.
            Assert.AreSame(carrier, ((CannonDevice)driver.Ship(1).GetDevice(0)).BulletFor(75));
            Assert.AreSame(Carrier, ((CannonDevice)driver.Ship(1).GetDevice(0)).BulletFor(125));
        }

        // Row 11: a retreat releases the key, so the weapon fires its charge.
        [Test]
        public void ARetreatFiresTheCharge()
        {
            var driver = Duel(Ship(Laser));
            driver.Control(1, systems: 1);
            driver.StepUntil(T + 50);
            Assert.IsTrue(driver.Retreat(driver.Entity(1)));
            var spawn = CannonTests.BulletSpawns(driver.StepCollecting(2)).Single();
            Assert.AreEqual(T + 50, spawn.Tick);
            Assert.AreEqual(50f / 250f, spawn.BeamDefinition.Charge, 1e-6f);
        }

        // The owner's spawn record carries the charge ticks; the copy builds the owner's beam from it.
        [Test]
        public void TheCopyBuildsTheOwnersChargeFromTheRecord()
        {
            var link = new Link(Ship(Laser));
            link.Owner.Control(1, systems: 1);
            link.Owner.Control(1, systems: 0, tick: link.Owner.Tick + 126);
            var copyEvents = new List<BattleEvent>();
            for (var i = 0; i < 130; ++i)
            {
                var operations = link.Step(copyEvents);
                foreach (var operation in operations.Where(o => o.Kind == OwnerOperationKind.BulletSpawn))
                    Assert.AreEqual(125, operation.ChargeTicks);
            }

            var owner = link.Owner.Battle.Snapshot.Bullets.ToArray().Single();
            var copy = CannonTests.BulletSpawns(copyEvents).Single();
            Assert.AreEqual(owner.Id, copy.EntityId);
            Assert.AreEqual(0.5f, copy.BeamDefinition.Charge);
            Assert.AreEqual(owner.ExpiryTick, link.Copy.Battle.Snapshot.Bullets.ToArray().Single().ExpiryTick);
        }

        // The owner releases at T + 125. A copy whose release frame comes later fires its own prediction; the owner's
        // record, 3 ticks late, adopts it with the owner's charge. A copy that missed the release drops its own charge at
        // the owner's record, so it fires no second beam. An earlier prediction is removed by the owner's watermark,
        // then the record adds the owner's beam.
        [TestCase(128, 3, 1)]
        [TestCase(129, 0, 1)]
        [TestCase(121, 0, 2)]
        public void ACopyTakesTheOwnersChargeOverItsPrediction(int copyRelease, int delay, int copySpawns)
        {
            var link = new Link(Ship(Laser), delay);
            var start = link.Owner.Tick;
            link.Owner.Control(1, systems: 1);
            link.Copy.Control(link.Owner.Entity(1), systems: 1);
            link.Owner.Control(1, systems: 0, tick: start + 126);
            link.Copy.Control(link.Owner.Entity(1), systems: 0, tick: start + copyRelease);

            var copyEvents = new List<BattleEvent>();
            for (var i = 0; i < 140; ++i) link.Step(copyEvents);

            var owner = link.Owner.Battle.Snapshot.Bullets.ToArray().Single();
            var copy = link.Copy.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.AreEqual(owner.Id, copy.Id);
            Assert.AreEqual(0.5f, copy.BeamDefinition.Charge);
            Assert.AreEqual(owner.ExpiryTick, copy.ExpiryTick);
            // An adopted copy keeps its spawn event and renames.
            Assert.AreEqual(copySpawns, CannonTests.BulletSpawns(copyEvents).Count);
            Assert.AreEqual(delay > 0, copyEvents.Any(e => e.Kind == BattleEventKind.Rename && e.EntityId == owner.Id));
        }

        private static ShipDefinition Ship(DeviceDefinition weapon, float energy = 1000f) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(energy),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new[] { weapon });

        private static ShipDefinition Target() => Fixtures.ShipWith(shield: new ResourceDefinition(0f));

        private static Driver Duel(ShipDefinition shooter, ShipDefinition enemy = null) => CannonTests.Duel(shooter, enemy);

        private static DeviceState State(Driver driver)
        {
            var snapshot = driver.Battle.Snapshot;
            Assert.IsTrue(snapshot.TryGetShip(driver.Entity(1), out var ship));
            return snapshot.GetDevices(ship)[0];
        }

        // The owner (20) flies the shooter, slot 1; the copy (10) flies slot 101. The copy receives the owner's records
        // and watermark of a tick the given number of ticks later, before its own step.
        private sealed class Link
        {
            public Link(ShipDefinition shooter, int delay = 0)
            {
                Owner = new Driver(Setup(ChargeTests.Owner, shooter));
                Copy = new Driver(Setup(Receiver, shooter));
                _delay = delay;
                Owner.Deploy(1);
                Copy.Deploy(101);
                Owner.Step();
                Deliver(Owner.Battle.Snapshot.OutgoingOperations.ToArray(), Owner.Battle.Snapshot.Tick);
                Copy.Step();
                Owner.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                Copy.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            }

            public Driver Owner { get; }
            public Driver Copy { get; }

            // Returns the owner's records of this tick.
            public List<OwnerOperation> Step(List<BattleEvent> copyEvents)
            {
                Owner.Step();
                var operations = Owner.Battle.Snapshot.OutgoingOperations.ToArray().ToList();
                _held.Enqueue((Owner.Battle.Snapshot.Tick, operations));
                while (_held.Count > 0 && _held.Peek().Tick + _delay <= Owner.Battle.Snapshot.Tick)
                {
                    var (tick, held) = _held.Dequeue();
                    Deliver(held, tick);
                }
                copyEvents.AddRange(Copy.StepCollecting());
                return operations;
            }

            private void Deliver(IEnumerable<OwnerOperation> operations, long tick)
            {
                foreach (var operation in operations)
                {
                    if (!Copy.Battle.Submit(operation.Owner, operation)) throw new InvalidOperationException("Rejected record.");
                    _lastSequence = operation.Sequence;
                }
                if (!Copy.Battle.Submit(ChargeTests.Owner, new OwnerWatermark(Copy.BattleId, ChargeTests.Owner, tick, _lastSequence)))
                    throw new InvalidOperationException("Rejected watermark.");
            }

            private readonly int _delay;
            private readonly Queue<(long Tick, List<OwnerOperation> Operations)> _held =
                new Queue<(long Tick, List<OwnerOperation> Operations)>();

            private static BattleSetup Setup(int localOwner, ShipDefinition shooter) =>
                new BattleSetup(new BattleId(78), 13, new[]
                {
                    new RosterSlotSetup(new RosterId(1), 0, shooter, ChargeTests.Owner),
                    new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, Receiver),
                }, localOwners: new[] { localOwner });

            private ulong _lastSequence;
        }
    }
}

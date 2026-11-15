using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 9b: legacy StealthDevice (Stealth and SuperStealth), the end rules, the cooldown rule (owner
    // question 5) and the owner records (H8). Rows: TODO/battle_simulator_phase5.md, "Statuses (9)".
    public class StealthTests
    {
        private const float Tolerance = 1e-3f;

        // Records 13 and 14: energy 5; cooldown 5 s (250 ticks) and 3 s.
        private static readonly StealthDefinition Stealth = new StealthDefinition(5f, 5f, endsOnContact: true);
        private static readonly StealthDefinition SuperStealth = new StealthDefinition(5f, 3f, endsOnContact: false);

        // A cannon without cost that fires once per 10 s.
        private static readonly CannonDefinition Cannon = new CannonDefinition(0, 10f, 0f, 0f, Fixtures.Bolt);

        // Energy 100 without recharge; one barrel for the cannon.
        private static ShipDefinition StealthShip(TargetPriority priority = TargetPriority.Normal, float energy = 100f,
            params DeviceDefinition[] devices) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(energy), barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: devices.Length > 0 ? devices : new DeviceDefinition[] { Stealth, Cannon }, targetPriority: priority);

        private static DeviceState Device(Driver driver, uint rosterId, int index) =>
            GateDeviceTests.Device(driver, rosterId, index);

        private static ShipState State(Driver driver, uint rosterId)
        {
            driver.Battle.Snapshot.TryGetShip(driver.Entity(rosterId), out var ship);
            return ship;
        }

        // Presses the bit of the device for one tick from the next tick on.
        private static void Press(Driver driver, uint rosterId, ulong systems)
        {
            driver.Control(rosterId, systems: systems);
            driver.Control(rosterId, systems: 0, tick: driver.Tick + 1);
        }

        // Row Stealth, activation and effect: one press spends the cost once, and stealth stays on after the release.
        [Test]
        public void APressStartsStealthForOneCostAndItStaysOn()
        {
            var driver = CannonTests.Duel(StealthShip());
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(95f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 1).Status);
            Assert.AreEqual(TargetPriority.None, State(driver, 1).TargetPriority);
            Assert.IsTrue(Device(driver, 1, 0).Active);

            driver.Control(1, systems: 0);
            driver.Step(100);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 1).Status);
            Assert.AreEqual(95f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.IsFalse(Device(driver, 1, 0).Active);
        }

        [Test]
        public void StealthNeedsItsEnergyCost()
        {
            var driver = CannonTests.Duel(StealthShip(energy: 4f));
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(4f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.None, State(driver, 1).Status);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
        }

        // Owner question 4: None wins over a jammer's High; the end gives High back.
        [Test]
        public void StealthHidesAJammerShipUntilItEnds()
        {
            var driver = CannonTests.Duel(StealthShip(TargetPriority.High));
            Press(driver, 1, 1);
            driver.Step();
            Assert.AreEqual(TargetPriority.None, State(driver, 1).TargetPriority);

            driver.Step(5); // The barrel is ready.
            Press(driver, 1, 2);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, State(driver, 1).Status);
            Assert.AreEqual(TargetPriority.High, State(driver, 1).TargetPriority);
        }

        // Row Stealth, end: a device after the stealth device becomes active; it already updated, so the cooldown counts
        // from the next tick.
        [Test]
        public void ALaterActiveDeviceEndsStealthAndTheCooldownCountsFromTheNextTick()
        {
            var driver = CannonTests.Duel(StealthShip());
            Press(driver, 1, 1);
            driver.Step(6);
            var shot = driver.Tick;
            Press(driver, 1, 2);

            var spawns = CannonTests.BulletSpawns(driver.StepCollecting());
            Assert.AreEqual(shot, spawns.Single().Tick);
            Assert.AreEqual(ShipStatusFlags.None, State(driver, 1).Status, "Stealth ends in the tick of the shot.");

            driver.StepUntil(shot + 250);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 1, 0).Ready, "Ready at shot + 1 + 250.");
        }

        // Legacy device order: an active device before the stealth device restarts the cooldown before stealth updates, so
        // a press in the same tick starts nothing and spends nothing. After it, stealth starts, pays and ends at once.
        [TestCase(true, 100f)]
        [TestCase(false, 95f)]
        public void TheDeviceOrderDecidesAPressInTheTickOfAShot(bool cannonFirst, float energy)
        {
            var devices = cannonFirst ? new DeviceDefinition[] { Cannon, Stealth } : new DeviceDefinition[] { Stealth, Cannon };
            var driver = CannonTests.Duel(StealthShip(devices: devices));
            driver.Step(5);
            var shot = driver.Tick;
            Press(driver, 1, 3);
            driver.Step();

            Assert.AreEqual(energy, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.None, State(driver, 1).Status);
            var stealth = cannonFirst ? 1 : 0;
            driver.StepUntil(shot + (cannonFirst ? 249 : 250));
            Assert.IsFalse(Device(driver, 1, stealth).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 1, stealth).Ready);
        }

        [Test]
        public void PointDefenseAndDroneBaysKeepStealthOn()
        {
            var pointDefense = new PointDefenseDefinition(5f, 0.1f, 0f);
            var driver = CannonTests.Duel(StealthShip(devices: new DeviceDefinition[] { Stealth, pointDefense }));
            Press(driver, 1, 1);
            driver.Step(2);
            driver.Control(1, systems: 2);
            driver.Step(20);

            Assert.IsTrue(Device(driver, 1, 1).Active);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 1).Status);
        }

        // Owner question 5 (a): device activity restarts the cooldown also while stealth is off, so stealth needs 5 s
        // without it.
        [Test]
        public void ActivityRestartsTheCooldownWhileStealthIsOff()
        {
            var fast = new CannonDefinition(0, 1f, 0f, 0f, Fixtures.Bolt);
            var driver = CannonTests.Duel(StealthShip(devices: new DeviceDefinition[] { Stealth, fast }));
            Press(driver, 1, 1);
            driver.Step(6);
            driver.Control(1, systems: 2);
            driver.Control(1, systems: 0, tick: driver.Tick + 400);
            var shots = CannonTests.BulletSpawns(driver.StepCollecting(400)).Select(e => e.Tick).ToList();
            Assert.AreEqual(8, shots.Count, "One shot per second.");
            Assert.IsFalse(Device(driver, 1, 0).Ready, "Each shot restarts the cooldown.");

            // The cannon after the stealth device: the cooldown counts from the tick after the last shot.
            var last = shots.Last();
            driver.StepUntil(last + 250);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 1, 0).Ready);
        }

        // Row Stealth, end: a hit ends Stealth in the next tick (legacy OnHit after the systems); SuperStealth survives it.
        [TestCase(true)]
        [TestCase(false)]
        public void AHitEndsStealthButNotSuperStealth(bool endsOnContact)
        {
            var target = StealthShip(devices: endsOnContact ? Stealth : SuperStealth);
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 10f, energyCost: 0f), target);
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            Press(driver, 101, 1);
            driver.Step();
            driver.Step(3); // The barrel is ready.
            driver.Control(1, systems: 1);

            long hitTick = -1;
            for (var i = 0; i < 30 && hitTick < 0; ++i)
            {
                driver.Step();
                if (driver.Battle.Snapshot.Events.ToArray().Any(e => e.Kind == BattleEventKind.Hit)) hitTick = driver.Tick - 1;
            }
            Assert.GreaterOrEqual(hitTick, 0, "The bolt hits.");
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 101).Status, "The status of the hit tick.");

            driver.Step();
            Assert.AreEqual(endsOnContact ? ShipStatusFlags.None : ShipStatusFlags.Stealth, State(driver, 101).Status);
            if (!endsOnContact) return;

            driver.StepUntil(hitTick + 250);
            Assert.IsFalse(Device(driver, 101, 0).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 101, 0).Ready, "Ready at hit + 1 + 250.");
        }

        // Legacy OnCollisionStay2D: a touch counts without damage and with an ally.
        [Test]
        public void ATouchWithAnAllyEndsStealth()
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, StealthShip()),
                new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(2);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(2, new Vec2(80f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            Press(driver, 1, 1);
            driver.Step(3);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 1).Status);

            driver.Place(2, new Vec2(51.9f, 50f), Vec2.Zero);
            var events = driver.StepCollecting(3);
            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(ShipStatusFlags.None, State(driver, 1).Status);
        }

        // Legacy ShipRetreatEffect blocks the activation only: stealth stays on.
        [Test]
        public void ARetreatKeepsStealthOn()
        {
            var driver = CannonTests.Duel(StealthShip());
            Press(driver, 1, 1);
            driver.Step();
            driver.Retreat(driver.Entity(1));
            driver.Step(10);

            Assert.AreNotEqual(ShipEntity.NoRetreat, driver.Ship(1).RetreatCompletionTick);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 1).Status);
        }

        // Who reads the facts, item 1: the missile search skips priority None, also over a jammer's High.
        [Test]
        public void AMissileIgnoresAStealthShip()
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(Fixtures.Pixie, cooldown: 10f)),
                new RosterSlotSetup(new RosterId(101), 1, StealthShip(TargetPriority.High)),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Step();
            var shooter = new Vec2(50f, 50f);
            driver.Place(1, shooter, Vec2.Zero);
            driver.Place(101, shooter + new Vec2(20f, 0f), Vec2.Zero);
            driver.Place(102, shooter + new Vec2(40f, 1f), Vec2.Zero);
            driver.Control(1, systems: 1);
            Press(driver, 101, 1);
            driver.StepUntil(4);

            var bullets = driver.Battle.Bullets;
            var missile = Enumerable.Range(0, bullets.Capacity).Single(bullets.IsAlive);
            Assert.AreEqual(driver.Entity(102), bullets[missile].TargetId);
        }

        // H8: the owner records the start and the end; the copy has the same status in the same ticks.
        [Test]
        public void TheCopyFollowsTheOwnersStealthRecords()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(StealthShip());
            Press(owner, 101, 1);
            var start = owner.Tick;
            GateDeviceTests.Tick(owner, remote, link);
            Assert.IsTrue(GateDeviceTests.Uses(owner).Single().UseStarted);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(remote, 101).Status);
            Assert.AreEqual(TargetPriority.None, State(remote, 101).TargetPriority);

            for (var i = 0; i < 5; ++i) GateDeviceTests.Tick(owner, remote, link);
            Press(owner, 101, 2);
            remote.Control(101, systems: 2, tick: owner.Tick);
            owner.Step();
            var end = GateDeviceTests.Uses(owner).Single();
            Assert.IsFalse(end.UseStarted);
            Assert.AreEqual(start + 6, end.Tick);
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();

            Assert.AreEqual(ShipStatusFlags.None, State(owner, 101).Status);
            Assert.AreEqual(ShipStatusFlags.None, State(remote, 101).Status);
            Assert.AreEqual(95f, owner.Ship(101).Energy.Value, Tolerance);
        }

        // A late end record ends the copy's stealth at once; until then the copy keeps it, though its own cannon fired.
        [Test]
        public void ALateEndRecordEndsTheCopyAtOnce()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(StealthShip());
            Press(owner, 101, 1);
            GateDeviceTests.Tick(owner, remote, link);
            for (var i = 0; i < 5; ++i) GateDeviceTests.Tick(owner, remote, link);

            Press(owner, 101, 2);
            remote.Control(101, systems: 2, tick: owner.Tick);
            remote.Control(101, systems: 0, tick: owner.Tick + 1);
            for (var i = 0; i < 3; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            Assert.AreEqual(ShipStatusFlags.None, State(owner, 101).Status);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(remote, 101).Status, "The copy waits for the record.");

            link.Deliver(remote);
            remote.Step();
            Assert.AreEqual(ShipStatusFlags.None, State(remote, 101).Status);
        }

        [Test]
        public void TheCopyDoesNotStartStealthFromItsOwnControls()
        {
            var (_, remote, _) = GateDeviceTests.Pair(StealthShip());
            remote.Control(101, systems: 1);
            remote.Step(3);

            Assert.AreEqual(ShipStatusFlags.None, State(remote, 101).Status);
            Assert.AreEqual(100f, remote.Ship(101).Energy.Value, Tolerance);
        }

        // A remote attacker's contact ends the owner's stealth when its record arrives (stage 10), from the next tick.
        [Test]
        public void ARemoteHitRecordEndsTheOwnersStealth()
        {
            var setup = new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, StealthShip(), 10),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, 20),
            }, localOwners: new[] { 10 });
            var driver = new Driver(setup);
            driver.Deploy(1);
            driver.Step();
            Press(driver, 1, 1);
            driver.Step(3);
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 1).Status);

            var hit = new ResolvedHit(driver.BattleId, driver.Tick - 1, new EntityId(999), 20, 0, driver.Entity(1), 10, 0,
                HitContactState.Start, HitImpactKind.Energy, 0f);
            Assert.IsTrue(driver.Battle.Submit(new InboundHit(20, hit.WithSequence(1))));
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.Stealth, State(driver, 1).Status, "The record applies at stage 10.");
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, State(driver, 1).Status);
        }
    }
}

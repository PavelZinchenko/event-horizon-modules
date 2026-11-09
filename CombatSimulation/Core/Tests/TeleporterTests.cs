using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 11b: the obsolete legacy TeleporterDevice (TODO/battle_simulator_phase5.md, "Instant devices (11)").
    public class TeleporterTests
    {
        private const float Tolerance = 1e-3f;

        // Record 17: energy 3, cooldown 3 s (150 ticks), range 20.
        private static readonly TeleporterDefinition Teleporter = new TeleporterDefinition(3f, 3f, 20f);

        // Energy 30 without recharge.
        private static ShipDefinition Ship(float energy = 30f, bool teleportImmune = false) =>
            new ShipDefinition(1f, 1f, Fixtures.Engine, 1000f, Fixtures.Shield, new ResourceDefinition(energy),
                devices: new DeviceDefinition[] { Teleporter }, teleportImmune: teleportImmune);

        // Slot 1 at (50, 50) with the given heading, moving at (5, 0) and spinning.
        private static Driver Duel(ShipDefinition ship, float angle = 0f)
        {
            var driver = CannonTests.Duel(ship);
            driver.Place(1, new Vec2(50f, 50f), new Vec2(5f, 0f), angle);
            driver.Battle.World.SetAngularVelocity(driver.Ship(1).Body, 2f);
            return driver;
        }

        private static BattleEvent[] Teleports(Driver driver) =>
            driver.Battle.Snapshot.Events.ToArray().Where(e => e.Kind == BattleEventKind.Teleport).ToArray();

        private static void Near(Vec2 expected, Vec2 actual)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance);
            Assert.AreEqual(expected.Y, actual.Y, Tolerance);
        }

        // Rows 1 and 2: the cost, the jump along the heading, velocity and spin to 0, and one event with both points.
        [Test]
        public void AUseJumpsAlongTheHeadingAndStopsTheShip()
        {
            var driver = Duel(Ship(), (float)(Math.PI / 2));
            var body = driver.Ship(1).Body;
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(27f, driver.Ship(1).Energy.Value, Tolerance);
            Near(new Vec2(50f, 70f), driver.Battle.World.GetPosition(body));
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Length, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(body), Tolerance);

            var jump = Teleports(driver).Single();
            Assert.AreEqual(driver.Entity(1), jump.EntityId);
            Assert.AreEqual(0, jump.DeviceIndex);
            Assert.AreSame(driver.Ship(1).Definition, jump.Definition);
            Assert.AreEqual(BattleEventAuthority.Committed, jump.Authority);
            Near(new Vec2(50f, 50f), jump.Point);
            Near(new Vec2(50f, 70f), jump.Destination);

            var state = GateDeviceTests.Device(driver, 1, 0);
            Assert.IsTrue(state.Active);
            Assert.IsFalse(state.Ready);
            Assert.Greater(state.Cooldown, 0.99f);
        }

        // The cooldown runs from the use; a held key jumps again at each cooldown.
        [Test]
        public void AHeldKeyJumpsAtEachCooldown()
        {
            var driver = Duel(Ship());
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            var jumps = driver.StepCollecting(301).Where(e => e.Kind == BattleEventKind.Teleport).Select(e => e.Tick);

            CollectionAssert.AreEqual(new[] { tick, tick + 150, tick + 300 }, jumps);
            Assert.AreEqual(21f, driver.Ship(1).Energy.Value, Tolerance);
        }

        // Legacy CanBeActivated needs the cost; a device that is not ready spends nothing.
        [Test]
        public void WithoutTheCostNothingHappens()
        {
            var driver = Duel(Ship(energy: 2f));
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(2f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.IsEmpty(Teleports(driver));
            Assert.IsFalse(GateDeviceTests.Device(driver, 1, 0).Active);
            Assert.Greater(driver.Battle.World.GetPosition(driver.Ship(1).Body).X, 50f);
        }

        // The landing wraps around the torus edge, without a free-space search.
        [Test]
        public void TheJumpWrapsAroundTheTorus()
        {
            var driver = Duel(Ship(), (float)Math.PI);
            driver.Place(1, new Vec2(10f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(1, systems: 1);
            driver.Step();

            var expected = driver.Battle.World.Torus.Wrap(new Vec2(-10f, 50f));
            Assert.Greater(expected.X, 10f);
            Near(expected, driver.Battle.World.GetPosition(driver.Ship(1).Body));
            Near(expected, Teleports(driver).Single().Destination);
        }

        // Finding 10: TeleportImmune is for impacts only.
        [Test]
        public void ATeleportImmuneShipStillJumps()
        {
            var driver = Duel(Ship(teleportImmune: true));
            driver.Control(1, systems: 1);
            driver.Step();

            Near(new Vec2(70f, 50f), driver.Battle.World.GetPosition(driver.Ship(1).Body));
        }

        // H8, question 2 (a): the copy takes the cooldown and the event from the owner's record but does not move; the
        // owner's position correction moves it.
        [Test]
        public void TheCopyFollowsTheJumpByCorrection()
        {
            var pair = new NetPair(ship101: Ship());
            var owner = pair.A.Ship(101);
            var copy = pair.B.Ship(101);
            var tick = pair.A.Tick;
            pair.A.Control(101, systems: 1);
            pair.A.Control(101, systems: 0, tick: tick + 1);
            pair.Step();

            Near(new Vec2(70f, 50f), pair.A.Battle.World.GetPosition(owner.Body));
            var use = pair.A.Battle.Snapshot.OutgoingOperations.ToArray().Single(o => o.Kind == OwnerOperationKind.DeviceUse);
            Assert.AreEqual(tick, use.Tick);
            Assert.IsTrue(use.UseStarted);
            Assert.AreEqual(0, use.DeviceIndex);

            pair.Step();
            Near(new Vec2(50f, 50f), pair.B.Battle.World.GetPosition(copy.Body));
            var jump = Teleports(pair.B).Single();
            Near(new Vec2(50f, 50f), jump.Point);
            Near(new Vec2(70f, 50f), jump.Destination);
            Assert.AreEqual(GateDeviceTests.Device(pair.A, 101, 0).Cooldown, GateDeviceTests.Device(pair.B, 101, 0).Cooldown,
                Tolerance);
            Assert.AreEqual(30f, copy.Energy.Value, Tolerance, "The copy spends nothing.");

            pair.Correct(pair.A, pair.B);
            pair.Step();
            Near(new Vec2(70f, 50f), pair.B.Battle.World.GetPosition(copy.Body));
            Assert.IsEmpty(Teleports(pair.B));
        }

        [Test]
        public void TheCopyDoesNotJumpFromItsOwnControls()
        {
            var (_, remote, _) = GateDeviceTests.Pair(Ship());
            var body = remote.Ship(101).Body;
            var start = remote.Battle.World.GetPosition(body);
            remote.Control(101, systems: 1);
            remote.Step(3);

            Near(start, remote.Battle.World.GetPosition(body));
            Assert.IsEmpty(Teleports(remote));
            Assert.AreEqual(30f, remote.Ship(101).Energy.Value, Tolerance);
        }

        // A late record starts the copy's cooldown at the owner's tick, not at its arrival.
        [Test]
        public void ALateRecordKeepsTheOwnersCooldown()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(Ship());
            var tick = owner.Tick;
            owner.Control(101, systems: 1);
            owner.Control(101, systems: 0, tick: tick + 1);
            for (var i = 0; i < 4; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            link.Deliver(remote);
            owner.Step();
            remote.Step();

            Assert.AreEqual(GateDeviceTests.Device(owner, 101, 0).Cooldown, GateDeviceTests.Device(remote, 101, 0).Cooldown,
                Tolerance);
            Assert.AreEqual(1, Teleports(remote).Length);
            owner.StepUntil(tick + 149);
            remote.StepUntil(tick + 149);
            Assert.IsFalse(GateDeviceTests.Device(remote, 101, 0).Ready);
            remote.Step();
            Assert.IsTrue(GateDeviceTests.Device(remote, 101, 0).Ready);
        }
    }
}

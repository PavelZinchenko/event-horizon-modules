using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 9d: legacy GhostDevice and FortificationDevice, their tint and their owner records (H8). Rows:
    // TODO/battle_simulator_phase5.md, "Statuses (9)".
    public class GhostFortificationTests
    {
        private const float Tolerance = 1e-3f;

        // Record 3 with a lifetime of 1 s (50 ticks) instead of 60 s: energy 6 at the start and 6 per second (0.12 per
        // tick), cooldown 5 s (250 ticks).
        private static readonly GhostDefinition Ghost = new GhostDefinition(6f, 5f, 1f);

        // Record 7: energy 1 per second (0.02 per tick), cooldown 4 s (200 ticks), Power 0: share 0.5.
        private static readonly FortificationDefinition Fortification = new FortificationDefinition(1f, 4f, 0.5f);

        private static readonly StealthDefinition Stealth = new StealthDefinition(5f, 5f, endsOnContact: true);

        // A cannon without cost that fires every 5 ticks.
        private static readonly CannonDefinition Cannon = new CannonDefinition(0, 0.1f, 0f, 0f, Fixtures.Bolt);

        // Energy without recharge, no shield; kinetic resistance 0.2, energy 0.1, heat 0.
        private static ShipDefinition DeviceShip(float energy = 100f, params DeviceDefinition[] devices) =>
            Fixtures.ShipWith(shield: new ResourceDefinition(0f), energy: new ResourceDefinition(energy),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: devices.Length > 0 ? devices : new DeviceDefinition[] { Ghost },
                resistances: LegacyDamageModel.Resistances(0.2f, 0.1f, 0f, 0f));

        private static ShipDefinition FortificationShip(float energy = 100f) => DeviceShip(energy, Fortification);

        private static DeviceState Device(Driver driver, uint rosterId, int index) =>
            GateDeviceTests.Device(driver, rosterId, index);

        private static ShipState State(Driver driver, uint rosterId)
        {
            driver.Battle.Snapshot.TryGetShip(driver.Entity(rosterId), out var ship);
            return ship;
        }

        private static ShipStatusFlags Status(Driver driver, uint rosterId) => driver.Ship(rosterId).Status;

        private static float Energy(Driver driver, uint rosterId) => driver.Ship(rosterId).Energy.Value;

        // Row Ghost, activation and effect: the start spends 6, each later tick 6 per second; no contacts while on.
        [Test]
        public void AHeldGhostHasNoContactsAndPaysPerSecond()
        {
            var driver = CannonTests.Duel(DeviceShip());
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(driver, 1));
            Assert.AreEqual(94f, Energy(driver, 1), Tolerance, "The start tick pays only the cost.");
            Assert.IsTrue(Device(driver, 1, 0).Active);
            Assert.IsTrue(Device(driver, 1, 0).Ready);

            driver.Step(10);
            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(driver, 1));
            Assert.AreEqual(94f - 10f * 0.12f, Energy(driver, 1), Tolerance);
        }

        // Row Ghost, ends: the lifetime. Legacy pays while the time left is above 0, so the ghost runs 50 ticks after
        // its start tick; the cooldown counts from the end.
        [Test]
        public void TheGhostEndsAfterItsLifetime()
        {
            var driver = CannonTests.Duel(DeviceShip());
            var start = driver.Tick;
            driver.Control(1, systems: 1);
            driver.StepUntil(start + 51);

            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(driver, 1));
            Assert.AreEqual(94f - 50f * 0.12f, Energy(driver, 1), Tolerance);

            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.AreEqual(88f, Energy(driver, 1), Tolerance, "The end tick pays nothing.");
            Assert.IsTrue(Device(driver, 1, 0).Active, "Legacy Active in the end tick.");
            Assert.IsFalse(Device(driver, 1, 0).Ready);

            driver.StepUntil(start + 51 + 250);
            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.IsFalse(Device(driver, 1, 0).Active, "Legacy Active needs the cooldown.");
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(driver, 1), "Ready at the end + 250.");
        }

        // Row Ghost, ends: the release ends it at once.
        [Test]
        public void TheReleaseEndsTheGhostAndStartsTheCooldown()
        {
            var driver = CannonTests.Duel(DeviceShip());
            driver.Control(1, systems: 1);
            driver.Step(5);
            var release = driver.Tick;
            driver.Control(1, systems: 0);
            driver.Step();

            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.IsFalse(Device(driver, 1, 0).Active);
            Assert.AreEqual(94f - 4f * 0.12f, Energy(driver, 1), Tolerance);

            driver.Control(1, systems: 1);
            driver.StepUntil(release + 250);
            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(driver, 1));
        }

        // Row Ghost, ends: energy out. The start needs the cost.
        [Test]
        public void TheGhostEndsWhenTheEnergyRunsOut()
        {
            var poor = CannonTests.Duel(DeviceShip(energy: 5.9f));
            poor.Control(1, systems: 1);
            poor.Step();
            Assert.AreEqual(ShipStatusFlags.None, Status(poor, 1));
            Assert.IsFalse(Device(poor, 1, 0).Active);

            var driver = CannonTests.Duel(DeviceShip(energy: 6.2f));
            driver.Control(1, systems: 1);
            driver.Step(2);
            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(driver, 1));
            Assert.AreEqual(0.08f, Energy(driver, 1), Tolerance);

            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.AreEqual(0.08f, Energy(driver, 1), Tolerance);
        }

        // Row Ghost, effect: bullets pass through the ship; hits come back at its end.
        [Test]
        public void BulletsPassThroughAGhost()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0.1f, energyCost: 0f), DeviceShip());
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(101, systems: 1);
            driver.Step();
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(40);
            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value, Tolerance);

            driver.Control(101, systems: 0);
            events = driver.StepCollecting(40);
            Assert.IsNotEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
        }

        // Row Ghost, effect: every other device is blocked from the next tick (the step 7 rule); the cannon after the
        // ghost fires in the start tick, then one tick after the end.
        [Test]
        public void AGhostBlocksTheOtherDevices()
        {
            var driver = CannonTests.Duel(DeviceShip(100f, Ghost, Cannon));
            driver.Step(5); // The barrel is ready 3 ticks after deployment.
            var start = driver.Tick;
            driver.Control(1, systems: 3);
            var events = driver.StepCollecting(20);
            var release = driver.Tick;
            driver.Control(1, systems: 2);
            events.AddRange(driver.StepCollecting(3));

            var spawns = CannonTests.BulletSpawns(events);
            Assert.AreEqual(new[] { start, release + 1 }, spawns.Select(e => e.Tick).ToArray());
        }

        // Legacy CanActivateSystem lets stealth start during a ghost, and StealthDevice.OnSystemActivated keeps stealth
        // on for a ghost and a fortification.
        [Test]
        public void StealthStartsDuringAGhostAndSurvivesBothDevices()
        {
            var driver = CannonTests.Duel(DeviceShip(100f, Ghost, Stealth));
            driver.Control(1, systems: 1);
            driver.Step();
            driver.Control(1, systems: 3);
            driver.Step(3);
            Assert.AreEqual(ShipStatusFlags.NoContacts | ShipStatusFlags.Stealth, Status(driver, 1));

            var fortified = CannonTests.Duel(DeviceShip(100f, Stealth, Fortification));
            fortified.Control(1, systems: 1);
            fortified.Step();
            fortified.Control(1, systems: 2);
            fortified.Step(3);
            Assert.AreEqual(ShipStatusFlags.Stealth, Status(fortified, 1));
            Assert.AreEqual(0.5f, fortified.Ship(1).ResistanceShare, Tolerance);
        }

        // A retreat blocks the activation: the ghost ends as at a release.
        [Test]
        public void ARetreatEndsTheGhost()
        {
            var driver = CannonTests.Duel(DeviceShip());
            driver.Control(1, systems: 1);
            driver.Step(3);
            Assert.IsTrue(driver.Retreat(driver.Entity(1)));
            driver.Step();

            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.IsFalse(Device(driver, 1, 0).Active);
        }

        // Legacy OnUpdateView: the colour moves by 5 x the elapsed time of the distance, toward the device colour and back.
        [Test]
        public void TheTintMovesTowardTheDeviceColourAndBack()
        {
            var driver = CannonTests.Duel(DeviceShip());
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(0.1f, State(driver, 1).Tint, Tolerance);

            driver.Step(9);
            Assert.AreEqual(1f - (float)System.Math.Pow(0.9, 10), State(driver, 1).Tint, Tolerance);

            driver.Step(40);
            var on = State(driver, 1).Tint;
            driver.Control(1, systems: 0);
            driver.Step();
            Assert.AreEqual(0.9f * on, State(driver, 1).Tint, Tolerance);

            driver.Step(100);
            Assert.AreEqual(0f, State(driver, 1).Tint);
        }

        // Row Fortification: each armor resistance r becomes 0.5 + 0.5 r; corrosive, half of the smallest, moves toward
        // 0.5 (legacy derives it from the fortified values). The start tick pays 1 per second.
        [Test]
        public void AFortificationRaisesTheArmorResistances()
        {
            var driver = CannonTests.Duel(FortificationShip());
            var ship = driver.Ship(1);
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(0.5f, ship.ResistanceShare, Tolerance);
            Assert.AreEqual(0.5f, State(driver, 1).ResistanceShare, Tolerance, "for the HUD");
            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.AreEqual(99.98f, Energy(driver, 1), Tolerance);
            Assert.IsTrue(Device(driver, 1, 0).Active);

            ship.AddPendingDamage(LegacyDamageModel.Impact, 100f);
            driver.Step();
            Assert.AreEqual(1000f - 40f, ship.Armor.Value, Tolerance, "Kinetic 0.2 becomes 0.6.");

            var armor = ship.Armor.Value;
            ship.AddPendingDamage(LegacyDamageModel.Energy, 100f);
            ship.AddPendingDamage(LegacyDamageModel.Heat, 100f);
            ship.AddPendingDamage(LegacyDamageModel.Corrosive, 100f);
            driver.Step();
            Assert.AreEqual(armor - 45f - 50f - 75f, ship.Armor.Value, Tolerance, "0.55, 0.5 and 0.25.");
        }

        // Row Fortification, activation: a start needs the energy of one second in store; a running one does not.
        [Test]
        public void AFortificationStartsWithOneSecondOfEnergyAndRunsUntilItIsOut()
        {
            var poor = CannonTests.Duel(FortificationShip(energy: 0.9f));
            poor.Control(1, systems: 1);
            poor.Step();
            Assert.AreEqual(0f, poor.Ship(1).ResistanceShare);
            Assert.IsFalse(Device(poor, 1, 0).Active);
            Assert.IsFalse(Device(poor, 1, 0).Ready);

            var driver = CannonTests.Duel(FortificationShip(energy: 1.03f));
            var start = driver.Tick;
            driver.Control(1, systems: 1);
            driver.StepUntil(start + 51);
            Assert.AreEqual(0.5f, driver.Ship(1).ResistanceShare, Tolerance);
            Assert.AreEqual(0.01f, Energy(driver, 1), Tolerance);

            driver.Step();
            Assert.AreEqual(0f, driver.Ship(1).ResistanceShare);
            Assert.IsTrue(Device(driver, 1, 0).Active, "Legacy Active while held, also when the payment fails.");
        }

        // Row Fortification, ends: the release ends it; the cooldown counts from the release tick.
        [Test]
        public void TheReleaseEndsTheFortificationAndStartsTheCooldown()
        {
            var driver = CannonTests.Duel(FortificationShip());
            driver.Control(1, systems: 1);
            driver.Step(10);
            var release = driver.Tick;
            driver.Control(1, systems: 0);
            driver.Step();
            Assert.AreEqual(0f, driver.Ship(1).ResistanceShare);
            Assert.AreEqual(99.8f, Energy(driver, 1), Tolerance);

            driver.Control(1, systems: 1);
            driver.StepUntil(release + 199);
            Assert.IsFalse(Device(driver, 1, 0).Ready, "Ready reads the next tick.");
            driver.Step();
            Assert.AreEqual(0f, driver.Ship(1).ResistanceShare);
            Assert.IsTrue(Device(driver, 1, 0).Ready);
            driver.Step();
            Assert.AreEqual(0.5f, driver.Ship(1).ResistanceShare, Tolerance);
        }

        // Two fortifications combine their shares as legacy modifications in turn: 1 - (1 - p)(1 - q).
        [Test]
        public void TwoFortificationsCombineTheirShares()
        {
            var driver = CannonTests.Duel(DeviceShip(100f, Fortification, new FortificationDefinition(1f, 4f, 0.2f)));
            driver.Control(1, systems: 3);
            driver.Step();

            Assert.AreEqual(0.6f, driver.Ship(1).ResistanceShare, Tolerance);
        }

        // H8: the owner records the start and the end; the copy has the same status and energy in the same ticks.
        [Test]
        public void TheCopyFollowsTheOwnersGhostRecords()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(DeviceShip());
            var start = owner.Tick;
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1, tick: start);
            GateDeviceTests.Tick(owner, remote, link);
            var use = GateDeviceTests.Uses(owner).Single();
            Assert.IsTrue(use.UseStarted);
            Assert.AreEqual(start, use.Tick);
            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(remote, 101));
            Assert.AreEqual(remote.Ship(101).UsableSystems, owner.Ship(101).UsableSystems);

            for (var i = 0; i < 5; ++i) GateDeviceTests.Tick(owner, remote, link);
            owner.Control(101, systems: 0, tick: owner.Tick);
            remote.Control(101, systems: 0, tick: owner.Tick);
            GateDeviceTests.Tick(owner, remote, link);
            var end = GateDeviceTests.Uses(owner).Single();
            Assert.IsFalse(end.UseStarted);
            Assert.AreEqual(start + 6, end.Tick);

            Assert.AreEqual(ShipStatusFlags.None, Status(remote, 101));
            Assert.AreEqual(Energy(owner, 101) + 6f, Energy(remote, 101), Tolerance, "The copy pays no start cost.");
        }

        // A late end record ends the copy's ghost at once; until then it has no contacts, and its failed payments end
        // nothing.
        [Test]
        public void ALateEndRecordEndsTheCopysGhostAtOnce()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(DeviceShip(energy: 6.2f));
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1, tick: owner.Tick);
            GateDeviceTests.Tick(owner, remote, link);
            for (var i = 0; i < 4; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            Assert.AreEqual(ShipStatusFlags.None, Status(owner, 101));
            Assert.AreEqual(ShipStatusFlags.NoContacts, Status(remote, 101), "The copy waits for the record.");

            link.Deliver(remote);
            remote.Step();
            Assert.AreEqual(ShipStatusFlags.None, Status(remote, 101));
        }

        [Test]
        public void TheCopyDoesNotStartAGhostOrAFortificationFromItsOwnControls()
        {
            var (_, remote, _) = GateDeviceTests.Pair(DeviceShip(100f, Ghost, Fortification));
            remote.Control(101, systems: 3);
            remote.Step(3);

            Assert.AreEqual(ShipStatusFlags.None, Status(remote, 101));
            Assert.AreEqual(0f, remote.Ship(101).ResistanceShare);
            Assert.IsEmpty(GateDeviceTests.Uses(remote));
        }

        [Test]
        public void TheCopyFollowsTheOwnersFortificationRecords()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(FortificationShip());
            var start = owner.Tick;
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1, tick: start);
            GateDeviceTests.Tick(owner, remote, link);
            Assert.IsTrue(GateDeviceTests.Uses(owner).Single().UseStarted);
            Assert.AreEqual(0.5f, remote.Ship(101).ResistanceShare, Tolerance);

            for (var i = 0; i < 5; ++i) GateDeviceTests.Tick(owner, remote, link);
            owner.Control(101, systems: 0, tick: owner.Tick);
            remote.Control(101, systems: 0, tick: owner.Tick);
            GateDeviceTests.Tick(owner, remote, link);
            Assert.IsFalse(GateDeviceTests.Uses(owner).Single().UseStarted);
            Assert.AreEqual(0f, remote.Ship(101).ResistanceShare);
            Assert.AreEqual(Energy(owner, 101), Energy(remote, 101), Tolerance);
        }
    }
}

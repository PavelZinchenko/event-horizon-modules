using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 7a: the gate examples of InfinityStone and WarpDrive (TODO/battle_simulator_phase5.md,
    // "Data-model gate (7)"), the stage 3 status and the owner records of device uses (H8).
    public class GateDeviceTests
    {
        private const float Tolerance = 1e-3f;

        // Record 23: cooldown 60 s, lifetime 2 s.
        private static readonly InfinityStoneDefinition Stone = new InfinityStoneDefinition(60f, 2f);

        // Record 32: energy 10, cooldown 3 s, speed 100, range 44 (22 ticks).
        private static readonly WarpDriveDefinition Warp = new WarpDriveDefinition(10f, 3f, 100f, 44f);

        // Armor 100, shield 50, energy 100, without recharge.
        private static ShipDefinition StoneShip(float shield = 50f) =>
            Fixtures.ShipWith(armor: 100f, shield: new ResourceDefinition(shield), energy: new ResourceDefinition(100f),
                devices: new DeviceDefinition[] { Stone });

        private static ShipDefinition WarpShip(float energy = 30f, params DeviceDefinition[] more) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(energy), barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { Warp }.Concat(more).ToArray());

        private static DeviceState Device(Driver driver, uint rosterId, int index)
        {
            driver.Battle.Snapshot.TryGetShip(driver.Entity(rosterId), out var ship);
            return driver.Battle.Snapshot.GetDevices(ship)[index];
        }

        private static Driver WarpDuel(ShipDefinition ship)
        {
            var driver = CannonTests.Duel(ship);
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        // InfinityStone, row 1: lethal damage restores every resource and starts NoContacts and the cooldown.
        [Test]
        public void LethalDamageRestoresTheShipAndStartsNoContacts()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            ship.Energy.Consume(60f);
            var tick = driver.Tick;
            ship.AddPendingDamage(0, 180f);
            driver.Step();

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(100f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(50f, ship.Shield.Value, Tolerance);
            Assert.AreEqual(100f, ship.Energy.Value, Tolerance);
            Assert.IsFalse(Device(driver, 1, 0).Ready);

            // Stage 3 has passed: 100 ticks without contacts from the next tick on, as legacy turns the collider off at
            // the next frame.
            Assert.AreEqual(ShipStatusFlags.None, driver.Battle.Snapshot.Ships[0].Status);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.NoContacts, driver.Battle.Snapshot.Ships[0].Status);
            driver.StepUntil(tick + 101);
            Assert.AreEqual(ShipStatusFlags.NoContacts, ship.Status);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, ship.Status);
        }

        // Row 2 (H3): every hit of the tick counts before the restore.
        [Test]
        public void SeveralHitsInOneTickCountBeforeTheRestore()
        {
            var driver = CannonTests.Duel(StoneShip(shield: 0f));
            var ship = driver.Ship(1);
            ship.AddPendingDamage(0, 80f);
            ship.AddPendingDamage(0, 40f);
            ship.AddPendingDamage(0, 30f);
            driver.Step();

            Assert.AreEqual(100f, ship.Armor.Value, Tolerance, "Legacy restores after the second hit and keeps 70.");
        }

        // Rows 3 and 4: a key use restores at any armor; lethal damage during the cooldown kills.
        [Test]
        public void AKeyUseStartsTheCooldownAndALaterLethalHitKills()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            ship.Armor.Consume(50f);
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(100f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.NoContacts, ship.Status);
            Assert.IsTrue(Device(driver, 1, 0).Active);
            Assert.IsFalse(Device(driver, 1, 0).Ready);

            driver.Control(1, systems: 0);
            driver.Step(120);
            ship.AddPendingDamage(0, 1000f);
            driver.Step();
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
        }

        // Row 5: lethal damage without a contact during NoContacts restores again; the cooldown does not restart.
        [Test]
        public void LethalDamageDuringNoContactsRestoresWithoutANewCooldown()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            driver.Control(1, systems: 1);
            driver.Step();
            driver.Control(1, systems: 0);
            var cooldown = Device(driver, 1, 0).Cooldown;

            driver.Step(49);
            ship.AddPendingDamage(0, 1000f);
            driver.Step();

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(100f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(cooldown - 50f / 3000f, Device(driver, 1, 0).Cooldown, 1e-5f);
        }

        // After the 60 s cooldown the stone saves the ship again.
        [Test]
        public void TheStoneIsReadyAgainAfterItsCooldown()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            var tick = driver.Tick;
            ship.AddPendingDamage(0, 1000f);
            driver.Step();

            driver.StepUntil(tick + 2999);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 1, 0).Ready, "Ready from tick + 3000 on.");
            ship.AddPendingDamage(0, 1000f);
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
        }

        // NoContacts: bullets pass through the ship; hits come back at its end.
        [Test]
        public void BulletsPassThroughAShipWithoutContacts()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0.1f, energyCost: 0f), StoneShip());
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(101, systems: 1);
            driver.Step();
            driver.Control(101, systems: 0);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(90);
            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(100f, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(50f, driver.Ship(101).Shield.Value, Tolerance);

            events = driver.StepCollecting(40);
            Assert.IsNotEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
        }

        // WarpDrive, rows 1 and 2: the warp sets the velocity, runs 22 ticks (44 u) and starts the cooldown at its end.
        [Test]
        public void AHeldWarpRunsItsFullRangeThenStops()
        {
            var driver = WarpDuel(WarpShip());
            var ship = driver.Ship(1);
            var body = ship.Body;
            var tick = driver.Tick;
            driver.Control(1, course: 0.5f, throttle: 1f, systems: 1);
            driver.Step();

            Assert.AreEqual(20f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.NoContacts | ShipStatusFlags.EngineOff | ShipStatusFlags.Faded,
                driver.Battle.Snapshot.Ships[0].Status);
            Assert.IsTrue(Device(driver, 1, 0).Active);

            // The engine is off: thrust and course change nothing.
            driver.StepUntil(tick + 22);
            Assert.AreEqual(100f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(body), Tolerance);
            Assert.AreEqual(94f, driver.Battle.World.GetPosition(body).X, 1e-2f);

            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Length, Tolerance);
            Assert.AreEqual(94f, driver.Battle.World.GetPosition(body).X, 1e-2f);
            Assert.AreEqual(ShipStatusFlags.None, ship.Status);

            // The cooldown runs from the end tick: 150 ticks.
            driver.StepUntil(tick + 22 + 149);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 1, 0).Ready);
        }

        // Row 3: a release ends the warp at the first tick after 0.1 s.
        [Test]
        public void AReleasedWarpEndsAfterTheMinimumTime()
        {
            var driver = WarpDuel(WarpShip());
            var body = driver.Ship(1).Body;
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: tick + 2);

            driver.StepUntil(tick + 6);
            Assert.AreEqual(100f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            driver.Step();
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Length, Tolerance);
            Assert.AreEqual(62f, driver.Battle.World.GetPosition(body).X, 1e-2f);
        }

        // Row 4 and status timing: a shot weapon after the warp drive fires in the warp tick, then is blocked until the
        // tick after the end.
        [Test]
        public void TheWarpBlocksShotWeaponsFromTheNextTick()
        {
            var cannon = new CannonDefinition(0, 0.02f, 0f, 0f, Fixtures.Bolt);
            var driver = WarpDuel(WarpShip(1000f, cannon));
            driver.Step(5); // The barrel is ready 3 ticks after deployment.
            var tick = driver.Tick;
            driver.Control(1, systems: 3);

            var spawns = CannonTests.BulletSpawns(driver.StepCollecting(30)).Select(e => e.Tick).ToList();
            Assert.AreEqual(tick, spawns[0]);
            Assert.AreEqual(tick + 23, spawns[1], "Blocked through the end tick + 22; the status of tick 22 is clear.");
        }

        // Row 5: not enough energy: no warp, nothing spent.
        [Test]
        public void AWarpNeedsItsEnergyCost()
        {
            var driver = WarpDuel(WarpShip(energy: 9f));
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(9f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.None, driver.Ship(1).Status);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(driver.Ship(1).Body).Length, Tolerance);
        }

        // Row 6: a retreat ends the warp as a release does.
        [Test]
        public void ARetreatEndsTheWarpAsARelease()
        {
            var driver = WarpDuel(WarpShip());
            var body = driver.Ship(1).Body;
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.Step(2);
            driver.Retreat(driver.Entity(1));

            driver.StepUntil(tick + 6);
            Assert.AreEqual(100f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            driver.Step();
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Length, Tolerance);
        }

        // NoContacts during the warp: the ship flies through an enemy without ramming, and touches again after.
        [Test]
        public void AWarpingShipPassesThroughAnEnemy()
        {
            var driver = WarpDuel(WarpShip());
            driver.Place(101, new Vec2(70f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(30);
            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body).Length, Tolerance);
        }

        // H8: the owner sends the start and the end; the copy follows them and never starts a warp itself.

        private const int RemoteOwner = 20;

        private static BattleSetup NetworkSetup(int localOwner, ShipDefinition ship) => new BattleSetup(new BattleId(77), 13,
            new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, ship, RemoteOwner),
            }, localOwners: new[] { localOwner });

        private static (Driver owner, Driver remote, Link link) Pair(ShipDefinition ship)
        {
            var owner = new Driver(NetworkSetup(RemoteOwner, ship));
            var remote = new Driver(NetworkSetup(10, ship));
            var link = new Link();
            owner.Deploy(101);
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();
            return (owner, remote, link);
        }

        private static void Tick(Driver owner, Driver remote, Link link)
        {
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();
        }

        private static IEnumerable<OwnerOperation> Uses(Driver driver) =>
            driver.Battle.Snapshot.OutgoingOperations.ToArray().Where(o => o.Kind == OwnerOperationKind.DeviceUse);

        [Test]
        public void TheCopyFollowsTheOwnersWarpRecords()
        {
            var (owner, remote, link) = Pair(WarpShip());
            var tick = owner.Tick;
            owner.Control(101, systems: 1);
            owner.Control(101, systems: 0, tick: tick + 2);
            owner.Step();
            var start = Uses(owner).Single();
            Assert.AreEqual(tick, start.Tick);
            Assert.IsTrue(start.UseStarted);
            Assert.AreEqual(0, start.DeviceIndex);
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();

            var copy = remote.Ship(101);
            Assert.AreEqual(ShipStatusFlags.NoContacts | ShipStatusFlags.EngineOff | ShipStatusFlags.Faded, copy.Status);
            Assert.AreEqual(100f, remote.Battle.World.GetLinearVelocity(copy.Body).Length, Tolerance);

            for (var i = 0; i < 5; ++i) Tick(owner, remote, link);
            Assert.AreNotEqual(ShipStatusFlags.None, copy.Status);
            Tick(owner, remote, link);
            Assert.AreEqual(ShipStatusFlags.None, copy.Status, "The end record of tick + 6 applied on time.");
            Assert.AreEqual(0f, remote.Battle.World.GetLinearVelocity(copy.Body).Length, Tolerance);
        }

        [Test]
        public void TheCopyDoesNotStartAWarpFromItsOwnControls()
        {
            var (_, remote, _) = Pair(WarpShip());
            remote.Control(101, systems: 1);
            remote.Step(3);

            Assert.AreEqual(ShipStatusFlags.None, remote.Ship(101).Status);
            Assert.AreEqual(30f, remote.Ship(101).Energy.Value, Tolerance);
        }

        [Test]
        public void TheCopyRestoresOnTheOwnersInfinityStoneRecord()
        {
            var (owner, remote, link) = Pair(StoneShip());
            owner.Ship(101).AddPendingDamage(0, 1000f);
            remote.Ship(101).Armor.Consume(70f);
            Tick(owner, remote, link);

            // The record names tick + 1, the first tick of NoContacts.
            Assert.AreEqual(RosterStatus.Active, owner.Status(101));
            Assert.AreEqual(30f, remote.Ship(101).Armor.Value, Tolerance);
            Tick(owner, remote, link);
            Assert.AreEqual(100f, remote.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.NoContacts, remote.Ship(101).Status);
        }

        [Test]
        public void ARecordForAMissingDeviceIsRejected()
        {
            var (owner, remote, _) = Pair(StoneShip());
            var ship = owner.Entity(101);
            Assert.IsFalse(remote.Battle.Submit(RemoteOwner,
                OwnerOperation.DeviceUsed(remote.BattleId, RemoteOwner, 2, remote.Tick, ship, 1, true)));
            Assert.IsTrue(remote.Battle.Submit(RemoteOwner,
                OwnerOperation.DeviceUsed(remote.BattleId, RemoteOwner, 2, remote.Tick, ship, 0, true)));
        }

        private sealed class Link
        {
            private readonly List<OwnerOperation> _held = new List<OwnerOperation>(64);
            private ulong _lastSequence;
            private long _through = -1;

            public void Capture(Driver owner)
            {
                foreach (var operation in owner.Battle.Snapshot.OutgoingOperations)
                {
                    _held.Add(operation);
                    _lastSequence = operation.Sequence;
                }
                _through = owner.Battle.Snapshot.Tick;
            }

            public void Deliver(Driver remote)
            {
                foreach (var operation in _held)
                    if (!remote.Battle.Submit(operation.Owner, operation)) throw new InvalidOperationException("Rejected record.");
                _held.Clear();
                if (!remote.Battle.Submit(RemoteOwner, new OwnerWatermark(remote.BattleId, RemoteOwner, _through, _lastSequence)))
                    throw new InvalidOperationException("Rejected watermark.");
            }
        }
    }
}

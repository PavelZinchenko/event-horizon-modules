using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 4g: legacy ContinuousCannon with an AuraEmitter bullet (phase 5 plan, "AuraEmitter (4g)").
    // Pulsar_M_2: radius 7 at full size (legacy 2 x BodySize 7 on the area prefab), lifetime 2 s (100 ticks of growth,
    // 101 ticks of flight), 25 damage per second over time, attached to the ship. The barrel of a ship deployed at tick 0
    // is first ready at tick 2, so the aura spawns at tick 2 with one tick of growth.
    public class AuraTests
    {
        private const float Tolerance = 1e-3f;

        private static readonly BulletDefinition Pulsar = new BulletDefinition(7f, 0f, 0f, 25f,
            impact: BulletImpact.OverTime, lifetime: 2f, scale: 14f, attachedToParent: true, aura: true);

        // EnergyCost 8: the shot and each second of holding.
        private static ShipDefinition PulsarShip(float energy = 300f) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(energy),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0f, 8f, 0f, Pulsar, CannonMode.Continuous) });

        // No shield, no resistances.
        private static readonly ShipDefinition Target = Fixtures.ShipWith(armor: 200f, shield: new ResourceDefinition(0f));

        private static List<BattleEvent> Shots(IEnumerable<BattleEvent> events) => CannonTests.BulletSpawns(events);

        private static BulletState Aura(Driver driver) => driver.Battle.Snapshot.Bullets.ToArray().Single();

        private static float Power(Driver driver) => Aura(driver).AuraPower;

        // Steps through the given tick.
        private static void Through(Driver driver, long tick) => driver.StepUntil(tick + 1);

        private static DeviceState State(Driver driver)
        {
            var snapshot = driver.Battle.Snapshot;
            Assert.IsTrue(snapshot.TryGetShip(driver.Entity(1), out var ship));
            return snapshot.GetDevices(ship)[0];
        }

        // Row 1: the shot costs 8; the aura grows one hundredth per tick to its full radius at tick 101, and holding it
        // costs 0.16 per tick. It lives while the key is held.
        [Test]
        public void AHeldKeyGrowsTheAuraToFullSizeAndKeepsIt()
        {
            var driver = CannonTests.Duel(PulsarShip());
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(2);
            Assert.AreEqual(new long[] { 2 }, Shots(events).Select(e => e.Tick).ToArray());
            Assert.AreEqual(292f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(0.01f, Power(driver), Tolerance);

            Through(driver, 51);
            Assert.AreEqual(0.5f, Power(driver), Tolerance);
            Assert.AreEqual(292f - 0.16f * 49, driver.Ship(1).Energy.Value, 0.01f);

            events = driver.StepCollecting(399);
            Assert.IsEmpty(Shots(events));
            Assert.IsEmpty(CannonTests.BulletRemovals(events));
            Assert.AreEqual(1f, Power(driver), 0f);
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i))
                    Assert.AreEqual(7f, driver.Battle.World.GetBulletRadius(bullets[i].Body), Tolerance);
            Assert.IsFalse(State(driver).ActiveBullet.IsNull);
            Assert.IsTrue(State(driver).Active);
        }

        // Row 2: released at tick 150, after the last renewal at tick 149. The lifetime runs: the aura shrinks one
        // hundredth per tick, reaches size 0 at tick 249 and expires then. Holding costs nothing after the release.
        [Test]
        public void ReleasedAtFullSizeTheAuraShrinksOverItsLifetime()
        {
            var driver = CannonTests.Duel(PulsarShip());
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 150);

            Through(driver, 150);
            Assert.AreEqual(0.99f, Power(driver), Tolerance);
            var energy = driver.Ship(1).Energy.Value;
            Through(driver, 200);
            Assert.AreEqual(0.49f, Power(driver), Tolerance);
            Assert.AreEqual(energy, driver.Ship(1).Energy.Value, 0f);
            Through(driver, 248);
            Assert.AreEqual(0.01f, Power(driver), Tolerance);

            var removal = CannonTests.BulletRemovals(driver.StepCollecting()).Single();
            Assert.AreEqual(249, removal.Tick);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
            Assert.IsTrue(State(driver).ActiveBullet.IsNull);
        }

        // Row 3: released at tick 50 (last renewal at 49, size 48). The aura grows until its size meets the falling
        // lifetime share at tick 75 (0.74), then shrinks with it and expires at tick 149.
        [Test]
        public void ReleasedDuringGrowthTheAuraGrowsUntilItMeetsItsLifetime()
        {
            var driver = CannonTests.Duel(PulsarShip());
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 50);

            var powers = new List<float>();
            var events = new List<BattleEvent>();
            for (var tick = 1; tick <= 149; ++tick)
            {
                events.AddRange(driver.StepCollecting());
                if (tick >= 2 && tick < 149) powers.Add(Power(driver));
            }

            var peak = powers.Max();
            Assert.AreEqual(0.74f, peak, Tolerance);
            Assert.AreEqual(75 - 2, powers.IndexOf(peak));
            Assert.AreEqual(0.73f, powers[76 - 2], Tolerance);
            Assert.AreEqual(0.01f, powers[148 - 2], Tolerance);
            Assert.AreEqual(149, CannonTests.BulletRemovals(events).Single().Tick);
        }

        // Row 4: energy 10. The shot leaves 2, enough for 12 renewals (ticks 3 to 14). The tick that cannot pay acts as a
        // release, so the aura expires at tick 114; the key stays held, and the next shot cannot pay.
        [Test]
        public void RunningOutOfEnergyActsAsARelease()
        {
            var driver = CannonTests.Duel(PulsarShip(energy: 10f));
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(200);
            Assert.AreEqual(1, Shots(events).Count);
            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(114, removal.Tick);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
            Assert.AreEqual(0.08f, driver.Ship(1).Energy.Value, Tolerance);
        }

        // Legacy ContinuousCannon: pressing the key again while the aura shrinks renews it (0.16 per tick, no new shot).
        // Released at 150 and pressed at 160 (size 90), it grows back to full size at tick 169.
        [Test]
        public void PressingAgainWhileItShrinksRenewsTheAura()
        {
            var driver = CannonTests.Duel(PulsarShip());
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 150);
            driver.Control(1, systems: 1, tick: 160);

            var events = driver.StepCollecting(159);
            Assert.AreEqual(0.9f, Power(driver), Tolerance);
            var energy = driver.Ship(1).Energy.Value;
            events.AddRange(driver.StepCollecting());
            Assert.AreEqual(0.91f, Power(driver), Tolerance);
            Assert.AreEqual(energy - 0.16f, driver.Ship(1).Energy.Value, Tolerance);
            events.AddRange(driver.StepCollecting(240));
            Assert.AreEqual(1f, Power(driver), 0f);
            Assert.AreEqual(1, Shots(events).Count);
            Assert.IsEmpty(CannonTests.BulletRemovals(events));
        }

        // Rows 1 and 2: 25 damage per second (0.5 per tick) while the aura touches the target. The target's hull (radius
        // 1) stands 5 from the muzzle, so it touches from size 58 (radius 4.06): ticks 59 to 191 with the release at 150.
        [Test]
        public void TheAuraDamagesWhatItTouchesWhileItGrowsAndShrinks()
        {
            var driver = CannonTests.Duel(PulsarShip(), Target);
            driver.Place(101, new Vec2(56f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 150);

            var hits = driver.StepCollecting(260).Where(e => e.Kind == BattleEventKind.Hit).ToList();
            Assert.AreEqual(59, hits.First().Tick);
            Assert.AreEqual(191, hits.Last().Tick);
            Assert.AreEqual(200f - 0.5f * 133, driver.Ship(101).Armor.Value, Tolerance);
        }

        // Network (owner check): the copy takes the aura from the owner's record, renews it only by the controls it has
        // (none here), and never expires it by itself: it leaves with the owner's removal record.
        [Test]
        public void ACopyOfTheAuraLeavesWithTheOwnersRemovalRecord()
        {
            var link = new Link(PulsarShip());
            link.Owner.Control(1, systems: 1);
            link.Owner.Control(1, systems: 0, tick: link.Owner.Tick + 30);

            var ownerEvents = new List<BattleEvent>();
            var copyEvents = new List<BattleEvent>();
            for (var i = 0; i < 160; ++i)
            {
                ownerEvents.AddRange(link.Owner.StepCollecting());
                link.Capture();
                link.Deliver();
                copyEvents.AddRange(link.Copy.StepCollecting());
            }

            var spawn = Shots(ownerEvents).Single();
            Assert.AreEqual(spawn.EntityId, Shots(copyEvents).Single().EntityId);
            var ownerRemoval = CannonTests.BulletRemovals(ownerEvents).Single();
            var copyRemoval = CannonTests.BulletRemovals(copyEvents).Single();
            Assert.AreEqual(RemovalReason.Expiry, ownerRemoval.RemovalReason);
            Assert.Greater(ownerRemoval.Tick, spawn.Tick + 101);
            Assert.AreEqual(ownerRemoval.Tick, copyRemoval.Tick);
            Assert.AreEqual(RemovalReason.Expiry, copyRemoval.RemovalReason);
        }

        // The owner (20) flies the shooter, slot 1; the copy (10) only receives. Its own slot 102 stays in the hangar,
        // so the battle has a network part.
        private sealed class Link
        {
            private const int Owner20 = 20;
            private const int Receiver = 10;

            public Link(ShipDefinition shooter)
            {
                Owner = new Driver(Setup(Owner20, shooter));
                Copy = new Driver(Setup(Receiver, shooter));
                Owner.Deploy(1);
                Owner.Step();
                Capture();
                Deliver();
                Copy.Step();
                Owner.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                Copy.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            }

            public Driver Owner { get; }
            public Driver Copy { get; }

            public void Capture()
            {
                foreach (var operation in Owner.Battle.Snapshot.OutgoingOperations)
                {
                    _held.Add(operation);
                    _lastSequence = operation.Sequence;
                }
                _through = Owner.Battle.Snapshot.Tick;
            }

            public void Deliver()
            {
                foreach (var operation in _held)
                    if (!Copy.Battle.Submit(operation.Owner, operation)) throw new InvalidOperationException("Rejected record.");
                _held.Clear();
                if (!Copy.Battle.Submit(Owner20, new OwnerWatermark(Copy.BattleId, Owner20, _through, _lastSequence)))
                    throw new InvalidOperationException("Rejected watermark.");
            }

            private static BattleSetup Setup(int localOwner, ShipDefinition shooter) =>
                new BattleSetup(new BattleId(77), 13, new[]
                {
                    new RosterSlotSetup(new RosterId(1), 0, shooter, Owner20),
                    new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, Receiver),
                    new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship, Receiver),
                }, localOwners: new[] { localOwner });

            private readonly List<OwnerOperation> _held = new List<OwnerOperation>(64);
            private ulong _lastSequence;
            private long _through = -1;
        }
    }
}

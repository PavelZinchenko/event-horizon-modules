using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 4b: legacy ManageableCannon and StickyController (phase 5 plan, "Manageable and StickyMine (4b)").
    // The barrel of a ship deployed at tick 0 is first ready at tick 2.
    public class ManageableTests
    {
        private const float Tolerance = 1e-3f;
        private const int Owner = 20;
        private const int Receiver = 10;

        private static readonly BulletDefinition Fragment = new BulletDefinition(0.1f, 12f, 4f, 2f);

        // FragBomb_M_1: speed 6; children on Detonated and Expired. It does not detonate when destroyed, so only a key
        // detonation runs its Detonated trigger.
        private static BulletDefinition Bomb(float range = 50f) => new BulletDefinition(0.3f, 6f, range, 0f,
            triggers: new[]
            {
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Detonated | BulletTriggerConditions.Expired, Fragment),
            },
            detonateWhenDestroyed: false);

        // Fire rate 0.3: 167 ticks; energy 25.
        private static CannonDefinition Launcher(BulletDefinition bomb) =>
            new CannonDefinition(0, 1f / 0.3f, 25f, 0f, bomb, CannonMode.Manageable);

        private static ShipDefinition ManageableShip(BulletDefinition bomb, ResourceDefinition? energy = null,
            params DeviceDefinition[] more) =>
            Fixtures.ShipWith(energy: energy, barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { Launcher(bomb) }.Concat(more).ToArray());

        // StickyBomb (ammunition 156): size 1.5, speed 30, range 30, 50 hit points, 1 damage per second over time,
        // sticky lifetime 15 s (750 ticks); the explosion on Detonated.
        private static readonly BulletDefinition Blast = new BulletDefinition(2f, 0f, 0f, 20f,
            impact: BulletImpact.AllTargets, lifetime: 0.3f);

        private static readonly BulletDefinition StickyBomb = new BulletDefinition(0.75f, 30f, 30f, 1f, hitPoints: 50f,
            impact: BulletImpact.OverTime, detonateWhenDestroyed: false, stickyLifetime: 15f,
            triggers: new[] { BulletTriggerDefinition.Spawn(BulletTriggerConditions.Detonated, Blast) });

        private static ShipDefinition StickyShip() => Fixtures.CannonShip(StickyBomb, cooldown: 100f, energyCost: 0f);

        private static List<BattleEvent> ShotsOf(Driver driver, IEnumerable<BattleEvent> events, uint rosterId = 1) =>
            CannonTests.BulletSpawns(events).Where(e => e.ParentId == driver.Entity(rosterId)).ToList();

        private static List<BattleEvent> ChildrenOf(IEnumerable<BattleEvent> events, EntityId parent) =>
            CannonTests.BulletSpawns(events).Where(e => e.ParentId == parent).ToList();

        private static BattleEvent RemovalOf(IEnumerable<BattleEvent> events, EntityId id) =>
            CannonTests.BulletRemovals(events).Single(e => e.EntityId == id);

        private static DeviceState State(Driver driver, uint rosterId = 1, int index = 0)
        {
            var snapshot = driver.Battle.Snapshot;
            Assert.IsTrue(snapshot.TryGetShip(driver.Entity(rosterId), out var ship));
            return snapshot.GetDevices(ship)[index];
        }

        private static BulletEntity Entry(Driver driver, EntityId id)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && bullets[i].Id == id) return bullets[i];
            throw new AssertionException($"No bullet {id}.");
        }

        private static BulletState Bullet(Driver driver, EntityId id) =>
            driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.Id == id);

        // CannonTests.Duel with a reserve slot that stays in the hangar.
        private static Driver Duel(ShipDefinition shooter, uint reserve)
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, shooter),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(reserve), reserve < 100 ? 0 : 1, Fixtures.Ship),
            }));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        private static Vec2 Position(Driver driver, uint rosterId) =>
            driver.Battle.World.GetPosition(driver.Ship(rosterId).Body);

        // Rows 1, 2 and 4: the shot costs 25; releasing the key at tick 52 detonates the bomb (Detonated children,
        // though it does not detonate when destroyed). The weapon sees the loss at tick 53, so the next shot comes 167
        // ticks later, whatever the key did meanwhile.
        [Test]
        public void ReleasingTheKeyDetonatesTheBombAndItsLossStartsTheCooldown()
        {
            var driver = CannonTests.Duel(ManageableShip(Bomb()));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 52);
            driver.Control(1, systems: 1, tick: 60);

            var events = driver.StepCollecting(2);
            Assert.AreEqual(275f, driver.Ship(1).Energy.Value, Tolerance);
            events.AddRange(driver.StepCollecting(228));

            var shots = ShotsOf(driver, events);
            Assert.AreEqual(new long[] { 2, 220 }, shots.Select(e => e.Tick).ToArray());
            var removal = RemovalOf(events, shots[0].EntityId);
            Assert.AreEqual(52, removal.Tick);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreEqual(new long[] { 52 }, ChildrenOf(events, shots[0].EntityId).Select(e => e.Tick).ToArray());
        }

        // Row 3: a held bomb that expires at tick 27 is lost at tick 28; the key stays held, and the next shot comes at
        // 28 + 167.
        [Test]
        public void ABombLostWhileHeldStartsTheCooldownInTheNextTick()
        {
            var driver = CannonTests.Duel(ManageableShip(Bomb(range: 3f)));
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(200);
            var shots = ShotsOf(driver, events);
            Assert.AreEqual(new long[] { 2, 195 }, shots.Select(e => e.Tick).ToArray());
            var removal = RemovalOf(events, shots[0].EntityId);
            Assert.AreEqual(27, removal.Tick);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
        }

        // Row 5: pressed at tick 2, released at tick 3: the bomb detonates at tick 3, near the barrel.
        [Test]
        public void ABombReleasedInTheNextTickDetonatesNearTheBarrel()
        {
            var driver = CannonTests.Duel(ManageableShip(Bomb()));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 3);

            var events = driver.StepCollecting(3);
            var bomb = ShotsOf(driver, events).Single().EntityId;
            Assert.AreEqual(3, RemovalOf(events, bomb).Tick);
            var child = Bullet(driver, ChildrenOf(events, bomb).Single().EntityId);
            Assert.Less((child.Position - new Vec2(51f, 50f)).Length, 0.5f);
        }

        // Row 6: the bomb of a dead ship flies on until it expires; nobody detonates it. A reserve slot keeps the battle
        // going.
        [Test]
        public void TheBombOfADeadShipFliesOnUntilItExpires()
        {
            var driver = Duel(ManageableShip(Bomb(range: 6f)), reserve: 2);
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(9);
            var bomb = ShotsOf(driver, events).Single().EntityId;

            driver.Kill(1);
            events.AddRange(driver.StepCollecting(60));
            var removal = RemovalOf(events, bomb);
            Assert.AreEqual(52, removal.Tick);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
        }

        // Legacy ShipRetreatEffect makes the weapon inactive: a retreat detonates the bomb.
        [Test]
        public void ARetreatDetonatesTheBomb()
        {
            var driver = CannonTests.Duel(ManageableShip(Bomb()));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(9);
            var bomb = ShotsOf(driver, events).Single().EntityId;

            Assert.IsTrue(driver.Retreat(driver.Entity(1)));
            events.AddRange(driver.StepCollecting());
            var removal = RemovalOf(events, bomb);
            Assert.AreEqual(10, removal.Tick);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreEqual(1, ChildrenOf(events, bomb).Count);
        }

        // Legacy CanBeActivated: with a bomb in flight the weapon is ready without energy, and the state names the bomb.
        [Test]
        public void TheDeviceStateNamesTheBombInFlight()
        {
            var driver = CannonTests.Duel(ManageableShip(Bomb(), new ResourceDefinition(25f)));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(2);
            var bomb = ShotsOf(driver, events).Single().EntityId;

            var state = State(driver);
            Assert.AreEqual(0f, driver.Ship(1).Energy.Value);
            Assert.AreEqual(bomb, state.ActiveBullet);
            Assert.IsTrue(state.Ready);
            Assert.IsTrue(state.Active);
            Assert.AreEqual(0f, state.Cooldown);

            driver.Control(1, systems: 0);
            driver.Step(2);
            state = State(driver);
            Assert.IsTrue(state.ActiveBullet.IsNull);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(166f / 167f, state.Cooldown, Tolerance);
        }

        // Legacy defect, fixed (owner, 2026-11-03): a shot of another weapon on the bomb's barrel at tick 20 made the
        // held bomb inactive at tick 21, so it detonated. The bomb now flies on, and the weapon stays ready and active.
        [Test]
        public void AnotherShotOnTheBarrelLeavesTheBombAlone()
        {
            var driver = CannonTests.Duel(ManageableShip(Bomb(), null,
                new CannonDefinition(0, 10f, 0f, 0f, Fixtures.Bolt)));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 3, tick: 20);

            var events = driver.StepCollecting(25);
            var shots = ShotsOf(driver, events);
            Assert.AreEqual(new long[] { 2, 20 }, shots.Select(e => e.Tick).ToArray());
            Assert.IsEmpty(CannonTests.BulletRemovals(events).Where(e => e.EntityId == shots[0].EntityId));
            var state = State(driver);
            Assert.AreEqual(shots[0].EntityId, state.ActiveBullet);
            Assert.IsTrue(state.Ready);
            Assert.IsTrue(state.Active);
        }

        // Sticky mine: the bomb sticks to the enemy at the tick after its first contact, with the offset x 0.9, and
        // follows it. It detonates 749 ticks after the stick tick.
        [Test]
        public void AStickyBombSticksToItsTargetAndDetonatesAfterItsLifetime()
        {
            var driver = CannonTests.Duel(StickyShip());
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = new List<BattleEvent>();
            var bomb = default(EntityId);
            var before = Vec2.Zero;
            while (bomb.IsNull || !Entry(driver, bomb).Stuck)
            {
                Assert.Less(driver.Tick, 40);
                if (!bomb.IsNull) before = Bullet(driver, bomb).Position;
                events.AddRange(driver.StepCollecting());
                if (bomb.IsNull) bomb = ShotsOf(driver, events).SingleOrDefault().EntityId;
            }

            var stuck = driver.Battle.Snapshot.Tick;
            var enemy = Position(driver, 101);
            var expected = enemy + 0.9f * (before - enemy);
            var position = Bullet(driver, bomb).Position;
            Assert.AreEqual(expected.X, position.X, Tolerance);
            Assert.AreEqual(expected.Y, position.Y, Tolerance);

            // The enemy turns a quarter and moves: the bomb keeps its pose in the enemy frame.
            var offset = position - enemy;
            driver.Place(101, new Vec2(80f, 70f), Vec2.Zero, (float)(Math.PI / 2.0));
            driver.Step();
            position = Bullet(driver, bomb).Position;
            Assert.AreEqual(80f - offset.Y, position.X, Tolerance);
            Assert.AreEqual(70f + offset.X, position.Y, Tolerance);

            while (driver.Tick <= stuck + 749) events.AddRange(driver.StepCollecting());
            var removal = RemovalOf(events, bomb);
            Assert.AreEqual(stuck + 749, removal.Tick);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreSame(Blast, ChildrenOf(events, bomb).Single().BulletDefinition);
        }

        // Legacy keeps the stuck bomb inside the target's collider, so its damage over time goes on: one hit of 0.02 on
        // the target in every tick, though physics never hits the body the bomb rides on.
        [Test]
        public void AStuckBombKeepsDamagingItsTarget()
        {
            var driver = CannonTests.Duel(StickyShip());
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            var bomb = ShotsOf(driver, driver.StepCollecting(30)).Single().EntityId;
            Assert.IsTrue(Entry(driver, bomb).Stuck);

            var hits = driver.StepCollecting(50).Where(e => e.Kind == BattleEventKind.Hit && e.AttackerId == bomb).ToList();
            Assert.AreEqual(Enumerable.Range(31, 50).Select(t => (long)t).ToArray(), hits.Select(e => e.Tick).ToArray());
            Assert.That(hits.Select(e => e.EntityId), Is.All.EqualTo(driver.Entity(101)));
            Assert.That(hits.Select(e => e.Damage), Is.All.EqualTo(0.02f).Within(1e-6f));
        }

        // If the target leaves first, the bomb vanishes in the next tick without triggers.
        [Test]
        public void AStickyBombVanishesWhenItsTargetLeaves()
        {
            var driver = Duel(StickyShip(), reserve: 102);
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(30);
            var bomb = ShotsOf(driver, events).Single().EntityId;
            Assert.IsTrue(Entry(driver, bomb).Stuck);

            driver.Kill(101);
            events.AddRange(driver.StepCollecting(2));
            var removal = RemovalOf(events, bomb);
            Assert.AreEqual(32, removal.Tick);
            Assert.AreEqual(RemovalReason.Disposal, removal.RemovalReason);
            Assert.IsEmpty(ChildrenOf(events, bomb));
        }

        // H8: the copy never sticks by itself. It sticks to the owner's target when the owner's record arrives, and
        // takes the detonation and the explosion from the owner's records.
        [Test]
        public void TheCopyOfAStickyBombFollowsTheOwnersRecords()
        {
            var link = new Link(both: true, StickyShip());
            link.Owner.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            link.Copy.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            link.Owner.Control(1, systems: 1);

            var bomb = default(EntityId);
            while (true)
            {
                Assert.Less(link.Owner.Tick, 40);
                link.Owner.Step();
                link.Capture();
                if (bomb.IsNull) bomb = link.Owner.Battle.Snapshot.Bullets.ToArray().SingleOrDefault().Id;
                if (!bomb.IsNull && Entry(link.Owner, bomb).Stuck) break;
                link.Deliver();
                link.Copy.Step();
            }

            // The owner's records wait: the copy touches the target but flies on.
            for (var i = 0; i < 3; ++i)
            {
                link.Copy.Step();
                Assert.IsFalse(Entry(link.Copy, bomb).Stuck);
                link.Owner.Step();
                link.Capture();
            }

            link.Deliver();
            link.Copy.Step();
            Assert.IsTrue(Entry(link.Copy, bomb).Stuck);
            Assert.AreEqual(Position(link.Copy, 101).X, Bullet(link.Copy, bomb).Position.X, 2f);

            // The explosion's record comes a tick after the removal: its spawn tick is the next tick.
            var ownerEvents = new List<BattleEvent>();
            var copyEvents = new List<BattleEvent>();
            while (!ChildrenOf(copyEvents, bomb).Any())
            {
                Assert.Less(link.Owner.Tick, 800);
                ownerEvents.AddRange(link.Owner.StepCollecting());
                link.Capture();
                link.Deliver();
                copyEvents.AddRange(link.Copy.StepCollecting());
            }

            Assert.AreEqual(RemovalOf(ownerEvents, bomb).Tick, RemovalOf(copyEvents, bomb).Tick);
            Assert.AreEqual(RemovalReason.Death, RemovalOf(copyEvents, bomb).RemovalReason);
            Assert.AreEqual(ChildrenOf(ownerEvents, bomb).Single().EntityId, ChildrenOf(copyEvents, bomb).Single().EntityId);
        }

        // H8: the copy never detonates; the owner's removal record names the tick, and the copy starts the cooldown at
        // the next tick, as the owner does, though the record arrives late.
        [Test]
        public void TheCopyTakesTheCooldownFromTheOwnersRemovalRecord()
        {
            var link = new Link(both: false, ManageableShip(Bomb()));
            link.Owner.Control(1, systems: 1);
            link.Owner.Control(1, systems: 0, tick: link.Owner.Tick + 20);
            link.Step(25);
            Assert.AreEqual(State(link.Owner).Cooldown, State(link.Copy).Cooldown, 0f);
            Assert.Greater(State(link.Owner).Cooldown, 0f);

            // A late record: the copy holds the next bomb until the record arrives.
            link.Owner.Control(1, systems: 1, tick: link.Owner.Tick + 170);
            link.Owner.Control(1, systems: 0, tick: link.Owner.Tick + 190);
            link.Step(185);
            Assert.IsFalse(State(link.Copy).ActiveBullet.IsNull);
            for (var i = 0; i < 10; ++i)
            {
                link.Owner.Step();
                link.Capture();
                link.Copy.Step();
            }
            Assert.IsFalse(State(link.Copy).ActiveBullet.IsNull);
            Assert.IsTrue(State(link.Owner).ActiveBullet.IsNull);

            link.Deliver();
            link.Step(3);
            Assert.IsTrue(State(link.Copy).ActiveBullet.IsNull);
            Assert.AreEqual(State(link.Owner).Cooldown, State(link.Copy).Cooldown, 0f);
        }

        // The owner (20) flies the shooter, slot 1, and with both also the enemy, slot 101; the copy (10) only receives.
        // The copy's own slot 102 stays in the hangar, so the battle has a network part.
        private sealed class Link
        {
            public Link(bool both, ShipDefinition shooter)
            {
                Owner = new Driver(Setup(Owner20, both, shooter));
                Copy = new Driver(Setup(Receiver, both, shooter));
                Owner.Deploy(1);
                if (both) Owner.Deploy(101);
                Step();
                Owner.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                Copy.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            }

            private const int Owner20 = ManageableTests.Owner;

            public Driver Owner { get; }
            public Driver Copy { get; }

            public void Step(int count = 1)
            {
                for (var i = 0; i < count; ++i)
                {
                    Owner.Step();
                    Capture();
                    Deliver();
                    Copy.Step();
                }
            }

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

            private static BattleSetup Setup(int localOwner, bool both, ShipDefinition shooter) =>
                new BattleSetup(new BattleId(77), 13, new[]
                {
                    new RosterSlotSetup(new RosterId(1), 0, shooter, Owner20),
                    new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, both ? Owner20 : Receiver),
                    new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship, Receiver),
                }, localOwners: new[] { localOwner });

            private readonly List<OwnerOperation> _held = new List<OwnerOperation>(64);
            private ulong _lastSequence;
            private long _through = -1;
        }
    }
}

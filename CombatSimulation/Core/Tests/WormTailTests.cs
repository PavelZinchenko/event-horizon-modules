using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 7d: the WormTailV2 examples (TODO/battle_simulator_phase5.md, "WormTailV2"), the tail's contacts and
    // the owner records of its segments (H8).
    public class WormTailTests
    {
        private const float Tolerance = 1e-3f;

        // Record 20: power 0.3, size 10, cooldown 5 s, offset (0.45, 0.35); the WormSegment prefab circle.
        private static WormTailDefinition Tail(int size = 10) =>
            new WormTailDefinition(0.3f, size, 5f, 0.45f, 0.35f, 0.4419455f);

        // Armor 1,000 (segment hit points 300), kinetic resistance 0.2, no shield; scale 2, mass 10.
        private static ShipDefinition Worm(int size = 10) =>
            Fixtures.ShipWith(mass: 10f, armor: 1000f, shield: new ResourceDefinition(0f),
                devices: new DeviceDefinition[] { Tail(size) },
                resistances: LegacyDamageModel.Resistances(0.2f, 0f, 0f, 0f));

        // The worm deploys at tick 0 and grows its head there; at tick 1 it rests at (50, 50), facing +X.
        // A reserve slot of side 0 stays ready, so the battle goes on after the worm leaves.
        private static Driver WormDuel(ShipDefinition worm = null, ShipDefinition enemy = null)
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, worm ?? Worm()),
                new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(101), 1, enemy ?? Fixtures.Ship),
            }));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var world = driver.Battle.World;
            world.TeleportGroup(driver.Ship(1).Body, new Vec2(50f, 50f), 0f);
            world.SetLinearVelocity(driver.Ship(1).Body, Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        private static TailSegment Segment(Driver driver, int number, bool detached = false)
        {
            var segments = driver.Battle.Segments;
            for (var i = 0; i < segments.Capacity; ++i)
                if (segments[i].IsAlive && segments[i].Number == number && segments[i].Detached == detached)
                    return segments[i];
            return null;
        }

        private static int Count(Driver driver, bool detached = false)
        {
            var count = 0;
            foreach (var segment in driver.Battle.Snapshot.Segments)
                if (segment.Detached == detached) ++count;
            return count;
        }

        private static void Hit(Driver driver, int number, float damage, bool detached = false) =>
            driver.Battle.QueueSegmentHit(Segment(driver, number, detached), damage, Vec2.Zero);

        // Row 1: each segment grows the next 3 ticks (0.05 s) after its own spawn; scale and mass x 0.95, the head at
        // 0.95 x the ship scale and ship mass / 10.
        [Test]
        public void TheTailGrowsTenSegmentsBehindTheShip()
        {
            var driver = WormDuel();
            var ship = driver.Ship(1);
            var world = driver.Battle.World;
            var head = Segment(driver, 1);
            var offset = world.Torus.Delta(world.GetPosition(head.Body), world.GetPosition(ship.Body));
            Assert.AreEqual(0.6f * 2f + 0.35f * 1.9f, offset.Length, Tolerance, "where the hinge holds it");
            Assert.AreEqual(0f, offset.Y, Tolerance, "straight behind");

            driver.StepUntil(27);
            Assert.AreEqual(9, Count(driver));
            driver.Step();
            Assert.AreEqual(10, Count(driver));

            for (var number = 1; number <= 10; ++number)
            {
                var segment = Segment(driver, number);
                Assert.AreEqual(2f * (float)Math.Pow(0.95, number), segment.Scale, Tolerance);
                Assert.AreEqual((float)Math.Pow(0.95, number - 1), world.GetMass(segment.Body), Tolerance);
                Assert.AreEqual(300f, segment.HitPoints, Tolerance);
                Assert.AreEqual(SpawnIdentity.CreateChild(ship.Id, 3 * (number - 1), 0, (uint)number, 0), segment.Id);
            }
            Assert.AreEqual(JointKind.Hinge, world.GetJointKind(Segment(driver, 1).Joint));
            Assert.AreEqual(ship.Body, world.GetJointBodyA(Segment(driver, 1).Joint));
            Assert.AreEqual(Segment(driver, 1).Body, world.GetJointBodyA(Segment(driver, 2).Joint));
        }

        // Row 2: share p = attached segments / 10; each armor resistance r becomes p + (1 - p) r, corrosive toward 0.5.
        [Test]
        public void TheAttachedShareMovesEachArmorResistance()
        {
            var driver = WormDuel();
            var ship = driver.Ship(1);
            driver.StepUntil(16);
            ship.AddPendingDamage(LegacyDamageModel.Impact, 100f);
            driver.Step();
            Assert.AreEqual(0.6f, ship.ResistanceShare, Tolerance, "6 segments");
            Assert.AreEqual(0.6f, driver.Battle.Snapshot.Ships[0].ResistanceShare, Tolerance, "for the HUD");
            Assert.AreEqual(1000f - 100f * (1f - 0.68f), ship.Armor.Value, Tolerance);

            driver.StepUntil(30);
            var armor = ship.Armor.Value;
            ship.AddPendingDamage(LegacyDamageModel.Impact, 100f);
            ship.AddPendingDamage(LegacyDamageModel.Heat, 100f);
            ship.AddPendingDamage(LegacyDamageModel.Corrosive, 100f);
            driver.Step();
            Assert.AreEqual(1f, ship.ResistanceShare, Tolerance);
            Assert.AreEqual(armor - 50f, ship.Armor.Value, Tolerance, "A full tail stops all but half the corrosive damage.");
        }

        // Row 3: the damage goes to the tip first.
        [Test]
        public void AHitGoesToTheTipFirst()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            Hit(driver, 4, 200f);
            driver.Step();

            Assert.AreEqual(100f, Segment(driver, 10).HitPoints, Tolerance);
            for (var number = 1; number < 10; ++number)
                Assert.AreEqual(300f, Segment(driver, number).HitPoints, Tolerance);
            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value, Tolerance);
        }

        // Row 4: more than the hit points in a short time tears the segment off; the rest detach, still take hits on their
        // own hit points, and destroy themselves after 5 s.
        [Test]
        public void AHitAboveTheHitPointsTearsTheSegmentOff()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            var tick = driver.Tick;
            Hit(driver, 4, 350f);
            driver.Step();

            Assert.IsNull(Segment(driver, 4));
            Assert.AreEqual(3, Count(driver));
            Assert.AreEqual(6, Count(driver, detached: true));
            Assert.AreEqual(300f, Segment(driver, 10, detached: true).HitPoints, Tolerance, "a torn segment passes nothing");
            driver.Step();
            Assert.AreEqual(0.3f, driver.Ship(1).ResistanceShare, Tolerance);

            Hit(driver, 7, 100f, detached: true);
            driver.Step();
            Assert.AreEqual(200f, Segment(driver, 7, detached: true).HitPoints, Tolerance);
            Assert.AreEqual(300f, Segment(driver, 10, detached: true).HitPoints, Tolerance);

            driver.StepUntil(tick + 250);
            Assert.AreEqual(6, Count(driver, detached: true));
            driver.Step();
            Assert.AreEqual(0, Count(driver, detached: true));
        }

        // Phase 5b, step 7b: a segment that leaves gives the view an AuxiliaryRemoval with its reason, last in the batch
        // with the other view-only events.
        [Test]
        public void ADeadSegmentGivesItsRemovalEventLast()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            var id = Segment(driver, 4).Id;
            Hit(driver, 4, 350f);
            driver.Step();

            var events = driver.Battle.Snapshot.Events.ToArray();
            var index = Array.FindIndex(events, e => e.Kind == BattleEventKind.AuxiliaryRemoval);
            Assert.GreaterOrEqual(index, 0);
            Assert.AreEqual(id, events[index].EntityId);
            Assert.AreEqual(driver.Ship(1).Id, events[index].ParentId);
            Assert.AreEqual(RemovalReason.Death, events[index].RemovalReason);
            Assert.IsTrue(events.Skip(index).All(e => ScriptedBattle.IsViewOnly(e.Kind)));
        }

        // Row 4: the recent damage falls by 5 x the hit points per second, so two hits tear only close together.
        [TestCase(1, true)]
        [TestCase(10, false)]
        public void TwoHitsTearASegmentOnlyCloseTogether(int ticksBetween, bool tears)
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            Hit(driver, 4, 200f);
            driver.Step(ticksBetween);
            Hit(driver, 4, 200f);
            driver.Step();

            Assert.AreEqual(tears, Segment(driver, 4) == null);
        }

        // Row 5: 5 s after the loss the parent grows a new segment, then one more every 0.5 s. A hit that the parent
        // absorbs restarts its wait.
        [TestCase(false)]
        [TestCase(true)]
        public void ALostSegmentRegrowsAfterTheCooldown(bool hitTheParent)
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            var tick = driver.Tick;
            Hit(driver, 4, 350f);
            driver.Step();

            var regrowth = tick + 250;
            if (hitTheParent)
            {
                driver.StepUntil(tick + 100);
                Hit(driver, 3, 50f);
                regrowth = tick + 350;
            }

            driver.StepUntil(regrowth);
            Assert.IsNull(Segment(driver, 4));
            driver.Step();
            var segment = Segment(driver, 4);
            Assert.IsNotNull(segment);
            Assert.AreEqual(SpawnIdentity.CreateChild(driver.Ship(1).Id, regrowth, 0, 4, 0), segment.Id);

            driver.StepUntil(regrowth + 25);
            Assert.IsNull(Segment(driver, 5));
            driver.Step();
            Assert.IsNotNull(Segment(driver, 5));
        }

        // Row 6: segments 1 and 2 never break while attached. A smaller hit lowers them; the damage the tail cannot take
        // passes to the ship as impact damage, and their hit points stay.
        [Test]
        public void TheFirstTwoSegmentsPassTheExcessToTheShip()
        {
            var driver = WormDuel(Worm(3));
            driver.StepUntil(8);
            Hit(driver, 3, 350f);
            driver.Step();
            Assert.IsNull(Segment(driver, 3));

            Hit(driver, 1, 200f);
            driver.Step();
            Assert.AreEqual(300f, Segment(driver, 1).HitPoints, Tolerance);
            Assert.AreEqual(100f, Segment(driver, 2).HitPoints, Tolerance);

            Hit(driver, 1, 500f);
            driver.Step();
            Assert.AreEqual(300f, Segment(driver, 1).HitPoints, Tolerance);
            Assert.AreEqual(100f, Segment(driver, 2).HitPoints, Tolerance);
            var resistance = 2f / 3f + (1f - 2f / 3f) * 0.2f;
            Assert.AreEqual(1000f - 100f * (1f - resistance), driver.Ship(1).Armor.Value, Tolerance);
        }

        // Row 7 (owner, 2026-11-02): a ship that dies takes the head of its tail; the rest detach and destroy themselves
        // after 5 s. Detached segments keep their own timers.
        [Test]
        public void TheTailDetachesWhenItsShipDies()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            var tearTick = driver.Tick;
            Hit(driver, 8, 350f);
            driver.Step();
            Assert.AreEqual(2, Count(driver, detached: true));

            driver.StepUntil(tearTick + 100);
            var deathTick = driver.Tick;
            driver.Kill(1);
            driver.Step();
            Assert.AreEqual(0, Count(driver));
            Assert.AreEqual(8, Count(driver, detached: true), "segments 2 to 7, and 9 and 10 from the tear");
            Assert.IsNull(Segment(driver, 1, detached: true));
            Assert.IsFalse(driver.Battle.World.IsAlive(Segment(driver, 2, detached: true).Joint));
            Assert.IsTrue(driver.Battle.World.IsAlive(Segment(driver, 3, detached: true).Joint), "the rest stay joined");

            driver.StepUntil(tearTick + 251);
            Assert.AreEqual(6, Count(driver, detached: true));
            driver.StepUntil(deathTick + 250);
            Assert.AreEqual(6, Count(driver, detached: true));
            driver.Step();
            Assert.AreEqual(0, driver.Battle.Snapshot.Segments.Length);
        }

        // Row 7: a ship that retreats or vanishes takes every attached segment (legacy Vanish detaches nothing).
        [Test]
        public void TheTailVanishesWithItsShip()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            Hit(driver, 8, 350f);
            driver.Step();

            driver.Battle.Submit(ShipCommand.Vanish(driver.BattleId, 900, driver.Tick, driver.Entity(1)));
            driver.Step();
            Assert.AreEqual(0, Count(driver));
            Assert.AreEqual(2, Count(driver, detached: true));
            driver.Step(250);
            Assert.AreEqual(0, driver.Battle.Snapshot.Segments.Length);
        }

        // Row 8: a segment far from its parent moves back behind it, with the parent's velocity and no spin.
        [Test]
        public void ASegmentFarFromItsParentMovesBack()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            var world = driver.Battle.World;
            var parent = Segment(driver, 2);
            var segment = Segment(driver, 3);
            world.SetPose(segment.Body, new Vec2(80f, 80f), 1f);
            world.SetAngularVelocity(segment.Body, 5f);
            driver.Step();

            var distance = world.Torus.Delta(world.GetPosition(parent.Body), world.GetPosition(segment.Body)).Length;
            Assert.AreEqual(0.45f * parent.Scale + 0.35f * segment.Scale, distance, 0.05f);
            Assert.AreEqual(world.GetAngle(parent.Body), world.GetAngle(segment.Body), 0.05f);
            Assert.AreEqual(0f, world.GetAngularVelocity(segment.Body), 0.5f);
        }

        // Row 9 (P4): a regrowth waits while its place overlaps a body the segment would touch.
        [Test]
        public void ARegrowthWaitsForFreeSpace()
        {
            var driver = WormDuel(Worm(3));
            driver.StepUntil(8);
            var tick = driver.Tick;
            Hit(driver, 3, 350f);
            driver.Step();

            var world = driver.Battle.World;
            var parent = Segment(driver, 2);
            var place = world.Torus.Wrap(world.GetPosition(parent.Body) +
                world.GetRotation(parent.Body).Rotate(new Vec2(-(0.45f * parent.Scale + 0.35f * 2f * 0.857375f), 0f)));
            var blocker = world.CreateBody(BodyDef.Create(place, 1f, BodyFlags.FixedPosition));
            world.AddCircle(blocker, new Circle(Vec2.Zero, 0.3f),
                new ShapeDef { Filter = CollisionFilters.Ship(Allegiances.OfSide(1)) });

            driver.StepUntil(tick + 270);
            Assert.IsNull(Segment(driver, 3));
            world.DestroyBody(blocker);
            driver.Step();
            Assert.IsNotNull(Segment(driver, 3));
        }

        // A bolt that meets the tail stops there and its damage goes to the tip.
        [TestCase(false)]
        [TestCase(true)]
        public void ABoltOnTheTailDamagesTheTip(bool ignoreShield)
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, effects: new ImpactEffects(ignoreShield: ignoreShield));
            var driver = WormDuel(enemy: Fixtures.CannonShip(bullet));
            driver.StepUntil(30);
            driver.Place(101, new Vec2(45f, 56f), Vec2.Zero, (float)(-Math.PI / 2));
            driver.Control(101, systems: 1);
            driver.Control(101, systems: 0, tick: driver.Tick + 1);
            driver.Step(20);

            Assert.AreEqual(295f, Segment(driver, 10).HitPoints, Tolerance);
            Assert.AreEqual(0, driver.Battle.Bullets.AliveCount);
            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value, Tolerance);
        }

        // A hostile ship rams a segment; the segment deals no ramming damage.
        [Test]
        public void AShipRamsASegment()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            var world = driver.Battle.World;
            var target = world.GetPosition(Segment(driver, 5).Body);
            driver.Place(101, target + new Vec2(0f, 2.5f), new Vec2(0f, -30f));
            driver.Step(5);

            var lost = 300f - Segment(driver, 10).HitPoints;
            Assert.Greater(lost, 1f);
            Assert.Less(lost, 0.1f * 30f * 0.82f + Tolerance);
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value, Tolerance);
        }

        // P4 closure: a ship with a tail turns no faster than the fastest database worm; other ships keep their engine.
        [Test]
        public void ATailCapsTheTurn()
        {
            var fast = new EngineDefinition(20f, 10f, 3000f * (float)(Math.PI / 180), 1200f * (float)(Math.PI / 180));
            var worm = Fixtures.ShipWith(engine: fast, engineWithoutEnergy: fast, devices: new DeviceDefinition[] { Tail() });
            Assert.AreEqual(WormTailDefinition.MaxTurnRate, worm.Engine.TurnRate, 1e-6f);
            Assert.AreEqual(WormTailDefinition.MaxAngularVelocity, worm.Engine.MaxAngularVelocity, 1e-6f);
            Assert.AreEqual(WormTailDefinition.MaxTurnRate, worm.EngineWithoutEnergy.TurnRate, 1e-6f);
            Assert.AreEqual(20f, worm.Engine.Propulsion);
            Assert.AreSame(fast, Fixtures.ShipWith(engine: fast).Engine);
            Assert.AreSame(Fixtures.Engine, Worm().Engine, "the fixture engine is below the cap");
        }

        [Test]
        public void ADefinitionRejectsInvalidValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WormTailDefinition(0.3f, 0, 5f, 0.45f, 0.35f, 0.44f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WormTailDefinition(-1f, 10, 5f, 0.45f, 0.35f, 0.44f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WormTailDefinition(0.3f, 10, 5f, 0.45f, 0.35f, 0f));
        }

        [Test]
        public void TheSnapshotListsTheSegments()
        {
            var driver = WormDuel();
            driver.StepUntil(30);
            var segments = driver.Battle.Snapshot.Segments.ToArray();
            Assert.AreEqual(Enumerable.Range(1, 10), segments.Select(s => s.Number).OrderBy(n => n));
            Assert.IsTrue(segments.All(s => s.ShipId == driver.Entity(1) && s.Side == 0 && !s.Detached));
            var tip = segments.Single(s => s.Number == 10);
            Assert.AreEqual(Segment(driver, 10).Id, tip.Id);
            Assert.AreEqual(2f * (float)Math.Pow(0.95, 10), tip.Scale, Tolerance);
        }

        // H8: the owner grows and removes segments and sends one record for each; the copy follows the records.

        private const int RemoteOwner = 20;

        private static BattleSetup NetworkSetup(int localOwner) => new BattleSetup(new BattleId(77), 13,
            new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(), 10),
                new RosterSlotSetup(new RosterId(101), 1, Worm(), RemoteOwner),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship, RemoteOwner),
            }, localOwners: new[] { localOwner });

        private static (Driver owner, Driver remote, Link link) Pair()
        {
            var owner = new Driver(NetworkSetup(RemoteOwner));
            var remote = new Driver(NetworkSetup(10));
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

        private static EntityId[] Ids(Driver driver, bool detached = false) => driver.Battle.Snapshot.Segments.ToArray()
            .Where(s => s.Detached == detached).OrderBy(s => s.Number).Select(s => s.Id).ToArray();

        [Test]
        public void TheCopyBuildsTheOwnersSegments()
        {
            var (owner, remote, link) = Pair();
            for (var i = 0; i < 30; ++i) Tick(owner, remote, link);

            Assert.AreEqual(10, Ids(owner).Length);
            CollectionAssert.AreEqual(Ids(owner), Ids(remote));
            var spawns = owner.Battle.Snapshot.OutgoingOperations.ToArray();
            Assert.IsEmpty(spawns, "no record once the tail is full");
        }

        [Test]
        public void ALateRecordBuildsTheMissingSegments()
        {
            var (owner, remote, link) = Pair();
            for (var i = 0; i < 30; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            Assert.AreEqual(1, Ids(remote).Length, "the copy never grows a segment itself");

            link.Deliver(remote);
            remote.Step();
            CollectionAssert.AreEqual(Ids(owner), Ids(remote));
        }

        [Test]
        public void TheCopyDetachesAndRemovesSegmentsByTheOwnersRecords()
        {
            var (owner, remote, link) = Pair();
            for (var i = 0; i < 30; ++i) Tick(owner, remote, link);
            var tick = owner.Tick;
            owner.Battle.QueueSegmentHit(Segment(owner, 4), 350f, Vec2.Zero);
            Tick(owner, remote, link);

            Assert.AreEqual(3, Ids(remote).Length);
            CollectionAssert.AreEqual(Ids(owner, detached: true), Ids(remote, detached: true));
            Tick(owner, remote, link);
            Assert.AreEqual(0.3f, remote.Ship(101).ResistanceShare, Tolerance, "the copy's share follows its chain");

            while (owner.Tick <= tick + 250) Tick(owner, remote, link);
            Assert.IsEmpty(Ids(owner, detached: true));
            Assert.IsEmpty(Ids(remote, detached: true));
            Assert.AreEqual(4, Ids(remote).Length, "the regrown segment 4");
        }

        // Row 7 on the copy: the owner's death record detaches the same segments; they leave by the owner's records.
        [Test]
        public void TheCopyDetachesTheTailOfADeadShip()
        {
            var (owner, remote, link) = Pair();
            for (var i = 0; i < 30; ++i) Tick(owner, remote, link);
            owner.Kill(101);
            Tick(owner, remote, link);

            Assert.IsNull(remote.Ship(101));
            Assert.IsEmpty(Ids(remote));
            Assert.AreEqual(9, Ids(remote, detached: true).Length);
            CollectionAssert.AreEqual(Ids(owner, detached: true), Ids(remote, detached: true));

            for (var i = 0; i < 249; ++i) Tick(owner, remote, link);
            Assert.AreEqual(9, Ids(remote, detached: true).Length);
            Tick(owner, remote, link);
            Assert.IsEmpty(Ids(owner, detached: true));
            Assert.IsEmpty(Ids(remote, detached: true));
        }

        // 9d: a contact on a segment counts once, whether the attacker's or the owner's device saw it. A ram (device -1)
        // counts once per tick.
        [Test]
        public void TheOwnerAppliesARemoteContactOnItsSegmentOnce()
        {
            var (owner, remote, link) = Pair();
            for (var i = 0; i < 30; ++i) Tick(owner, remote, link);
            var tip = Segment(owner, 10);
            var attacker = new EntityId(12345);
            ResolvedHit Contact(int device, ulong sequence, long tick) => new ResolvedHit(owner.BattleId, tick, attacker, 10,
                device, Segment(owner, 4).Id, RemoteOwner, 0, HitContactState.Start, HitImpactKind.Energy, 40f, sequence,
                targetIsBullet: true);

            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, Contact(0, 1, owner.Tick - 1))));
            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, Contact(0, 2, owner.Tick - 1))));
            owner.Step();
            Assert.AreEqual(260f, tip.HitPoints, Tolerance, "a projectile counts once");

            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, Contact(-1, 3, owner.Tick - 1))));
            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, Contact(-1, 4, owner.Tick))));
            owner.Step(2);
            Assert.AreEqual(180f, tip.HitPoints, Tolerance, "a ram counts at each tick");
        }

        // A local bolt on the copy's tail sends the contact to the owner; the copy does not damage the segment itself.
        [Test]
        public void ALocalBoltOnTheCopysTailClaimsTheContact()
        {
            var (owner, remote, link) = Pair();
            for (var i = 0; i < 30; ++i) Tick(owner, remote, link);
            remote.Deploy(1);
            remote.Step();
            var world = remote.Battle.World;
            world.TeleportGroup(remote.Ship(101).Body, new Vec2(50f, 50f), 0f);
            var target = world.GetPosition(Segment(remote, 3).Body);
            remote.Place(1, target + new Vec2(0f, 6f), Vec2.Zero, (float)(-Math.PI / 2));
            remote.Control(1, systems: 1);
            remote.Control(1, systems: 0, tick: remote.Tick + 3);

            var claims = new List<ResolvedHit>();
            for (var i = 0; i < 20; ++i)
            {
                remote.Step();
                claims.AddRange(remote.Battle.Snapshot.OutgoingHits.ToArray());
            }

            var claim = claims.Single();
            Assert.IsTrue(claim.TargetIsBullet);
            Assert.AreEqual(Segment(remote, 3).Id, claim.TargetId);
            Assert.AreEqual(5f, claim.Amount, Tolerance);
            Assert.AreEqual(300f, Segment(remote, 10).HitPoints, Tolerance);

            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, claim)));
            owner.StepUntil(claim.Tick + 1);
            owner.Step();
            Assert.AreEqual(295f, Segment(owner, 10).HitPoints, Tolerance);
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

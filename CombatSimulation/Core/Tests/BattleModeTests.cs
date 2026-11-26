using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Modes;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Battle modes (phase 2): the mode hooks, the mode actions, ModeCommand, mode events, respawn and the unit performance.
    public class BattleModeTests
    {
        private static BattleSetup WaveSetup(int lives = 2, ulong matchSeed = 7) =>
            new BattleSetup(new BattleId(1), matchSeed, Array.Empty<UnitSetup>(), 400f, 400f,
                rules: new WaveRules(Fixtures.Ship, Fixtures.Ship, lives, waves: 2, waveSize: 2, waveInterval: 100));

        private static List<string> Run(Battle battle, long ticks)
        {
            var log = new List<string>();
            for (var i = 0; i < ticks && !battle.IsCompleted; ++i)
            {
                battle.Step();
                foreach (var item in battle.Snapshot.Events)
                    if (item.Kind != BattleEventKind.Damage) log.Add(item.ToString());
            }
            return log;
        }

        private static UnitState Unit(Battle battle, UnitId id) =>
            battle.Snapshot.Units.ToArray().Single(unit => unit.UnitId == id);

        [Test]
        public void AModeSpawnsRespawnsAndEndsABattleWithoutCoreChanges()
        {
            var battle = new Battle(WaveSetup()) { ThrowOnDefect = true };

            var log = Run(battle, 150);
            Assert.AreEqual(5, battle.Snapshot.Units.Length, "The player and two waves of two.");
            Assert.AreEqual(Allegiance.Player, battle.Snapshot.Ships[0].Allegiance);
            Assert.AreEqual(WaveMode.Start, battle.Snapshot.Ships[0].Position);
            Assert.That(log, Has.Some.Match(@"^1\.\d+ Mode 2 \(1, 0\)$"), "Wave 1 arrives at tick 1.");
            Assert.That(log, Has.Some.Match(@"^101\.\d+ Mode 2 \(2, 0\)$"));
            foreach (var enemy in battle.Snapshot.Ships.ToArray().Where(ship => ship.Side == 1))
                Assert.AreEqual(WaveMode.WaveDistance, (enemy.Position - WaveMode.Start).Length, 10f);

            var first = Unit(battle, WaveMode.Player).ActiveEntity;
            battle.QueueDeath(first);
            log = Run(battle, 1);
            Assert.That(log, Has.Some.EndsWith(" Roster 1: Active -> Destroyed (Killed, revision 2, entity " + first + ")"));
            Assert.That(log, Has.Some.Match(@"^150\.\d+ Mode 1 \(1, 0\)$"), "One life left.");

            log = Run(battle, WaveMode.RespawnTicks);
            var player = Unit(battle, WaveMode.Player);
            Assert.AreEqual(UnitStatus.Active, player.Status);
            Assert.AreEqual(2u, player.DeploymentCount);
            Assert.AreNotEqual(first, player.ActiveEntity);
            Assert.That(log, Has.Some.Match(@"^400\.\d+ Roster 1: Destroyed -> Ready \(Respawned, revision 3"));
            Assert.That(log, Has.Some.Match(@"^400\.\d+ Roster 1: Ready -> Active \(Deployed, revision 4"));

            Assert.IsTrue(battle.Submit(new ModeCommand(battle.BattleId, 1, battle.Tick, WaveMode.KillAllEnemies)));
            Run(battle, 1);
            Assert.IsTrue(battle.IsCompleted);
            var result = battle.Snapshot.Result;
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(0, result.WinningSide);
            Assert.AreEqual(BattleEndReason.Mode, result.Reason);
            Assert.AreEqual(WaveMode.LevelComplete, result.ModeReason);
            Assert.AreEqual("Victory of side 0 (Mode 1) at tick 401", result.ToString());
            Assert.AreEqual(4, battle.Snapshot.Units.ToArray().Count(unit => unit.Status == UnitStatus.Destroyed));
        }

        [Test]
        public void TheLastLifeEndsTheBattle()
        {
            var battle = new Battle(WaveSetup(lives: 1)) { ThrowOnDefect = true };
            Run(battle, 10);
            battle.QueueDeath(Unit(battle, WaveMode.Player).ActiveEntity);
            Run(battle, 1);
            Assert.AreEqual(WaveMode.OutOfLives, battle.Snapshot.Result.ModeReason);
            Assert.AreEqual(1, battle.Snapshot.Result.WinningSide);
        }

        [Test]
        public void TheModeGivesTheSameBattleTwice()
        {
            // Interleaved, as the scripted battle checks: no state is shared between the two battles.
            var a = new Battle(WaveSetup());
            var b = new Battle(WaveSetup());
            var logA = new List<string>();
            var logB = new List<string>();
            for (var tick = 0; tick < 420; ++tick)
            {
                if (tick == 150)
                {
                    a.QueueDeath(Unit(a, WaveMode.Player).ActiveEntity);
                    b.QueueDeath(Unit(b, WaveMode.Player).ActiveEntity);
                }
                logA.AddRange(Run(a, 1));
                logB.AddRange(Run(b, 1));
            }
            CollectionAssert.AreEqual(logA, logB);

            var other = new Battle(WaveSetup(matchSeed: 8));
            Run(other, 2);
            Assert.AreNotEqual(Unit(a, new UnitId(101)).ActiveEntity, default(EntityId));
            Assert.AreNotEqual(a.Snapshot.Ships[1].Position, other.Snapshot.Ships[1].Position,
                "The match seed changes the mode's random directions.");
        }

        [Test]
        public void ModeCommandsReachTheModeInCommandIdOrder()
        {
            var rules = new ScriptRules();
            var battle = new Battle(new BattleSetup(new BattleId(3), 7, Fixtures.Units(1, 1), rules: rules));
            Assert.IsTrue(battle.Submit(new ModeCommand(battle.BattleId, 9, 1, 5, 50, 51)));
            Assert.IsTrue(battle.Submit(new ModeCommand(battle.BattleId, 4, 1, 6)));
            Assert.IsTrue(battle.Submit(new ModeCommand(battle.BattleId, 2, 0, 7)));
            Assert.IsFalse(battle.Submit(new ModeCommand(battle.BattleId, 4, 2, 8)), "The command ID is pending.");
            Assert.IsFalse(battle.Submit(new ModeCommand(new BattleId(4), 10, 1, 8)), "Another battle.");
            battle.Step();
            CollectionAssert.AreEqual(new[] { 7 }, rules.Mode.Inputs.Select(input => input.Kind));
            battle.Step();
            CollectionAssert.AreEqual(new[] { 7, 6, 5 }, rules.Mode.Inputs.Select(input => input.Kind));
            Assert.AreEqual(50, rules.Mode.Inputs[2].Argument0);
            Assert.AreEqual(51, rules.Mode.Inputs[2].Argument1);
            Assert.IsFalse(battle.Submit(new ModeCommand(battle.BattleId, 11, 1, 8)), "A past tick.");
        }

        [Test]
        public void AnOpenBattleHasNoLimitAndNoResult()
        {
            var battle = new Battle(new BattleSetup(new BattleId(1), 7, Fixtures.Units(2, 0)));
            battle.Submit(UnitCommand.Deploy(battle.BattleId, 1, 0, new UnitId(1)));
            battle.Submit(UnitCommand.Deploy(battle.BattleId, 2, 0, new UnitId(2)));
            battle.Step();
            Assert.AreEqual(2, battle.Snapshot.Ships.Length);
            battle.QueueDeath(battle.Snapshot.Ships[0].Id);
            battle.QueueDeath(battle.Snapshot.Ships[1].Id);
            battle.Step();
            Assert.IsFalse(battle.IsCompleted);
            Assert.AreEqual(0, battle.Snapshot.TimerElapsedTicks);
        }

        [Test]
        public void TheModeVetoesADeployCommand()
        {
            var rules = new ScriptRules();
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), rules: rules));
            rules.Mode.Veto = unit => unit.Side == 1 ? "no enemies" : null;
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            Assert.AreEqual(UnitStatus.Active, driver.Status(1));
            Assert.AreEqual(UnitStatus.Ready, driver.Status(101));
            Assert.AreEqual(1, driver.Battle.RejectedCommandCount);
        }

        [Test]
        public void PlacementsAndAddedUnitsGrowTheSnapshot()
        {
            var rules = new ScriptRules();
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, Array.Empty<UnitSetup>(), rules: rules));
            var at = new Vec2(30f, 40f);
            rules.Mode.Next.Add(context =>
            {
                context.AddUnit(new UnitSetup(new UnitId(1), 0, Fixtures.Ship));
                context.AddUnit(new UnitSetup(new UnitId(2), 1, Fixtures.Ship, allegiance: Allegiance.Neutral));
                Assert.IsTrue(context.Deploy(new UnitId(1), SpawnPlacement.At(at + new Vec2(200f, 0f), 1f, new Vec2(2f, 0f), 0.5f)));
                // Near the first ship: the search moves the second one off the overlap.
                Assert.IsTrue(context.Deploy(new UnitId(2), SpawnPlacement.Near(at, 0f, 10f)));
                Assert.IsFalse(context.Deploy(new UnitId(2), SpawnPlacement.Auto), "Already active.");
                Assert.IsFalse(context.Deploy(new UnitId(3), SpawnPlacement.Auto), "Unknown.");
                Assert.Throws<ArgumentException>(() => context.AddUnit(new UnitSetup(new UnitId(1), 0, Fixtures.Ship)));
            });
            driver.Step();
            var ships = driver.Battle.Snapshot.Ships;
            Assert.AreEqual(2, driver.Battle.Snapshot.Units.Length);
            Assert.AreEqual(at.X, ships[0].Position.X, 0.1f, "The torus wraps the position.");
            Assert.AreEqual(1f, ships[0].Angle, 0.05f);
            Assert.Greater(ships[0].LinearVelocity.X, 0.5f, "The engine brakes, but the ship keeps its direction.");
            Assert.Greater((ships[1].Position - ships[0].Position).Length, 1.9f);
            Assert.AreEqual(Allegiance.Neutral, ships[1].Allegiance);

            var copy = driver.Battle.Snapshot.CopyTo();
            Assert.AreEqual(2, copy.Units.Length);
            rules.Mode.Next.Add(context => context.AddUnit(new UnitSetup(new UnitId(3), 1, Fixtures.Ship)));
            driver.Step();
            Assert.AreEqual(3, driver.Battle.Snapshot.CopyTo(copy).Units.Length);
        }

        // TODO/captain_perks.md, C11: the mode's teleport wraps the position, sets the angle, stops the ship, and adds a
        // Teleport event with device index -1.
        [Test]
        public void TeleportMovesAndStopsTheShip()
        {
            var rules = new ScriptRules();
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, Array.Empty<UnitSetup>(), 400f, 400f,
                rules: rules));
            rules.Mode.Next.Add(context =>
            {
                context.AddUnit(new UnitSetup(new UnitId(1), 0, Fixtures.Ship));
                Assert.IsTrue(context.Deploy(new UnitId(1), SpawnPlacement.At(new Vec2(50f, 50f), 0f, new Vec2(5f, 0f), 1f)));
            });
            driver.Step();
            var ship = driver.Entity(1);
            var from = driver.Battle.Snapshot.Ships[0].Position;
            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.Teleport(ship, new Vec2(430f, 60f), 2f));
                Assert.IsFalse(context.Teleport(new EntityId(999), Vec2.Zero, 0f), "Unknown.");
                Assert.Throws<ArgumentOutOfRangeException>(() => context.Teleport(ship, Vec2.Zero, float.NaN));
            });
            driver.Step();

            var state = driver.Battle.Snapshot.Ships[0];
            Assert.AreEqual(30f, state.Position.X, 0.1f, "The torus wraps the position.");
            Assert.AreEqual(60f, state.Position.Y, 0.1f);
            Assert.AreEqual(2f, state.Angle, 0.05f);
            Assert.AreEqual(0f, state.LinearVelocity.Length, 0.1f, "Only the engine moves it after the jump.");
            var item = driver.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Teleport);
            Assert.AreEqual(ship, item.EntityId);
            Assert.AreEqual(-1, item.DeviceIndex);
            Assert.AreEqual(from.X, item.Point.X, 0.5f);
            Assert.AreEqual(30f, item.Destination.X, 0.1f);
        }

        // Exploration objects, step 3: an objective's death needs no new event. At stage 13 the mode reads a tagged
        // asteroid's removal and a unit's Killed transition in the tick's events.
        [Test]
        public void TheModeSeesTheDeathOfATaggedAsteroidAndAUnitInTheSameTick()
        {
            var rules = new ScriptRules();
            var asteroids = new[] { new AsteroidSetup(new Vec2(300f, 300f), Vec2.Zero, 4f, 80f, 1600f, 1f, tag: 42) };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), rules: rules,
                asteroids: asteroids));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();

            var seen = new List<BattleEvent>();
            rules.Mode.End = context =>
            {
                foreach (var item in context.TickEvents)
                    if (item.Kind == BattleEventKind.AsteroidRemoval ||
                        item.Kind == BattleEventKind.UnitTransition && item.TransitionReason == UnitTransitionReason.Killed)
                        seen.Add(item);
            };
            var ship = driver.Entity(101);
            driver.Battle.QueueDeath(ship);
            driver.Battle.Asteroids.Items[0].PendingDeath = true;
            driver.Step();

            Assert.AreEqual(2, seen.Count);
            var unit = seen.Single(e => e.Kind == BattleEventKind.UnitTransition);
            Assert.AreEqual(new UnitId(101), unit.UnitId);
            Assert.AreEqual(ship, unit.EntityId);
            Assert.AreEqual(42L, seen.Single(e => e.Kind == BattleEventKind.AsteroidRemoval).Tag);
        }

        [Test]
        public void ModeActionsRunOnlyInBeginTick()
        {
            var rules = new ScriptRules();
            var battle = new Battle(new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), rules: rules));
            rules.Mode.End = context => context.AddUnit(new UnitSetup(new UnitId(5), 0, Fixtures.Ship));
            Assert.Throws<InvalidOperationException>(() => battle.Step());

            var second = new Battle(battle.Setup);
            rules.Mode.End = context => context.Drain(new UnitId(1), 0.1f, 0.1f);
            Assert.Throws<InvalidOperationException>(() => second.Step());
        }

        [Test]
        public void ARemoteOwnerBattleRejectsModeActions()
        {
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.Ship, owner: 10),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship, owner: 20),
            };
            var battle = new Battle(new BattleSetup(new BattleId(1), 7, units, rules: rules, localOwners: new[] { 10 }));
            rules.Mode.Next.Add(context =>
            {
                Assert.Throws<InvalidOperationException>(() => context.Deploy(new UnitId(1), SpawnPlacement.Auto));
                Assert.Throws<InvalidOperationException>(() => context.DestroyUnit(new UnitId(1)));
                Assert.Throws<InvalidOperationException>(() => context.SetVelocity(default, Vec2.Zero));
                Assert.Throws<InvalidOperationException>(() => context.Teleport(default, Vec2.Zero, 0f));
                Assert.Throws<InvalidOperationException>(() => context.SetNoDamage(new UnitId(1), true));
                Assert.Throws<InvalidOperationException>(() => context.RemoveUnit(new UnitId(1)));
                Assert.Throws<InvalidOperationException>(() => context.VanishAsteroid(default));
                Assert.Throws<InvalidOperationException>(() => context.VanishBullet(default));
                context.Drain(new UnitId(1), 0f, 0f);
                context.Publish(new ModeEvent(3));
            });
            battle.Step();
            Assert.AreEqual(BattleEventKind.Mode, battle.Snapshot.Events[0].Kind);
        }

        [Test]
        public void DestroyUnitRemovesTheShipAndTheReserve()
        {
            var rules = new ScriptRules();
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, Fixtures.Units(2, 1), rules: rules));
            driver.Deploy(1);
            driver.Step();
            var ship = driver.Entity(1);
            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.DestroyUnit(new UnitId(1)));
                Assert.IsTrue(context.DestroyUnit(new UnitId(2)));
                Assert.IsFalse(context.DestroyUnit(new UnitId(7)));
            });
            var events = driver.StepCollecting();
            Assert.AreEqual(UnitStatus.Destroyed, driver.Status(1));
            Assert.AreEqual(UnitStatus.Destroyed, driver.Status(2));
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Removal && e.EntityId == ship && e.RemovalReason == RemovalReason.Death));
            Assert.AreEqual(2, events.Count(e => e.TransitionReason == UnitTransitionReason.ForcedDestruction));
            Assert.AreEqual(UnitStatus.Ready, rules.Mode.Changes[2].Before, "The reserve had no entity.");
        }

        // Common level mode, decision 1: the ship vanishes, then the drones, then the unit leaves the table.
        [Test]
        public void RemoveUnitVanishesTheShipAndItsDronesAndLeavesTheTable()
        {
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, DroneBayTests.Carrier(capacity: 3)),
                new UnitSetup(new UnitId(2), 0, Fixtures.Ship),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            var drone = DroneBayTests.Launch(driver);
            driver.Control(1, systems: 0);
            driver.Step();
            var ship = driver.Entity(1);
            var droneId = drone.Id;

            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.RemoveUnit(new UnitId(1)));
                Assert.IsTrue(context.RemoveUnit(new UnitId(1)), "A second call changes nothing.");
                Assert.IsTrue(context.RemoveUnit(new UnitId(2)), "A Ready unit has no entity.");
                Assert.IsFalse(context.RemoveUnit(new UnitId(7)));
            });
            var events = driver.StepCollecting();

            var removals = events.Where(e => e.Kind == BattleEventKind.Removal).ToArray();
            CollectionAssert.AreEqual(new[] { ship, droneId }, removals.Select(e => e.EntityId).ToArray());
            Assert.IsTrue(removals.All(e => e.RemovalReason == RemovalReason.Vanish));
            var transitions = events.Where(e => e.TransitionReason == UnitTransitionReason.Removed).ToArray();
            CollectionAssert.AreEqual(new[] { new UnitId(1), new UnitId(2) }, transitions.Select(e => e.UnitId).ToArray());
            Assert.AreEqual(ship, transitions[0].EntityId);
            Assert.AreEqual(UnitStatus.Destroyed, transitions[0].Status);
            Assert.Greater(transitions[0].Sequence, removals[1].Sequence, "The unit leaves after its drones.");
            Assert.AreEqual(UnitStatus.Active, rules.Mode.Changes[rules.Mode.Changes.Count - 2].Before);
            Assert.AreEqual(UnitStatus.Ready, rules.Mode.Changes.Last().Before);

            Assert.IsNull(driver.Battle.Units.Find(new UnitId(1)));
            CollectionAssert.AreEqual(new[] { new UnitId(101) }, driver.Battle.Snapshot.Units.ToArray().Select(u => u.UnitId));
            CollectionAssert.AreEqual(new[] { driver.Entity(101) }, driver.Battle.Snapshot.Ships.ToArray().Select(s => s.Id));

            rules.Mode.Next.Add(context =>
            {
                context.AddUnit(new UnitSetup(new UnitId(1), 0, Fixtures.Ship));
                Assert.IsTrue(context.Deploy(new UnitId(1), SpawnPlacement.Auto));
            });
            driver.Step();
            Assert.AreEqual(UnitStatus.Active, driver.Status(1), "A later tick adds the ID again.");
            Assert.AreNotEqual(ship, driver.Entity(1));
        }

        // Frontier's preparation clears the enemy's bullets (TODO/core_results.md, play check): a disposal without the
        // bullet's triggers.
        [Test]
        public void AModeVanishesABulletWithoutItsTriggers()
        {
            var rules = new ScriptRules();
            var bullet = new BulletDefinition(4f, 0f, 0f, 15f, lifetime: 12f);
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.CannonShip(bullet, cooldown: 20f, energyCost: 0f)),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            }, rules: rules));
            driver.Deploy(1);
            driver.Step();

            driver.Control(1, systems: 1);
            var id = default(EntityId);
            for (var i = 0; i < 10 && id.IsNull; ++i)
            {
                var spawns = CannonTests.BulletSpawns(driver.StepCollecting());
                if (spawns.Count > 0) id = spawns[0].EntityId;
            }
            Assert.IsFalse(id.IsNull, "The cannon did not fire.");
            driver.Control(1, systems: 0);

            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.VanishBullet(id));
                Assert.IsFalse(context.VanishBullet(new EntityId(99)));
            });
            var removal = CannonTests.BulletRemovals(driver.StepCollecting()).Single();
            Assert.AreEqual(id, removal.EntityId);
            Assert.AreEqual(RemovalReason.Disposal, removal.RemovalReason);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
        }

        // Common level mode, decision 2.
        [Test]
        public void AModeSpawnsAndVanishesAsteroidsWithNewIds()
        {
            var rules = new ScriptRules();
            var rock = new AsteroidSetup(new Vec2(300f, 300f), Vec2.Zero, 4f, 80f, 1600f, 1f, tag: 5);
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), rules: rules,
                asteroids: new[] { rock }));
            driver.Step();
            var first = driver.Battle.Snapshot.Asteroids[0].Id;

            var spawned = default(EntityId);
            rules.Mode.Next.Add(context =>
            {
                spawned = context.SpawnAsteroid(new AsteroidSetup(new Vec2(100f, 100f), new Vec2(1f, 0f), 2f, 20f, 400f, 1f));
                Assert.IsTrue(context.VanishAsteroid(first));
                Assert.IsFalse(context.VanishAsteroid(new EntityId(99)));
            });
            var events = driver.StepCollecting();
            Assert.AreNotEqual(default(EntityId), spawned);
            Assert.AreNotEqual(first, spawned);
            var removal = events.Single(e => e.Kind == BattleEventKind.AsteroidRemoval);
            Assert.AreEqual(first, removal.EntityId);
            Assert.AreEqual(RemovalReason.Vanish, removal.RemovalReason);
            Assert.AreEqual(5L, removal.Tag);
            Assert.AreEqual(spawned, driver.Battle.Snapshot.Asteroids.ToArray().Single().Id);
            Assert.AreEqual(2f, driver.Battle.Snapshot.Asteroids[0].Size);

            // The free entry is reused with a new generation, and the ID stays new.
            var third = default(EntityId);
            rules.Mode.Next.Add(context =>
                third = context.SpawnAsteroid(new AsteroidSetup(new Vec2(200f, 100f), Vec2.Zero, 3f, 45f, 900f, 1f)));
            driver.Step();
            Assert.AreEqual(2, driver.Battle.Asteroids.Items.Count);
            Assert.AreEqual(2, driver.Battle.Asteroids.AliveCount);
            Assert.AreNotEqual(first, third);
            Assert.AreNotEqual(spawned, third);
            Assert.AreEqual(third, driver.Battle.Asteroids.Items[0].Id);
            Assert.IsNull(driver.Battle.Asteroids.Find(first));

            var again = new Driver(new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), asteroids: new[] { rock, rock }));
            Assert.AreEqual(spawned, again.Battle.Snapshot.Asteroids[1].Id,
                "Asteroid number 1: the setup and the spawns share the numbering.");
        }

        [Test]
        public void NoDamageKeepsTheArmorFullUntilTheModeClearsIt()
        {
            // The bolt deals 5 a hit to a ship with 2 armor and no shield.
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.CannonShip(cooldown: 0.1f)),
                new UnitSetup(new UnitId(101), 1, Fixtures.ShipWith(armor: 2f, shield: new ResourceDefinition(0f))),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.SetNoDamage(new UnitId(101), true));
                Assert.IsFalse(context.SetNoDamage(new UnitId(7), true));
            });
            driver.Control(1, systems: 1);
            var events = new List<BattleEvent>();
            for (var i = 0; i < 30; ++i) events.AddRange(driver.StepCollecting());

            Assert.AreEqual(UnitStatus.Active, driver.Status(101));
            Assert.AreEqual(2f, driver.Ship(101).Armor.Value);
            Assert.IsTrue(Unit(driver.Battle, new UnitId(101)).NoDamage);
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Damage), "The hits still give damage events.");
            Assert.Greater(Unit(driver.Battle, new UnitId(101)).Performance.ArmorDamageReceived, 0f, "The hits still count.");

            rules.Mode.Next.Add(context => context.SetNoDamage(new UnitId(101), false));
            for (var i = 0; i < 30 && driver.Status(101) == UnitStatus.Active; ++i) driver.Step();
            Assert.AreEqual(UnitStatus.Destroyed, driver.Status(101));
        }

        [Test]
        public void StartEnergyAppliesToEachLaterDeployment()
        {
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.Ship),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.SetStartEnergy(new UnitId(1), 0f));
                Assert.IsFalse(context.SetStartEnergy(new UnitId(7), 0f));
                Assert.Throws<ArgumentOutOfRangeException>(() => context.SetStartEnergy(new UnitId(1), 1.5f));
                Assert.Throws<ArgumentOutOfRangeException>(() => context.SetStartEnergy(new UnitId(1), float.NaN));
            });
            driver.Step();
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            Assert.AreEqual(0f, driver.Ship(1).Energy.Value, "The spend starts the recharge delay.");
            Assert.AreEqual(Fixtures.Energy.Max, driver.Ship(101).Energy.Value);
            Assert.AreEqual(0f, Unit(driver.Battle, new UnitId(1)).StartEnergy);

            driver.Kill(1);
            driver.Step();
            rules.Mode.Next.Add(context => Assert.IsTrue(context.Respawn(new UnitId(1))));
            driver.Step();
            driver.Deploy(1);
            driver.Step();
            Assert.AreEqual(UnitStatus.Active, driver.Status(1));
            Assert.AreEqual(0f, driver.Ship(1).Energy.Value, "A respawn keeps the start energy.");
        }

        [Test]
        public void RespawnRefillsTheBayAndTheNewShipTakesTheLiveDrones()
        {
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, DroneBayTests.Carrier(capacity: 3)),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            var drone = DroneBayTests.Launch(driver);
            driver.Control(1, systems: 0);
            driver.Step();
            var bay = driver.Slot(1).GetBay(0);
            Assert.AreEqual(1, bay.ActiveCount);
            Assert.AreEqual(2, bay.InHangar);

            var first = driver.Entity(1);
            driver.Kill(1);
            driver.Step();
            Assert.AreEqual(UnitStatus.Destroyed, driver.Status(1));
            Assert.IsTrue(drone.IsAlive, "A mothership's death leaves its drones.");

            rules.Mode.Next.Add(context =>
            {
                Assert.IsFalse(context.Respawn(new UnitId(101)), "Active.");
                Assert.IsTrue(context.Respawn(new UnitId(1)));
            });
            driver.Step();
            Assert.AreEqual(UnitStatus.Ready, driver.Status(1));
            Assert.AreEqual(UnitTransitionReason.Respawned, rules.Mode.Changes.Last().Reason);
            Assert.AreEqual(2, bay.InHangar, "Capacity 3 less the live drone.");
            Assert.AreEqual(default(EntityId), drone.MothershipId, "No mothership before the deployment.");

            driver.Deploy(1);
            driver.Step();
            var second = driver.Entity(1);
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(second, drone.MothershipId);
            Assert.AreEqual(second, DroneBayTests.Drones(driver).Single().MothershipId);
        }

        [Test]
        public void ARespawnedUnitDeploysWhileADecoyLives()
        {
            var rules = new ScriptRules();
            var decoys = new DeviceDefinition[] { new DecoyDefinition(10f, 20f, 4, 4f, 100f, 10f) };
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.ShipWith(devices: decoys)),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: driver.Tick + 1);
            driver.Step();

            driver.Kill(1);
            driver.Step();
            Assert.AreEqual(UnitStatus.Destroyed, driver.Status(1));
            Assert.IsTrue(Enumerable.Range(0, driver.Battle.ShipEntryCount).Select(driver.Battle.GetShipEntry)
                .Any(s => s != null && s.IsDecoy), "A decoy outlives its ship.");

            rules.Mode.Next.Add(context => Assert.IsTrue(context.Respawn(new UnitId(1))));
            driver.Step();
            driver.Deploy(1);
            driver.Step();
            Assert.AreEqual(UnitStatus.Active, driver.Status(1), "A decoy has no bay, so the live-drone handover skips it.");
        }

        [Test]
        public void PerformanceCountsDamageKillsAndDeaths()
        {
            // The bolt deals 5: the target's shield takes it.
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0.1f));
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(30);
            var shooter = Unit(driver.Battle, new UnitId(1)).Performance;
            var target = Unit(driver.Battle, new UnitId(101)).Performance;
            Assert.Greater(shooter.ShieldDamageDealt, 0f);
            Assert.AreEqual(target.ShieldDamageReceived, shooter.ShieldDamageDealt, 1e-3f);
            Assert.AreEqual(0f, shooter.ArmorDamageDealt);
            Assert.AreEqual(Fixtures.Shield.Max - driver.Ship(101).Shield.Value, target.ShieldDamageReceived, 1e-3f);

            var fragile = Fixtures.ShipWith(armor: 2f, shield: new ResourceDefinition(0f));
            driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0.1f), fragile);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            for (var i = 0; i < 30 && !driver.Battle.IsCompleted; ++i) driver.Step();
            Assert.AreEqual(UnitStatus.Destroyed, driver.Status(101));
            shooter = Unit(driver.Battle, new UnitId(1)).Performance;
            target = Unit(driver.Battle, new UnitId(101)).Performance;
            Assert.AreEqual(1, shooter.Kills);
            Assert.AreEqual(1, target.Deaths);
            Assert.AreEqual(2f, shooter.ArmorDamageDealt, 1e-3f, "The armor the target had.");
            Assert.AreEqual(0, shooter.Deaths);
        }

        [Test]
        public void TheSetupRejectsAWorldAboveTheLimit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), BattleSetup.MaxWorldSize + 1f, 100f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), 100f, float.PositiveInfinity));
            Assert.DoesNotThrow(() =>
                new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 1), BattleSetup.MaxWorldSize, BattleSetup.MaxWorldSize));
        }

        [Test]
        public void TheRosterRulesNeedAUnitOnEachSide()
        {
            Assert.Throws<ArgumentException>(() =>
                new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 0), rules: RosterRules.Default));
            Assert.DoesNotThrow(() => new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 0)));
        }
    }
}

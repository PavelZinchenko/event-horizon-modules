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

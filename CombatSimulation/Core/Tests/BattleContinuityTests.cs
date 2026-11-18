using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Modes;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Battle continuity (D12, phase 2b): the battle after its result, the ceasefire and ChangeRules.
    public class BattleContinuityTests
    {
        private static BattleSetup Setup(IBattleRules rules, ShipDefinition side0 = null, bool continueAfterResult = true) =>
            new BattleSetup(new BattleId(1), 7, new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, side0 ?? Fixtures.Ship),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            }, rules: rules, continueAfterResult: continueAfterResult);

        private static Driver Start(IBattleRules rules, ShipDefinition side0 = null)
        {
            var driver = new Driver(Setup(rules, side0));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            return driver;
        }

        private static ulong _commandId = 1000;

        private static bool Ceasefire(Driver driver, bool enabled) =>
            driver.Battle.Submit(BattleCommand.Ceasefire(driver.BattleId, ++_commandId, driver.Tick, enabled));

        private static bool ChangeRules(Driver driver, IBattleRules rules) =>
            driver.Battle.Submit(BattleCommand.ChangeRules(driver.BattleId, ++_commandId, driver.Tick, rules));

        private static int ResultEvents(IEnumerable<BattleEvent> events) => events.Count(e => e.Kind == BattleEventKind.Result);

        [Test]
        public void TheBattleStepsAfterItsResultAndTheFirstResultStays()
        {
            var driver = Start(RosterRules.Default);
            driver.Kill(101);
            var events = driver.StepCollecting();
            var result = driver.Battle.Snapshot.Result;
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(1, ResultEvents(events));
            Assert.IsTrue(driver.Battle.IsCompleted);
            Assert.IsFalse(driver.Battle.IsStopped);

            var start = driver.Battle.Snapshot.Ships[0].Position;
            Assert.IsTrue(driver.Control(1, course: 0f, throttle: 1f));
            events = driver.StepCollecting(50);
            Assert.AreEqual(0, ResultEvents(events), "The result event comes once.");
            Assert.AreEqual(result.Tick, driver.Battle.Snapshot.Result.Tick);
            Assert.Greater((driver.Battle.Snapshot.Ships[0].Position - start).Length, 1f, "The ship still flies.");

            // Without the flag the battle stops at its result, as before.
            var stopped = new Driver(Setup(RosterRules.Default, continueAfterResult: false));
            stopped.Deploy(1);
            stopped.Deploy(101);
            stopped.Step();
            stopped.Kill(101);
            stopped.Step();
            Assert.IsTrue(stopped.Battle.IsStopped);
            Assert.Throws<InvalidOperationException>(() => stopped.Battle.Step());
            Assert.Throws<InvalidOperationException>(() => stopped.Control(1));
        }

        [Test]
        public void ALaterResultOfTheModeIsIgnored()
        {
            var rules = new ScriptRules();
            var driver = Start(rules);
            rules.Mode.Outcome = context => new BattleResult(BattleOutcome.Victory, 0, BattleEndReason.Mode, context.Tick, 1);
            driver.Step();
            var first = driver.Battle.Snapshot.Result;

            var ends = 0;
            rules.Mode.End = context => ++ends;
            rules.Mode.Outcome = context => new BattleResult(BattleOutcome.Victory, 1, BattleEndReason.Mode, context.Tick, 2);
            var events = driver.StepCollecting(5);
            Assert.AreEqual(5, ends, "The mode keeps running.");
            Assert.AreEqual(0, ResultEvents(events));
            Assert.AreEqual(first.Tick, driver.Battle.Snapshot.Result.Tick);
            Assert.AreEqual(1, driver.Battle.Snapshot.Result.ModeReason);
        }

        [Test]
        public void RemoteOwnersRejectContinuityAndBattleCommands()
        {
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.Ship, owner: 10),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship, owner: 20),
            };
            Assert.Throws<ArgumentException>(() =>
                new BattleSetup(new BattleId(1), 7, units, localOwners: new[] { 10 }, continueAfterResult: true));

            var battle = new Battle(new BattleSetup(new BattleId(1), 7, units, localOwners: new[] { 10 }));
            Assert.Throws<InvalidOperationException>(() =>
                battle.Submit(BattleCommand.Ceasefire(battle.BattleId, 1, 0, true)));
            Assert.Throws<InvalidOperationException>(() =>
                battle.Submit(BattleCommand.ChangeRules(battle.BattleId, 2, 0, null)));
        }

        [Test]
        public void CeasefireMasksTheControlBitsAndAHeldButtonFiresAgainAfterIt()
        {
            var driver = Start(new ScriptRules(), Fixtures.CannonShip(cooldown: 0.1f));
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Control(1, systems: 1);
            Assert.IsNotEmpty(CannonTests.BulletSpawns(driver.StepCollecting(10)));

            Assert.IsTrue(Ceasefire(driver, true));
            driver.Step();
            var ship = driver.Ship(1);
            Assert.IsTrue(driver.Battle.Snapshot.Ceasefire);
            Assert.AreEqual(0UL, ship.Systems);
            Assert.AreEqual(1UL, ship.RequestedSystems);
            Assert.AreEqual(1UL, ship.ReleasedSystems, "The devices see the release.");

            driver.Control(1, throttle: 1f, systems: 1);
            Assert.IsEmpty(CannonTests.BulletSpawns(driver.StepCollecting(20)));
            Assert.AreEqual(0UL, ship.Systems, "A new frame is masked too.");
            Assert.AreEqual(1f, ship.Throttle, "The engine controls stay.");

            Assert.IsTrue(Ceasefire(driver, false));
            driver.Step();
            Assert.IsFalse(driver.Battle.Snapshot.Ceasefire);
            Assert.AreEqual(1UL, ship.PressedSystems, "The held button presses again.");
            Assert.IsNotEmpty(CannonTests.BulletSpawns(driver.StepCollecting(10)));
        }

        [Test]
        public void CeasefireMasksADrone()
        {
            var rules = new ScriptRules();
            var driver = Start(rules, DroneBayTests.Carrier(capacity: 2));
            var drone = DroneBayTests.Launch(driver);
            driver.Control(drone.Id, systems: 1);
            driver.Step();
            Assert.AreEqual(1UL, drone.Systems);

            Ceasefire(driver, true);
            driver.Step();
            Assert.AreEqual(0UL, drone.Systems);
            Assert.AreEqual(0UL, driver.Ship(1).Systems);
            Assert.AreEqual(1, DroneBayTests.Drones(driver).Length, "The held launch button launches no drone.");
        }

        [Test]
        public void ChangeRulesGivesANewResultAndZeroPerformance()
        {
            var first = new ScriptRules();
            var driver = Start(first, Fixtures.CannonShip(cooldown: 0.1f));
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(30);
            Assert.Greater(driver.Slot(1).Performance.ShieldDamageDealt, 0f);

            first.Mode.Outcome = context => new BattleResult(BattleOutcome.Victory, 0, BattleEndReason.Mode, context.Tick, 1);
            Ceasefire(driver, true);
            // The bolts in flight expire: range 10 at 20 u/s.
            driver.Step(30);
            Assert.IsTrue(driver.Battle.IsCompleted);
            Assert.IsTrue(driver.Battle.Ceasefire);
            var entity = driver.Entity(1);
            var ends = 0;
            first.Mode.End = context => ++ends;

            var second = new ScriptRules();
            Assert.IsTrue(ChangeRules(driver, second));
            var events = driver.StepCollecting();
            Assert.AreSame(second, driver.Battle.Rules);
            Assert.AreSame(first, driver.Battle.Setup.Rules, "The setup keeps its rules.");
            Assert.AreEqual(0, ends, "The old mode no longer runs.");
            Assert.IsFalse(driver.Battle.IsCompleted);
            Assert.IsFalse(driver.Battle.Snapshot.IsCompleted);
            Assert.IsFalse(driver.Battle.Ceasefire);
            Assert.AreEqual(0, ResultEvents(events));
            Assert.AreEqual(default(UnitPerformance), driver.Slot(1).Performance);
            Assert.AreEqual(default(UnitPerformance), driver.Slot(101).Performance);
            Assert.AreEqual(2, driver.Battle.Snapshot.Units.Length);
            Assert.AreEqual(entity, driver.Entity(1), "The ships stay.");

            // About 3 u of flight at 20 u/s.
            driver.Step(15);
            Assert.Greater(driver.Slot(1).Performance.ShieldDamageDealt, 0f, "The held fire button fires again.");
            second.Mode.Outcome = context => new BattleResult(BattleOutcome.Victory, 1, BattleEndReason.Mode, context.Tick, 2);
            events = driver.StepCollecting();
            Assert.AreEqual(1, ResultEvents(events));
            Assert.AreEqual(2, driver.Battle.Snapshot.Result.ModeReason);
        }

        [Test]
        public void ChangeRulesValidatesTheRulesAtSubmission()
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, Fixtures.Units(1, 0), rules: new ScriptRules()));
            Assert.Throws<ArgumentException>(() => ChangeRules(driver, RosterRules.Default), "A unit on each side.");
            Assert.IsTrue(ChangeRules(driver, null), "No rules: an open battle.");
            driver.Step();
            Assert.IsNull(driver.Battle.Rules);
        }

        [Test]
        public void ARunnerKeepsThePilotsThinkingAfterTheResult()
        {
            var pilots = new CountingPilots();
            var runner = new BattleRunner(Setup(RosterRules.Default), pilots);
            runner.Deploy(new UnitId(1), 0);
            runner.Deploy(new UnitId(101), 0);
            runner.Step();
            runner.Surrender(1, runner.Tick);
            runner.Step();
            Assert.IsTrue(runner.IsCompleted);
            var thoughts = pilots.Thoughts;
            runner.Step();
            runner.Step();
            Assert.AreEqual(thoughts + 2, pilots.Thoughts, "The surviving ship's pilot thinks each tick.");
        }

        private sealed class CountingPilots : IPilotProvider, IPilot
        {
            public int Thoughts;

            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                return ship.Side == 0 ? this : null;
            }

            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                ++Thoughts;
                controls = PilotControls.Create(null, 0f, 0);
                return true;
            }
        }
    }
}

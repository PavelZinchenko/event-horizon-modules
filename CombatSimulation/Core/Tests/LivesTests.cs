using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Modes;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Lives with respawn (new combat simulator, phase 3, step 4; TODO/player_lives.md): a mode helper on the mode API, with
    // no core change.
    public class LivesTests
    {
        private const int Changed = 1;
        private const int Decision = 2;
        private const long Delay = 10;
        private const float Tolerance = 1e-3f;

        private static readonly UnitId First = new UnitId(1);
        private static readonly UnitId Second = new UnitId(2);
        private static readonly Vec2 Start = new Vec2(100f, 100f);
        private static readonly Vec2 OtherStart = new Vec2(300f, 300f);

        private Driver _driver;
        private Lives _lives;
        private ScriptMode _mode;

        // Two ships of side 0 in a 400 × 400 world. Tick 0 deploys them at their starts. The mode runs the helper in
        // each BeginTick and EndTick.
        private void Create(LivesSetup first, LivesSetup? second = null, bool secondFirst = false)
        {
            var rules = new ScriptRules();
            var units = new[] { new UnitSetup(First, 0, Fixtures.Ship), new UnitSetup(Second, 0, Fixtures.Ship) };
            _driver = new Driver(new BattleSetup(new BattleId(1), 7, units, 400f, 400f, rules));
            _mode = rules.Mode;
            _lives = new Lives(Changed, Decision);
            if (second.HasValue && secondFirst) _lives.Track(Second, second.Value);
            _lives.Track(First, first);
            if (second.HasValue && !secondFirst) _lives.Track(Second, second.Value);

            _mode.Next.Add(context =>
            {
                context.Deploy(First, SpawnPlacement.At(Start, 0f));
                context.Deploy(Second, SpawnPlacement.At(OtherStart, 0f));
            });
            _mode.Begin = context => _lives.BeginTick(context);
            _mode.End = context => _lives.EndTick(context);
            _driver.Step();
        }

        private static LivesSetup Setup(int lives, bool atLastPose = false, bool ask = false) =>
            new LivesSetup(lives, Delay, SpawnPlacement.At(Start, 0f), atLastPose, ask);

        private static IEnumerable<ModeEvent> ModeEvents(IEnumerable<BattleEvent> events, int code) =>
            events.Where(e => e.Kind == BattleEventKind.Mode && e.ModeEvent.Code == code).Select(e => e.ModeEvent);

        private static BattleEvent Transition(IEnumerable<BattleEvent> events, UnitId unit, UnitTransitionReason reason) =>
            events.Single(e => e.Kind == BattleEventKind.UnitTransition && e.UnitId == unit && e.TransitionReason == reason);

        private ShipState Ship(UnitId unit)
        {
            Assert.IsTrue(_driver.Battle.Snapshot.TryGetShip(_driver.Entity(unit.Value), out var state));
            return state;
        }

        [Test]
        public void TheSetupAndTheHelperValidateTheirInput()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LivesSetup(-1, 0, SpawnPlacement.Auto));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LivesSetup(0, -1, SpawnPlacement.Auto));
            Assert.Throws<ArgumentException>(() => new Lives(3, 3));

            var lives = new Lives(Changed, Decision);
            lives.Track(First, Setup(1));
            Assert.Throws<ArgumentException>(() => lives.Track(First, Setup(1)));
            Assert.Throws<ArgumentException>(() => lives.Status(Second));
            Assert.Throws<ArgumentOutOfRangeException>(() => lives.SetDelay(First, -1));
        }

        // A death spends the life at once (legacy --_lives); the respawn comes after the delay, at the placement. The next
        // death with no lives left and no question makes the unit Out after the delay.
        [Test]
        public void AUnitRespawnsAfterTheDelayAndIsOutWhenItsLivesAreSpent()
        {
            Create(Setup(1));
            Assert.AreEqual(new[] { (long)First.Value, 1L },
                ModeEvents(_driver.Battle.Snapshot.Events.ToArray(), Changed).Select(e => new[] { e.Value0, e.Value1 }).First());

            _driver.Place(1, new Vec2(150f, 120f), new Vec2(1f, 0f), 1f);
            _driver.Kill(1);
            var events = _driver.StepCollecting();
            var death = Transition(events, First, UnitTransitionReason.Killed).Tick;
            Assert.AreEqual(LifeStatus.Waiting, _lives.Status(First));
            Assert.AreEqual(0, _lives.LivesLeft(First));
            Assert.AreEqual(1, _lives.Deaths(First));
            Assert.AreEqual(0L, ModeEvents(events, Changed).Single().Value1);

            events = _driver.StepCollecting(Delay);
            Assert.AreEqual(death + Delay, Transition(events, First, UnitTransitionReason.Respawned).Tick);
            Assert.AreEqual(death + Delay, Transition(events, First, UnitTransitionReason.Deployed).Tick);
            Assert.AreEqual(LifeStatus.Alive, _lives.Status(First));
            Assert.AreEqual(Start.X, Ship(First).Position.X, Tolerance);
            Assert.AreEqual(Start.Y, Ship(First).Position.Y, Tolerance);

            _driver.Kill(1);
            events = _driver.StepCollecting(Delay + 1);
            Assert.IsEmpty(ModeEvents(events, Changed), "No life left to spend.");
            Assert.AreEqual(LifeStatus.Out, _lives.Status(First));
            Assert.AreEqual(UnitStatus.Destroyed, _driver.Status(1));
            Assert.AreEqual(2, _lives.Deaths(First));
        }

        // Exploration: near the dead ship's pose of the last snapshot, with zero velocity. A ship that dies in its spawn
        // tick is not in the snapshot, so the respawn uses the placement.
        [Test]
        public void AtLastPoseRespawnsWhereTheShipDiedOrAtThePlacement()
        {
            Create(Setup(3, atLastPose: true));
            _driver.Place(1, new Vec2(200f, 250f), new Vec2(2f, 0f), 1f);
            _driver.Step();
            var before = Ship(First);

            _driver.Kill(1);
            _driver.Step(Delay + 1);
            var after = Ship(First);
            Assert.AreEqual(before.Position.X, after.Position.X, Tolerance);
            Assert.AreEqual(before.Position.Y, after.Position.Y, Tolerance);
            Assert.AreEqual(before.Angle, after.Angle, Tolerance);
            Assert.AreEqual(0f, after.LinearVelocity.Length, Tolerance);

            var killOnRespawn = true;
            _mode.Begin = context =>
            {
                _lives.BeginTick(context);
                if (killOnRespawn && context.TryGetUnit(First, out var unit) && unit.Status == UnitStatus.Active &&
                    unit.DeploymentCount == 3)
                {
                    killOnRespawn = false;
                    _driver.Battle.QueueDeath(unit.ActiveEntity);
                }
            };
            _driver.Place(1, new Vec2(200f, 250f), Vec2.Zero, 1f);
            _driver.Kill(1);
            _driver.Step(2 * Delay + 2);
            Assert.IsFalse(killOnRespawn, "The third ship died in its spawn tick.");
            Assert.AreEqual(UnitStatus.Active, _driver.Status(1));
            Assert.AreEqual(Start.X, Ship(First).Position.X, Tolerance);
            Assert.AreEqual(Start.Y, Ship(First).Position.Y, Tolerance);
        }

        // The defeat window: one decision event; a bought life respawns in the tick of the answer; a refusal is Out.
        [Test]
        public void AnAskingUnitDecidesOnceAndRespawnsOnABoughtLife()
        {
            Create(Setup(0, ask: true));
            _driver.Kill(1);
            var events = _driver.StepCollecting(Delay + 5);
            var decision = ModeEvents(events, Decision).Single();
            Assert.AreEqual((long)First.Value, decision.Value0);
            Assert.AreEqual(1L, decision.Value1, "The deaths.");
            Assert.AreEqual(LifeStatus.Deciding, _lives.Status(First));

            _lives.AddLife(First);
            events = _driver.StepCollecting();
            Assert.AreEqual(_driver.Tick - 1, Transition(events, First, UnitTransitionReason.Respawned).Tick);
            Assert.AreEqual(LifeStatus.Alive, _lives.Status(First));
            Assert.AreEqual(0, _lives.LivesLeft(First));
            Assert.IsEmpty(ModeEvents(events, Changed), "The bought life is spent in the same call.");

            _driver.Kill(1);
            events = _driver.StepCollecting(Delay + 1);
            Assert.AreEqual(2L, ModeEvents(events, Decision).Single().Value1);
            _lives.Decline(First);
            Assert.AreEqual(LifeStatus.Out, _lives.Status(First));
            events = _driver.StepCollecting(5);
            Assert.IsEmpty(ModeEvents(events, Decision));
            Assert.AreEqual(UnitStatus.Destroyed, _driver.Status(1));
        }

        // Legacy checks the starbase before the player: one window at a time, in Track order.
        [Test]
        public void TwoDueUnitsDecideOneAtATimeInTrackOrder()
        {
            Create(Setup(0, ask: true), Setup(0, ask: true), secondFirst: true);
            _driver.Kill(1);
            _driver.Kill(2);
            var events = _driver.StepCollecting(Delay + 1);
            Assert.AreEqual((long)Second.Value, ModeEvents(events, Decision).Single().Value0);
            Assert.AreEqual(LifeStatus.Deciding, _lives.Status(Second));
            Assert.AreEqual(LifeStatus.Waiting, _lives.Status(First));

            _lives.Decline(Second);
            events = _driver.StepCollecting();
            Assert.AreEqual((long)First.Value, ModeEvents(events, Decision).Single().Value0);
            Assert.AreEqual(LifeStatus.Deciding, _lives.Status(First));
        }

        // A vanish is a retreat, not a death. A forced destruction (the mode's DestroyUnit) stops the tracking.
        [Test]
        public void AVanishIsNoDeathAndAForcedDestructionStopsTheTracking()
        {
            Create(Setup(1));
            var ship = _driver.Entity(1);
            _mode.Next.Add(context => context.Vanish(ship));
            var events = _driver.StepCollecting();
            Transition(events, First, UnitTransitionReason.Retreated);
            Assert.AreEqual(LifeStatus.Alive, _lives.Status(First));
            Assert.AreEqual(0, _lives.Deaths(First));

            _mode.Next.Add(context => context.DestroyUnit(First));
            events = _driver.StepCollecting(Delay + 1);
            Transition(events, First, UnitTransitionReason.ForcedDestruction);
            Assert.IsFalse(_lives.IsTracked(First));
            Assert.AreEqual(UnitStatus.Destroyed, _driver.Status(1));
        }

        [Test]
        public void TwoRunsGiveTheSameEvents()
        {
            List<string> Run()
            {
                Create(Setup(1, atLastPose: true, ask: true), Setup(2));
                var log = new List<string>();
                for (var tick = 0; tick < 80; ++tick)
                {
                    if (tick % 15 == 3 && _driver.Status(1) == UnitStatus.Active) _driver.Kill(1);
                    if (tick % 20 == 7 && _driver.Status(2) == UnitStatus.Active) _driver.Kill(2);
                    if (tick == 50 && _lives.Status(First) == LifeStatus.Deciding) _lives.AddLife(First);
                    log.AddRange(_driver.StepCollecting().Where(e => e.Kind != BattleEventKind.Damage).Select(e => e.ToString()));
                }
                return log;
            }

            var first = Run();
            Assert.That(first, Has.Some.Contains("Mode 2"), "The run reaches a decision.");
            CollectionAssert.AreEqual(first, Run());
        }
    }
}

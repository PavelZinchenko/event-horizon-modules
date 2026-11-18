using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CombatSimulation.Modes;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // The shared exploration mode (new combat simulator, phase 3, step 5; TODO/exploration_mode.md): the layout generator
    // and the runtime, on the mode API with no core change.
    public class ExplorationModeTests
    {
        private const float World = 1000f;
        private const uint Base = 1000;
        private const long Delay = 10;
        private const float Tolerance = 1e-3f;

        private static readonly UnitId Player = new UnitId(1);
        private static readonly ExplorationObjectiveSetup Crate = new ExplorationObjectiveSetup(ExplorationObjectiveKind.Dockable, 5f, 11);
        private static readonly ExplorationObjectiveSetup Merchant = new ExplorationObjectiveSetup(ExplorationObjectiveKind.DockableShip, 4f, 12);

        private Driver _driver;
        private ExplorationLayout _layout;
        private ExplorationMode _mode;
        private ulong _commandId = 1000;

        // Builds a fresh definition on each call, as a game provider without a cache.
        private sealed class Content : IExplorationContent
        {
            public readonly List<(ExplorationShipRole Role, long Key)> Calls = new List<(ExplorationShipRole, long)>();

            public ShipDefinition CreateShip(ExplorationShipRole role, long contentKey)
            {
                Calls.Add((role, contentKey));
                return Fixtures.ShipWith();
            }
        }

        private static ExplorationLayoutInput Input(IReadOnlyList<ExplorationObjectiveSetup> objectives, int randomShips = 0,
            IReadOnlyList<ExplorationGuardianGroup> groups = null, ExplorationZones zones = default,
            ExplorationAsteroids asteroids = default, ulong seed = 42) =>
            new ExplorationLayoutInput(seed, World, Base, objectives, groups,
                Enumerable.Range(0, randomShips).Select(i => 100L + i).ToArray(), zones, asteroids);

        private static ExplorationObjectiveSetup Hostile(int group = -1) =>
            new ExplorationObjectiveSetup(ExplorationObjectiveKind.Hostile, 8f, 13, group);

        private static LivesSetup NoLives(Vec2 start) => new LivesSetup(0, Delay, SpawnPlacement.At(start, 0f));

        private void Create(ExplorationLayoutInput input, Func<ExplorationLayout, LivesSetup> lives = null,
            IReadOnlyCollection<int> completed = null, IExplorationContent content = null)
        {
            _layout = ExplorationLayout.Generate(input);
            var rules = new ExplorationRules(_layout, content ?? new Content(), Player,
                lives?.Invoke(_layout) ?? NoLives(_layout.Start), completed);
            var setup = new BattleSetup(new BattleId(1), 7,
                new[] { new UnitSetup(Player, 0, Fixtures.Ship, allegiance: Allegiance.Player) }, World, World, rules,
                asteroids: _layout.Asteroids, zones: _layout.Zones, continueAfterResult: true);
            _driver = new Driver(setup);
            _mode = (ExplorationMode)_driver.Battle.Mode;
            _driver.Step();
        }

        // The mode reads the last completed tick, so a placement shows after two steps.
        private List<BattleEvent> MovePlayer(Vec2 position, long steps = 2)
        {
            if (_driver.Ship(Player.Value) != null)
                _driver.Place(Player.Value, _layout.Torus.Wrap(position), Vec2.Zero);
            return _driver.StepCollecting(steps);
        }

        private List<BattleEvent> Command(int kind, long argument = 0, long steps = 1)
        {
            Assert.IsTrue(_driver.Battle.Submit(new ModeCommand(_driver.BattleId, _commandId++, _driver.Tick, kind, argument)));
            return _driver.StepCollecting(steps);
        }

        private static ModeEvent[] ModeEvents(IEnumerable<BattleEvent> events, int code) =>
            events.Where(e => e.Kind == BattleEventKind.Mode && e.ModeEvent.Code == code).Select(e => e.ModeEvent).ToArray();

        private uint Unit(int spawner) => _layout.Spawners[spawner].UnitId.Value;

        private static void AssertClose(Vec2 expected, Vec2 actual)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance);
            Assert.AreEqual(expected.Y, actual.Y, Tolerance);
        }

        [Test]
        public void TheSameInputGivesTheSameLayout()
        {
            var groups = new[] { new ExplorationGuardianGroup(1, 4, 10f, 0, 21) };
            var input = Input(new[] { Crate, Hostile(0), Merchant }, 30, groups, new ExplorationZones(3, 10f, 20f, 0, 5f),
                ExplorationAsteroids.Legacy(20));
            var first = ExplorationLayout.Generate(input);
            var second = ExplorationLayout.Generate(input);

            Assert.AreEqual(first.StartAngle, second.StartAngle);
            CollectionAssert.AreEqual(first.Objectives.Select(o => o.Position), second.Objectives.Select(o => o.Position));
            CollectionAssert.AreEqual(first.Spawners.Select(s => s.Position), second.Spawners.Select(s => s.Position));
            CollectionAssert.AreEqual(first.Zones.Select(z => z.Position), second.Zones.Select(z => z.Position));
            CollectionAssert.AreEqual(first.Asteroids.Select(a => a.Velocity), second.Asteroids.Select(a => a.Velocity));

            var other = ExplorationLayout.Generate(Input(new[] { Crate, Hostile(0), Merchant }, 30, groups, seed: 43));
            CollectionAssert.AreNotEqual(first.Spawners.Select(s => s.Position), other.Spawners.Select(s => s.Position));
        }

        // Each objective is on its own grid point within the jitter, and never on the player's.
        [Test]
        public void ObjectivesTakeUniqueGridPointsAndLeaveThePlayersPointFree()
        {
            var objectives = Enumerable.Repeat(Crate, ExplorationLayout.MaxObjectives).ToArray();
            var layout = ExplorationLayout.Generate(Input(objectives));
            var cell = World / ExplorationLayout.GridSize;
            var points = new HashSet<int>();
            foreach (var objective in layout.Objectives)
            {
                var x = (int)Math.Round(objective.Position.X / cell) % ExplorationLayout.GridSize;
                var y = (int)Math.Round(objective.Position.Y / cell) % ExplorationLayout.GridSize;
                Assert.IsTrue(points.Add(y * ExplorationLayout.GridSize + x), $"Objective {objective.Id} shares a point.");
                var offset = layout.Torus.Delta(new Vec2(x * cell, y * cell), objective.Position);
                Assert.LessOrEqual(Math.Abs(offset.X), ExplorationLayout.Jitter * cell + Tolerance);
                Assert.LessOrEqual(Math.Abs(offset.Y), ExplorationLayout.Jitter * cell + Tolerance);
            }

            Assert.IsFalse(points.Contains(55), "The player's grid point.");
            Assert.Throws<ArgumentException>(() => Input(Enumerable.Repeat(Crate, 100).ToArray()));
        }

        // Separate streams: zones and asteroids do not move the rest, and a new objective at the end keeps the others.
        [Test]
        public void APartChangesWithoutMovingTheOthers()
        {
            var objectives = new[] { Crate, Hostile(), Merchant };
            var plain = ExplorationLayout.Generate(Input(objectives, 20));
            var rich = ExplorationLayout.Generate(Input(objectives, 20, zones: new ExplorationZones(5, 10f, 20f, 0, 5f),
                asteroids: ExplorationAsteroids.Legacy(20)));
            CollectionAssert.AreEqual(plain.Objectives.Select(o => o.Position), rich.Objectives.Select(o => o.Position));
            CollectionAssert.AreEqual(plain.Spawners.Select(s => s.Position), rich.Spawners.Select(s => s.Position));
            Assert.AreEqual(5, rich.Zones.Count);
            Assert.AreEqual(20, rich.Asteroids.Count);
            Assert.AreEqual(ExplorationLayout.ZoneTagBase + 4, rich.Zones[4].Tag);
            Assert.AreEqual(ExplorationLayout.AsteroidTagBase + 19, rich.Asteroids[19].Tag);

            var more = ExplorationLayout.Generate(Input(objectives.Append(Crate).ToArray(), 20));
            CollectionAssert.AreEqual(plain.Objectives.Select(o => o.Position), more.Objectives.Take(3).Select(o => o.Position));
            CollectionAssert.AreEqual(plain.Spawners.Where(s => s.Role == ExplorationShipRole.RandomShip).Select(s => s.Position),
                more.Spawners.Where(s => s.Role == ExplorationShipRole.RandomShip).Select(s => s.Position));
        }

        // The order: hostile objectives, guardians, dockable ships, random ships. Guardians ring their objective.
        [Test]
        public void SpawnersComeInUnitIdOrderAndGuardiansRingTheirObjective()
        {
            var groups = new[] { new ExplorationGuardianGroup(3, 3, 10f, 1500, 21) };
            var layout = ExplorationLayout.Generate(Input(new[] { Merchant, Hostile(0), Crate }, 50, groups,
                zones: new ExplorationZones(10, 10f, 20f, 0, 5f), asteroids: ExplorationAsteroids.Legacy(20)));

            CollectionAssert.AreEqual(
                new[] { ExplorationShipRole.Hostile }.Concat(Enumerable.Repeat(ExplorationShipRole.Guardian, 3))
                    .Append(ExplorationShipRole.DockableShip).Concat(Enumerable.Repeat(ExplorationShipRole.RandomShip, 50)),
                layout.Spawners.Select(s => s.Role));
            for (var i = 0; i < layout.Spawners.Count; ++i)
            {
                Assert.AreEqual(Base + i, layout.Spawners[i].UnitId.Value);
                Assert.IsTrue(layout.TryFindSpawner(layout.Spawners[i].UnitId, out var found));
                Assert.AreEqual(i, found.Index);
            }
            Assert.IsFalse(layout.TryFindSpawner(new UnitId(Base - 1), out _));
            Assert.IsFalse(layout.TryFindSpawner(new UnitId(Base + (uint)layout.Spawners.Count), out _));

            var hostile = layout.Objectives[1];
            Assert.AreEqual(0, hostile.Spawner);
            Assert.AreEqual(4, layout.Objectives[0].Spawner);
            Assert.AreEqual(-1, layout.Objectives[2].Spawner);
            foreach (var guardian in layout.Spawners.Where(s => s.Role == ExplorationShipRole.Guardian))
            {
                Assert.AreEqual(0, guardian.Parent);
                Assert.AreEqual(1500L, guardian.RespawnDelayTicks);
                Assert.AreEqual(21L, guardian.ContentKey);
                Assert.AreEqual(10f, layout.Torus.Delta(hostile.Position, guardian.Position).Length, Tolerance);
            }

            foreach (var position in layout.Spawners.Where(s => s.Role == ExplorationShipRole.RandomShip).Select(s => s.Position)
                         .Concat(layout.Zones.Select(z => z.Position)).Concat(layout.Asteroids.Select(a => a.Position)))
                Assert.GreaterOrEqual(layout.Torus.Delta(layout.Start, position).Length, 75f - Tolerance);
        }

        [Test]
        public void TheRulesValidateTheSetup()
        {
            var layout = ExplorationLayout.Generate(Input(new[] { Crate }, 2));
            var rules = new ExplorationRules(layout, new Content(), Player, NoLives(layout.Start));
            BattleSetup Setup(float size, params UnitSetup[] units) => new BattleSetup(new BattleId(1), 7, units, size, size, rules);
            var player = new UnitSetup(Player, 0, Fixtures.Ship);

            Assert.DoesNotThrow(() => Setup(World, player));
            Assert.Throws<ArgumentException>(() => Setup(800f, player));
            Assert.Throws<ArgumentException>(() => Setup(World));
            Assert.Throws<ArgumentException>(() => Setup(World, new UnitSetup(Player, 1, Fixtures.Ship)));
            Assert.Throws<ArgumentException>(() => Setup(World, player, new UnitSetup(new UnitId(Base + 1), 1, Fixtures.Ship)));
            Assert.DoesNotThrow(() => Setup(World, player, new UnitSetup(new UnitId(Base + 2), 1, Fixtures.Ship)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ExplorationRules(layout, new Content(), Player, NoLives(layout.Start), vanishDistance: 75f));
        }

        // Tick 0: the player at the start and the dockable ship at its objective. Nothing else has a unit.
        [Test]
        public void TheStartDeploysThePlayerAndTheDockableShips()
        {
            var content = new Content();
            Create(Input(new[] { Crate, Merchant }, 5), content: content);
            Assert.AreEqual(UnitStatus.Active, _driver.Status(Player.Value));
            AssertClose(_layout.Start, _driver.Battle.World.GetPosition(_driver.Ship(Player.Value).Body));

            var merchant = _layout.Objectives[1];
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(merchant.Spawner));
            var unit = _driver.Slot(Unit(merchant.Spawner));
            Assert.AreEqual(0, unit.Side);
            AssertClose(merchant.Position, _driver.Battle.World.GetPosition(unit.ActiveShip.Body));
            Assert.AreEqual(new[] { (ExplorationShipRole.DockableShip, 12L) }, content.Calls.ToArray());
            Assert.AreEqual(2, _driver.Battle.Units.All.Count);
        }

        // Activates within 75 of the spawner, vanishes beyond 100 from the ship, comes back at the spawner position with
        // its damage, and stays dead after a kill.
        [Test]
        public void ARandomShipActivatesVanishesReturnsAndStaysDead()
        {
            Create(Input(new[] { Crate }, 1));
            var spawner = _layout.Spawners[0];
            var id = spawner.UnitId.Value;

            MovePlayer(spawner.Position + new Vec2(76f, 0f));
            Assert.AreEqual(ExplorationSpawnerState.Dormant, _mode.State(0));
            MovePlayer(spawner.Position + new Vec2(74f, 0f));
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(0));
            var ship = _driver.Ship(id);
            AssertClose(_layout.Torus.Wrap(spawner.Position), _driver.Battle.World.GetPosition(ship.Body));
            Assert.AreEqual(0f, _driver.Battle.World.GetAngle(ship.Body), Tolerance, "Facing the player.");
            Assert.AreEqual(1, _driver.Slot(id).Side);

            ship.Armor.Consume(100f);
            _driver.Place(id, _layout.Torus.Wrap(spawner.Position + new Vec2(30f, 0f)), Vec2.Zero);
            MovePlayer(spawner.Position + new Vec2(129f, 0f));
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(0), "99 from the ship.");
            MovePlayer(spawner.Position + new Vec2(-80f, 0f));
            Assert.AreEqual(ExplorationSpawnerState.Away, _mode.State(0), "110 from the ship.");
            Assert.AreEqual(UnitStatus.Ready, _driver.Status(id));
            Assert.AreEqual(100f, _driver.Slot(id).ArmorDeficit, Tolerance);

            var rejected = _driver.Battle.RejectedCommandCount;
            _driver.Deploy(id);
            _driver.Step();
            Assert.AreEqual(rejected + 1, _driver.Battle.RejectedCommandCount, "The mode deploys every unit.");
            Assert.AreEqual(UnitStatus.Ready, _driver.Status(id));

            MovePlayer(spawner.Position + new Vec2(-70f, 0f));
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(0));
            ship = _driver.Ship(id);
            AssertClose(_layout.Torus.Wrap(spawner.Position), _driver.Battle.World.GetPosition(ship.Body));
            Assert.AreEqual(Math.PI, Math.Abs(_driver.Battle.World.GetAngle(ship.Body)), Tolerance);
            Assert.AreEqual(100f, ship.Armor.Deficit, Tolerance);

            _driver.Kill(id);
            _driver.Step();
            Assert.AreEqual(ExplorationSpawnerState.Dead, _mode.State(0));
            MovePlayer(spawner.Position + new Vec2(300f, 0f));
            MovePlayer(spawner.Position + new Vec2(10f, 0f), 30);
            Assert.AreEqual(ExplorationSpawnerState.Dead, _mode.State(0));
            Assert.AreEqual(UnitStatus.Destroyed, _driver.Status(id));
        }

        // While the player has no ship, no spawner activates or vanishes.
        [Test]
        public void NothingChangesWhileThePlayerIsDead()
        {
            Create(Input(new[] { Crate }, 1), layout => new LivesSetup(1, 100, SpawnPlacement.At(layout.Start, 0f)));
            var spawner = _layout.Spawners[0];
            MovePlayer(spawner.Position + new Vec2(50f, 0f));
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(0));
            _driver.Kill(Player.Value);
            _driver.Place(spawner.UnitId.Value, _layout.Torus.Wrap(spawner.Position + new Vec2(300f, 0f)), Vec2.Zero);
            _driver.Step(50);
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(0));
        }

        // A guardian respawns after its delay while its objective lives. The objective's death completes it and stops the
        // respawns; a live guardian keeps fighting.
        [Test]
        public void GuardiansRespawnUntilTheirObjectiveDies()
        {
            var groups = new[] { new ExplorationGuardianGroup(2, 2, 10f, 20, 21) };
            Create(Input(new[] { Hostile(0), Crate }, groups: groups));
            var hostile = _layout.Objectives[0];
            MovePlayer(hostile.Position + new Vec2(40f, 0f));
            for (var i = 0; i < 3; ++i) Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(i));

            _driver.Kill(Unit(1));
            _driver.Step(20);
            Assert.AreEqual(ExplorationSpawnerState.Dead, _mode.State(1));
            var events = _driver.StepCollecting(2);
            Assert.IsTrue(events.Any(e => e.UnitId.Value == Unit(1) && e.TransitionReason == UnitTransitionReason.Respawned));
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(1));

            _driver.Kill(Unit(0));
            events = _driver.StepCollecting();
            var completed = ModeEvents(events, ExplorationMode.ObjectiveCompleted).Single();
            Assert.AreEqual(0L, completed.Value0);
            Assert.AreEqual((long)ExplorationCompletion.Destroyed, completed.Value1);
            Assert.IsTrue(_mode.IsComplete(0));

            _driver.Kill(Unit(2));
            _driver.Step(40);
            Assert.AreEqual(ExplorationSpawnerState.Dead, _mode.State(2));
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(1));
        }

        // Dock, then the scan window's command: the area goes (undock) and the objective completes. The last objective
        // gives the victory, and the battle steps after it.
        [Test]
        public void DockingAndTheCommandCompleteADockableObjective()
        {
            Create(Input(new[] { Crate }));
            var crate = _layout.Objectives[0];
            var events = MovePlayer(crate.Position + new Vec2(5.5f, 0f));
            var docked = ModeEvents(events, ExplorationMode.Docked).Single();
            Assert.AreEqual(0L, docked.Value0);
            Assert.AreEqual((long)_driver.Entity(Player.Value).Value, docked.Value1);

            Assert.IsEmpty(ModeEvents(Command(ExplorationMode.CompleteObjective, 7), ExplorationMode.ObjectiveCompleted),
                "An unknown ID.");
            events = Command(ExplorationMode.CompleteObjective, 0);
            Assert.AreEqual(0L, ModeEvents(events, ExplorationMode.Undocked).Single().Value0);
            var completed = ModeEvents(events, ExplorationMode.ObjectiveCompleted).Single();
            Assert.AreEqual((long)ExplorationCompletion.Command, completed.Value1);
            var result = _driver.Battle.Snapshot.Result;
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(0, result.WinningSide);
            Assert.AreEqual(ExplorationMode.AllComplete, result.ModeReason);

            Assert.IsEmpty(ModeEvents(Command(ExplorationMode.CompleteObjective, 0), ExplorationMode.ObjectiveCompleted));
            _driver.Step(10);
            Assert.AreEqual(result.Tick, _driver.Battle.Snapshot.Result.Tick);
        }

        // The merchant's area follows the ship; its death completes it.
        [Test]
        public void ADockableShipDocksWhereItIsAndCompletesWhenItDies()
        {
            Create(Input(new[] { Merchant, Crate }));
            var merchant = _layout.Objectives[0];
            var id = Unit(merchant.Spawner);
            var moved = merchant.Position + new Vec2(0f, 60f);
            _driver.Place(id, _layout.Torus.Wrap(moved), Vec2.Zero);
            Assert.IsEmpty(ModeEvents(MovePlayer(merchant.Position + new Vec2(3f, 0f)), ExplorationMode.Docked));
            Assert.AreEqual(0L, ModeEvents(MovePlayer(moved + new Vec2(4.5f, 0f), 3), ExplorationMode.Docked).Single().Value0);

            _driver.Kill(id);
            var events = _driver.StepCollecting();
            Assert.AreEqual(0L, ModeEvents(events, ExplorationMode.Undocked).Single().Value0);
            Assert.AreEqual((long)ExplorationCompletion.Destroyed,
                ModeEvents(events, ExplorationMode.ObjectiveCompleted).Single().Value1);
            Assert.AreEqual(1, _mode.RemainingObjectives);
        }

        // Completed IDs: no area, no ship, no guardians; unknown IDs are ignored. All complete gives the victory at once.
        [Test]
        public void CompletedObjectivesAreSkipped()
        {
            var groups = new[] { new ExplorationGuardianGroup(2, 2, 10f, 20, 21) };
            var content = new Content();
            Create(Input(new[] { Hostile(0), Merchant, Crate }, groups: groups), completed: new[] { 0, 1, 99, -1 },
                content: content);
            Assert.AreEqual(1, _mode.RemainingObjectives);
            Assert.IsEmpty(content.Calls);
            for (var i = 0; i < 4; ++i) Assert.AreEqual(ExplorationSpawnerState.Dead, _mode.State(i));
            Assert.IsEmpty(ModeEvents(MovePlayer(_layout.Objectives[1].Position), ExplorationMode.Docked));
            MovePlayer(_layout.Objectives[0].Position + new Vec2(30f, 0f), 30);
            Assert.AreEqual(1, _driver.Battle.Units.All.Count);

            Create(Input(new[] { Crate }), completed: new[] { 0 });
            Assert.AreEqual(ExplorationMode.AllComplete, _driver.Battle.Snapshot.Result.ModeReason);
            Assert.AreEqual(0L, _driver.Battle.Snapshot.Result.Tick);
        }

        [Test]
        public void ExitIsADefeat()
        {
            Create(Input(new[] { Crate }));
            Command(ExplorationMode.Exit);
            var result = _driver.Battle.Snapshot.Result;
            Assert.AreEqual(1, result.WinningSide);
            Assert.AreEqual(ExplorationMode.PlayerExit, result.ModeReason);
            _driver.Step(5);
        }

        // Frontier: the defeat window, a bought life respawns at the last pose; a refusal ends the battle.
        [Test]
        public void ThePlayerBuysALifeThenDeclinesAndIsOut()
        {
            Create(Input(new[] { Crate }),
                layout => new LivesSetup(0, Delay, SpawnPlacement.At(layout.Start, 0f), atLastPose: true, askForMore: true));
            var place = _layout.Start + new Vec2(30f, 20f);
            MovePlayer(place);
            _driver.Kill(Player.Value);
            var events = _driver.StepCollecting(Delay + 1);
            Assert.AreEqual((long)Player.Value, ModeEvents(events, ExplorationMode.LivesDecision).Single().Value0);
            Assert.AreEqual(LifeStatus.Deciding, _mode.PlayerStatus);

            Command(ExplorationMode.BuyLife);
            Assert.AreEqual(UnitStatus.Active, _driver.Status(Player.Value));
            AssertClose(place, _driver.Battle.World.GetPosition(_driver.Ship(Player.Value).Body));

            _driver.Kill(Player.Value);
            _driver.Step(Delay + 1);
            Assert.IsFalse(_driver.Battle.Snapshot.Result.IsCompleted);
            Command(ExplorationMode.Decline);
            var result = _driver.Battle.Snapshot.Result;
            Assert.AreEqual(1, result.WinningSide);
            Assert.AreEqual(ExplorationMode.PlayerOut, result.ModeReason);
            _driver.Step(5);
        }

        // Main: no lives, no question; out after the delay.
        [Test]
        public void WithoutLivesThePlayerIsOutAfterTheDelay()
        {
            Create(Input(new[] { Crate }));
            _driver.Kill(Player.Value);
            _driver.Step(Delay);
            Assert.IsFalse(_driver.Battle.Snapshot.Result.IsCompleted);
            _driver.Step();
            Assert.AreEqual(ExplorationMode.PlayerOut, _driver.Battle.Snapshot.Result.ModeReason);
        }

        // The cheat destroys the active hostile units only; a hostile objective completes.
        [Test]
        public void KillAllDestroysTheActiveHostileUnits()
        {
            Create(Input(new[] { Hostile(), Merchant, Crate }, 1));
            var hostile = _layout.Objectives[0];
            MovePlayer(hostile.Position + new Vec2(30f, 0f));
            var random = _layout.Spawners.Single(s => s.Role == ExplorationShipRole.RandomShip).Index;
            var randomWasActive = _mode.State(random) == ExplorationSpawnerState.Active;
            var events = Command(ExplorationMode.KillAll);
            Assert.AreEqual(ExplorationSpawnerState.Dead, _mode.State(hostile.Spawner));
            Assert.AreEqual((long)ExplorationCompletion.Destroyed,
                ModeEvents(events, ExplorationMode.ObjectiveCompleted).Single().Value1);
            Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(_layout.Objectives[1].Spawner));
            Assert.AreEqual(UnitStatus.Active, _driver.Status(Player.Value));
            Assert.AreEqual(randomWasActive, _mode.State(random) == ExplorationSpawnerState.Dead);
        }

        // Two runs with the same commands give the same events.
        [Test]
        public void TwoRunsGiveTheSameEvents()
        {
            string Run()
            {
                Create(Input(new[] { Crate, Hostile(0), Merchant }, 40,
                    new[] { new ExplorationGuardianGroup(1, 4, 10f, 30, 21) }));
                var log = new List<string>();
                var position = _layout.Start;
                for (var step = 0; step < 40; ++step)
                {
                    position += new Vec2(25f, 15f);
                    foreach (var item in MovePlayer(position, 5))
                        log.Add($"{item.Tick} {item.Kind} {item.UnitId} {item.TransitionReason} {item.ModeEvent.Code} {item.ModeEvent.Value0}");
                    if (step == 20) _driver.Kill(Player.Value);
                }
                return string.Join("\n", log);
            }

            var first = Run();
            StringAssert.Contains("Deployed", first);
            Assert.AreEqual(first, Run());
        }

        // Frontier's highest levels: about 620 random spawners, about 10 active. The check of every spawner each tick and
        // the snapshot copy; then the worst tick: a hostile objective and four guardians activate at once.
        [Test]
        public void ManySpawnersStayCheap()
        {
            var groups = new[] { new ExplorationGuardianGroup(4, 4, 10f, 0, 21) };
            Create(Input(new[] { Hostile(0), Crate }, 620, groups));

            for (var i = 0; i < 5; ++i) Assert.AreEqual(ExplorationSpawnerState.Dormant, _mode.State(i));
            _driver.Place(Player.Value, _layout.Torus.Wrap(_layout.Objectives[0].Position + new Vec2(40f, 0f)), Vec2.Zero);
            _driver.Step();
            var burst = Stopwatch.StartNew();
            _driver.Step();
            burst.Stop();
            for (var i = 0; i < 5; ++i) Assert.AreEqual(ExplorationSpawnerState.Active, _mode.State(i));

            BattleSnapshot copy = null;
            var tick = new Stopwatch();
            var snapshot = new Stopwatch();
            var worst = 0.0;
            var position = _layout.Start;
            var activeSum = 0;
            const int steps = 500;
            for (var i = 0; i < steps; ++i)
            {
                // About 2 u per tick through the world, so ships activate and vanish.
                if (i % 10 == 0)
                {
                    position += new Vec2(17f, 9f);
                    _driver.Place(Player.Value, _layout.Torus.Wrap(position), Vec2.Zero);
                }
                var before = tick.Elapsed.TotalMilliseconds;
                tick.Start();
                _driver.Step();
                tick.Stop();
                worst = Math.Max(worst, tick.Elapsed.TotalMilliseconds - before);
                snapshot.Start();
                copy = _driver.Battle.Snapshot.CopyTo(copy);
                snapshot.Stop();
                activeSum += Enumerable.Range(0, _layout.Spawners.Count).Count(k => _mode.State(k) == ExplorationSpawnerState.Active);
            }
            var units = _driver.Battle.Units.All.Count;

            TestContext.WriteLine($"{_layout.Spawners.Count} spawners, {units} units, {activeSum / (float)steps:F1} active on average. " +
                                  $"Tick: mean {tick.Elapsed.TotalMilliseconds / steps:F3} ms, worst {worst:F3} ms; " +
                                  $"snapshot copy mean {snapshot.Elapsed.TotalMilliseconds / steps:F3} ms; " +
                                  $"five activations {burst.Elapsed.TotalMilliseconds:F3} ms.");
            Assert.Greater(units, 20, "The walk visited spawners.");
            Assert.Less(activeSum / steps, 40);
        }
    }
}

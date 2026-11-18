using System;
using CombatSimulation.Core;
using CombatSimulation.Physics;

namespace CombatSimulation.Modes
{
    public enum ExplorationSpawnerState : byte
    {
        // No unit yet.
        Dormant,

        // The ship is in the battle.
        Active,

        // The unit is Ready after a vanish, with its armor and shield damage.
        Away,

        // The unit is Destroyed, or the spawner's objective (or its guardians' objective) was complete before the visit.
        Dead,
    }

    public enum ExplorationCompletion : byte
    {
        // CompleteObjective: the scan window of a dockable object, the store of a dockable ship.
        Command = 1,

        // The death of a hostile objective or a dockable ship; a forced destruction (kill-all) too.
        Destroyed = 2,
    }

    // The runtime of an exploration level (new combat simulator, phase 3, step 5; TODO/exploration_mode.md): the
    // spawners activate near the player and vanish far from it, the player docks at the objectives, and the battle ends
    // when every objective is complete, on exit, or when the player is out of lives. It uses only the mode API.
    //
    // BeginTick: the commands, the lives, the start (tick 0), the spawners, then the docking. EndTick: the lives, the
    // unit transitions of the spawners, then the result. The battle continues after the result (ContinueAfterResult),
    // and the mode keeps running.
    public sealed class ExplorationMode : IBattleMode
    {
        // Command kinds.
        public const int CompleteObjective = 1;
        public const int Exit = 2;
        public const int BuyLife = 3;
        public const int Decline = 4;
        public const int KillAll = 5;

        // Event codes. Docked and undocked: objective ID, player entity. Completed: objective ID, ExplorationCompletion.
        // Lives changed: unit ID, lives left. Decision: unit ID, deaths.
        public const int Docked = 1;
        public const int Undocked = 2;
        public const int ObjectiveCompleted = 3;
        public const int LivesChanged = 4;
        public const int LivesDecision = 5;

        // Result reasons.
        public const ushort AllComplete = 1;
        public const ushort PlayerExit = 2;
        public const ushort PlayerOut = 3;

        public ExplorationMode(BattleSetup setup, ExplorationRules rules)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _layout = rules.Layout;
            _torus = _layout.Torus;
            _docking = new Docking(setup, Docked, Undocked, rules.DockingReleaseMargin);
            _lives = new Lives(LivesChanged, LivesDecision);

            var spawners = _layout.Spawners;
            _states = new ExplorationSpawnerState[spawners.Count];
            _deathTicks = new long[spawners.Count];
            _shipTicks = new long[spawners.Count];
            _shipPositions = new Vec2[spawners.Count];
            for (var i = 0; i < _shipTicks.Length; ++i) _shipTicks[i] = -1;

            _completed = new bool[_layout.Objectives.Count];
            for (var i = 0; i < _completed.Length; ++i)
            {
                _completed[i] = rules.CompletedObjectives[i];
                if (!_completed[i]) ++RemainingObjectives;
            }

            // A complete objective's ship never comes, and its guardians neither (their parent is dead).
            foreach (var spawner in spawners)
            {
                var objective = spawner.Parent >= 0 ? spawners[spawner.Parent].Objective : spawner.Objective;
                if (objective >= 0 && _completed[objective]) _states[spawner.Index] = ExplorationSpawnerState.Dead;
            }
        }

        public ExplorationRules Rules { get; }
        public int RemainingObjectives { get; private set; }
        public long TimerElapsedTicks => 0;

        public ExplorationSpawnerState State(int spawner) => _states[spawner];
        public bool IsComplete(int objective) => _completed[objective];
        public LifeStatus PlayerStatus => _lives.Status(Rules.Player);

        public void BeginTick(ModeContext context, ReadOnlySpan<ModeCommand> inputs)
        {
            if (!_started) Start(context);

            foreach (var input in inputs)
            {
                switch (input.Kind)
                {
                    case CompleteObjective:
                        if (input.Argument0 >= 0 && input.Argument0 < _completed.Length)
                            Complete(context, (int)input.Argument0, ExplorationCompletion.Command);
                        break;
                    case Exit:
                        _exit = true;
                        break;
                    case BuyLife:
                        _lives.AddLife(Rules.Player);
                        break;
                    case Decline:
                        _lives.Decline(Rules.Player);
                        break;
                    case KillAll:
                        for (var i = 0; i < _states.Length; ++i)
                            if (_states[i] == ExplorationSpawnerState.Active &&
                                _layout.Spawners[i].Role != ExplorationShipRole.DockableShip)
                                context.DestroyUnit(_layout.Spawners[i].UnitId);
                        break;
                }
            }

            _lives.BeginTick(context);

            var player = default(EntityId);
            var playerPosition = default(Vec2);
            var hasPlayer = context.TryGetUnit(Rules.Player, out var unit) && unit.Status == UnitStatus.Active &&
                            FindShip(context, unit.ActiveEntity, out playerPosition);
            if (hasPlayer) player = unit.ActiveEntity;

            ReadShipPositions(context);
            UpdateSpawners(context, hasPlayer, playerPosition);
            MoveDockableShips();
            _docking.Update(context, player);
        }

        // The mode deploys every unit.
        public bool AllowDeploy(ModeContext context, in UnitState unit, out string reason)
        {
            reason = "the exploration mode deploys the units";
            return false;
        }

        public void OnTransition(in UnitChange change) { }

        public BattleResult EndTick(ModeContext context, long confirmedThrough, out BattleResult candidate)
        {
            _lives.EndTick(context);

            foreach (var item in context.TickEvents)
            {
                if (item.Kind != BattleEventKind.UnitTransition || !_layout.TryFindSpawner(item.UnitId, out var spawner))
                    continue;
                switch (item.TransitionReason)
                {
                    case UnitTransitionReason.Retreated:
                        _states[spawner.Index] = ExplorationSpawnerState.Away;
                        break;
                    case UnitTransitionReason.Killed:
                    case UnitTransitionReason.ForcedDestruction:
                        _states[spawner.Index] = ExplorationSpawnerState.Dead;
                        _deathTicks[spawner.Index] = context.Tick;
                        if (spawner.Objective >= 0) Complete(context, spawner.Objective, ExplorationCompletion.Destroyed);
                        break;
                }
            }

            if (!_result.IsCompleted)
            {
                if (RemainingObjectives == 0) _result = Result(context, 0, AllComplete);
                else if (_lives.Status(Rules.Player) == LifeStatus.Out) _result = Result(context, 1, PlayerOut);
                else if (_exit) _result = Result(context, 1, PlayerExit);
            }

            candidate = _result;
            return _result;
        }

        // Tick 0: the player at the start, the dockable ships at their objectives, the docking areas.
        private void Start(ModeContext context)
        {
            _started = true;
            if (!context.Deploy(Rules.Player, SpawnPlacement.At(_layout.Start, _layout.StartAngle)))
                throw new InvalidOperationException($"The player's unit {Rules.Player} did not deploy.");
            _lives.Track(Rules.Player, Rules.PlayerLives);

            foreach (var objective in _layout.Objectives)
            {
                if (_completed[objective.Id] || objective.Kind == ExplorationObjectiveKind.Hostile) continue;
                _docking.Add(objective.Id, objective.Position, objective.Radius);
                if (objective.Kind != ExplorationObjectiveKind.DockableShip) continue;

                var spawner = _layout.Spawners[objective.Spawner];
                context.AddUnit(new UnitSetup(spawner.UnitId, 0,
                    Rules.Content.CreateShip(spawner.Role, spawner.ContentKey), allegiance: Allegiance.Ally));
                context.Deploy(spawner.UnitId, SpawnPlacement.At(spawner.Position, spawner.Angle));
                _states[spawner.Index] = ExplorationSpawnerState.Active;
            }
        }

        // A hostile objective has no docking area; Remove ignores it.
        private void Complete(ModeContext context, int objective, ExplorationCompletion how)
        {
            if (_completed[objective]) return;
            _completed[objective] = true;
            --RemainingObjectives;
            _docking.Remove(context, objective);
            context.Publish(new ModeEvent(ObjectiveCompleted, objective, (long)how));
        }

        // One pass over the snapshot: the position of each spawner's ship in the last completed tick.
        private void ReadShipPositions(ModeContext context)
        {
            var tick = context.Snapshot.Tick;
            foreach (var ship in context.Snapshot.Ships)
            {
                if (ship.IsDrone || ship.IsDecoy || !_layout.TryFindSpawner(ship.UnitId, out var spawner)) continue;
                _shipTicks[spawner.Index] = tick;
                _shipPositions[spawner.Index] = ship.Position;
            }
            _snapshotTick = tick;
        }

        private void UpdateSpawners(ModeContext context, bool hasPlayer, Vec2 player)
        {
            var activation = Rules.ActivationDistance * Rules.ActivationDistance;
            var vanish = Rules.VanishDistance * Rules.VanishDistance;
            var spawners = _layout.Spawners;
            for (var i = 0; i < spawners.Count; ++i)
            {
                var spawner = spawners[i];
                switch (_states[i])
                {
                    case ExplorationSpawnerState.Dormant:
                        if (!hasPlayer || _torus.Delta(spawner.Position, player).LengthSquared > activation) break;
                        context.AddUnit(new UnitSetup(spawner.UnitId, 1,
                            Rules.Content.CreateShip(spawner.Role, spawner.ContentKey), allegiance: Allegiance.Enemy));
                        Deploy(context, spawner, player);
                        break;
                    case ExplorationSpawnerState.Away:
                        if (!hasPlayer || _torus.Delta(spawner.Position, player).LengthSquared > activation) break;
                        Deploy(context, spawner, player);
                        break;
                    case ExplorationSpawnerState.Active:
                        // A dockable ship never vanishes. A ship that deployed in this tick is not in the snapshot.
                        if (!hasPlayer || spawner.Role == ExplorationShipRole.DockableShip || _shipTicks[i] != _snapshotTick)
                            break;
                        if (_torus.Delta(_shipPositions[i], player).LengthSquared <= vanish) break;
                        if (context.TryGetUnit(spawner.UnitId, out var unit)) context.Vanish(unit.ActiveEntity);
                        break;
                    case ExplorationSpawnerState.Dead:
                        if (spawner.RespawnDelayTicks <= 0 || context.Tick < _deathTicks[i] + spawner.RespawnDelayTicks)
                            break;
                        if (spawner.Parent >= 0 && _states[spawner.Parent] == ExplorationSpawnerState.Dead) break;
                        if (context.Respawn(spawner.UnitId)) _states[i] = ExplorationSpawnerState.Away;
                        break;
                }
            }
        }

        // The docking area of a live dockable ship follows the ship of the last completed tick.
        private void MoveDockableShips()
        {
            foreach (var objective in _layout.Objectives)
            {
                if (objective.Kind != ExplorationObjectiveKind.DockableShip || _completed[objective.Id]) continue;
                if (_shipTicks[objective.Spawner] == _snapshotTick)
                    _docking.Move(objective.Id, _shipPositions[objective.Spawner]);
            }
        }

        // At the spawner's position, facing the player, at rest.
        private void Deploy(ModeContext context, in ExplorationSpawner spawner, Vec2 player)
        {
            var direction = _torus.Delta(spawner.Position, player);
            var angle = (float)Math.Atan2(direction.Y, direction.X);
            if (!context.Deploy(spawner.UnitId, SpawnPlacement.At(spawner.Position, angle)))
                throw new InvalidOperationException($"The spawner unit {spawner.UnitId} did not deploy.");
            _states[spawner.Index] = ExplorationSpawnerState.Active;
        }

        private static bool FindShip(ModeContext context, EntityId id, out Vec2 position)
        {
            var found = context.Snapshot.TryGetShip(id, out var ship);
            position = ship.Position;
            return found;
        }

        private static BattleResult Result(ModeContext context, int winningSide, ushort reason) =>
            new BattleResult(BattleOutcome.Victory, winningSide, BattleEndReason.Mode, context.Tick, reason);

        private readonly ExplorationLayout _layout;
        private readonly Torus _torus;
        private readonly Docking _docking;
        private readonly Lives _lives;
        private readonly ExplorationSpawnerState[] _states;
        private readonly long[] _deathTicks;

        // The snapshot tick in which each spawner's ship was last seen, and its position then.
        private readonly long[] _shipTicks;
        private readonly Vec2[] _shipPositions;
        private long _snapshotTick = -1;

        private readonly bool[] _completed;
        private bool _started;
        private bool _exit;
        private BattleResult _result;
    }
}

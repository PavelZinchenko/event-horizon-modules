using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using Utilites;

namespace CombatSimulation.Core.Tests
{
    // The phase 2 test mode (battle modes, step 5): a player unit with lives, enemy waves, a respawn after a delay, and a
    // result. It uses only the mode API, so it proves that a mode needs no core change.
    internal sealed class WaveRules : IBattleRules
    {
        public WaveRules(ShipDefinition player, ShipDefinition enemy, int lives, int waves, int waveSize, long waveInterval)
        {
            Player = player;
            Enemy = enemy;
            Lives = lives;
            Waves = waves;
            WaveSize = waveSize;
            WaveInterval = waveInterval;
        }

        public ShipDefinition Player { get; }
        public ShipDefinition Enemy { get; }
        public int Lives { get; }
        public int Waves { get; }
        public int WaveSize { get; }
        public long WaveInterval { get; }

        public void Validate(BattleSetup setup)
        {
            if (setup.Units.Count != 0) throw new ArgumentException("The wave mode creates its own units.", nameof(setup));
        }

        public IBattleMode CreateMode(BattleSetup setup) => new WaveMode(this);
    }

    internal sealed class WaveMode : IBattleMode
    {
        // Command kinds.
        public const int KillAllEnemies = 1;
        public const int Exit = 2;

        // Event codes.
        public const int LivesChanged = 1;
        public const int WaveArrived = 2;

        // Result reasons.
        public const ushort LevelComplete = 1;
        public const ushort OutOfLives = 2;
        public const ushort PlayerExit = 3;

        public const long RespawnTicks = 250;
        public const uint WaveRandom = 1;
        public const float WaveDistance = 40f;

        public static readonly UnitId Player = new UnitId(1);
        public static readonly Vec2 Start = new Vec2(100f, 100f);

        public WaveMode(WaveRules rules)
        {
            _rules = rules;
            _lives = rules.Lives;
        }

        public long TimerElapsedTicks => 0;

        public void BeginTick(ModeContext context, ReadOnlySpan<ModeCommand> inputs)
        {
            if (context.Tick == 0)
            {
                context.AddUnit(new UnitSetup(Player, 0, _rules.Player, allegiance: Allegiance.Player));
                context.Deploy(Player, SpawnPlacement.At(Start, 0f));
            }

            foreach (var input in inputs)
            {
                if (input.Kind == Exit) _exit = true;
                else if (input.Kind == KillAllEnemies)
                    for (var i = 0; i < context.UnitCount; ++i)
                    {
                        var unit = context.GetUnit(i);
                        if (unit.Side == 1) context.DestroyUnit(unit.UnitId);
                    }
            }

            if (_deathTick >= 0 && _lives > 0 && context.Tick >= _deathTick + RespawnTicks)
            {
                _deathTick = -1;
                context.Respawn(Player);
                context.Deploy(Player, SpawnPlacement.At(Start, 0f));
            }

            if (_waves < _rules.Waves && context.Tick == 1 + _waves * _rules.WaveInterval)
            {
                var random = context.Random(WaveRandom);
                for (var k = 0; k < _rules.WaveSize; ++k)
                {
                    var id = new UnitId((uint)(100 + _waves * _rules.WaveSize + k + 1));
                    var angle = random.NextFloat() * 2f * (float)Math.PI;
                    var position = Start + WaveDistance * new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
                    context.AddUnit(new UnitSetup(id, 1, _rules.Enemy));
                    context.Deploy(id, SpawnPlacement.Near(position, angle + (float)Math.PI, 10f));
                }
                ++_waves;
                context.Publish(new ModeEvent(WaveArrived, _waves));
            }
        }

        public bool AllowDeploy(ModeContext context, in UnitState unit, out string reason)
        {
            reason = unit.UnitId == Player ? "the mode deploys the player" : null;
            return reason == null;
        }

        public void OnTransition(in UnitChange change)
        {
            if (change.UnitId != Player || change.Reason != UnitTransitionReason.Killed) return;
            _deathTick = change.Tick;
            --_lives;
            _livesChanged = true;
        }

        public BattleResult EndTick(ModeContext context, long confirmedThrough, out BattleResult candidate)
        {
            if (_livesChanged)
            {
                _livesChanged = false;
                context.Publish(new ModeEvent(LivesChanged, _lives));
            }

            var enemies = context.CountUnits(1, UnitStatus.Ready) + context.CountUnits(1, UnitStatus.Active);
            if (_exit) candidate = new BattleResult(BattleOutcome.Victory, 1, BattleEndReason.Mode, context.Tick, PlayerExit);
            else if (_lives == 0) candidate = new BattleResult(BattleOutcome.Victory, 1, BattleEndReason.Mode, context.Tick, OutOfLives);
            else if (_waves == _rules.Waves && enemies == 0)
                candidate = new BattleResult(BattleOutcome.Victory, 0, BattleEndReason.Mode, context.Tick, LevelComplete);
            else candidate = default;
            return candidate;
        }

        private readonly WaveRules _rules;
        private int _lives;
        private int _waves;
        private long _deathTick = -1;
        private bool _livesChanged;
        private bool _exit;
    }

    // A mode for single checks: each BeginTick runs the actions queued for it, then Begin, and EndTick runs End. It ends
    // only when Outcome gives a result.
    internal sealed class ScriptRules : IBattleRules
    {
        public void Validate(BattleSetup setup) { }

        public IBattleMode CreateMode(BattleSetup setup) => Mode = new ScriptMode();

        // The mode of the last battle created from these rules.
        public ScriptMode Mode { get; private set; }
    }

    internal sealed class ScriptMode : IBattleMode
    {
        public readonly List<Action<ModeContext>> Next = new List<Action<ModeContext>>();
        public Action<ModeContext> Begin;
        public Action<ModeContext> End;
        public Func<UnitState, string> Veto;
        public Func<ModeContext, BattleResult> Outcome;
        public readonly List<UnitChange> Changes = new List<UnitChange>();
        public readonly List<ModeCommand> Inputs = new List<ModeCommand>();

        public long TimerElapsedTicks => 0;

        public void BeginTick(ModeContext context, ReadOnlySpan<ModeCommand> inputs)
        {
            foreach (var input in inputs) Inputs.Add(input);
            var actions = Next.ToArray();
            Next.Clear();
            foreach (var action in actions) action(context);
            Begin?.Invoke(context);
        }

        public bool AllowDeploy(ModeContext context, in UnitState unit, out string reason)
        {
            reason = Veto?.Invoke(unit);
            return reason == null;
        }

        public void OnTransition(in UnitChange change) => Changes.Add(change);

        public BattleResult EndTick(ModeContext context, long confirmedThrough, out BattleResult candidate)
        {
            End?.Invoke(context);
            candidate = Outcome?.Invoke(context) ?? default;
            return candidate;
        }
    }
}

using System;
using CombatSimulation.Ai;
using CombatSimulation.Core;
using CombatSimulation.Runtime;
using GameDatabase.Model;
using UnityEngine;
using CoreEntityId = CombatSimulation.Core.EntityId;

namespace Combat.Integration.Core
{
    // A battle that steps on the main thread at the core's fixed rate (phase 9, step 2, S4). Each tick's events are handled
    // right after its step, so no event is lost. A local battle counts game time: a pause stops it.
    public sealed class LocalBattleSource : IBattleSource
    {
        public LocalBattleSource(BattleRunner runner, BehaviorTreeFactory trees, int localSide)
        {
            Runner = runner;
            _trees = trees;
            LocalSide = localSide;
        }

        public BattleRunner Runner { get; }
        public int LocalSide { get; }
        public bool IsRunning => !Runner.IsCompleted;

        // A local battle also steps after its result (phase 2b, D12).
        public bool IsStepping => !Runner.IsStopped;

        public long CommandTick => Runner.Tick;
        public bool IsRemote => false;
        public float Alpha => Mathf.Clamp01(_accumulator / Battle.TickDuration);

        public void Run(IBattleSink sink, bool playerInput)
        {
            if (!IsStepping)
                return;

            _accumulator += Time.deltaTime;
            var steps = 0;
            while (_accumulator >= Battle.TickDuration && IsStepping)
            {
                if (steps == MaxStepsPerFrame)
                {
                    // A long frame drops the rest instead of catching up later.
                    _accumulator = 0f;
                    break;
                }

                _accumulator -= Battle.TickDuration;
                Step(sink, playerInput);
                steps++;
            }
        }

        // One tick: the mode's commands, step, then handle the tick's events before the next step.
        public void Step(IBattleSink sink, bool playerInput)
        {
            if (!IsStepping)
                return;

            // After the result the mode submits nothing.
            if (IsRunning)
                sink.BeforeStep();

            // The pilots of this step think on the snapshot of the current tick.
            if (playerInput)
                _trees.OnPlayerInput(Runner.Tick);

            Runner.Step();
            sink.Process(Runner.Snapshot);
        }

        public void TakeBroadcasts(long tick, Action<CoreEntityId, string, ColorData> show) { }

        private const int MaxStepsPerFrame = 5;

        private readonly BehaviorTreeFactory _trees;
        private float _accumulator;
    }
}

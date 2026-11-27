using System;
using CombatSimulation.Core;
using GameDatabase.Model;
using CoreEntityId = CombatSimulation.Core.EntityId;

namespace Combat.Integration.Core
{
    // Where CoreBattleHost's snapshots come from (phase 9, step 2, S4): a BattleRunner on the main thread
    // (LocalBattleSource), or a session on another thread (CoreDuelSource).
    public interface IBattleSource
    {
        int LocalSide { get; }

        // False after the result: no deployment, mode command or timer.
        bool IsRunning { get; }

        // The battle steps and the player flies.
        bool IsStepping { get; }

        // The tick that the next command targets.
        long CommandTick { get; }

        // The snapshots are copies from another thread: the renderer smooths the corrections, and the exit delay counts
        // real time.
        bool IsRemote { get; }

        // While the battle steps: how far the frame is past the last snapshot, 0 to 1, for the render interpolation.
        float Alpha { get; }

        // Once per frame: hands each new snapshot to the sink, oldest first. playerInput: the player steered in this frame.
        void Run(IBattleSink sink, bool playerInput);

        // The ShowMessage texts of a remote battle up to the given tick, in order. A local battle broadcasts through the
        // host's OnBroadcast.
        void TakeBroadcasts(long tick, Action<CoreEntityId, string, ColorData> show);
    }

    // CoreBattleHost's side of IBattleSource.Run.
    public interface IBattleSink
    {
        // Before each local step while the battle runs: the mode's own commands.
        void BeforeStep();

        // One tick's snapshot.
        void Process(BattleSnapshot snapshot);

        // The battle stopped before its result (a lost link): the host exits as after a result.
        void EndWithoutResult();
    }
}

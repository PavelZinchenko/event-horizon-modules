using System;
using CombatSimulation.Core;
using CombatSimulation.Runtime;
using GameDatabase.Model;
using UnityEngine;
using CoreEntityId = CombatSimulation.Core.EntityId;

namespace Combat.Integration.Core
{
    // The online duel as the snapshot source of the shared CoreBattleHost (phase 7, step 6; Frontier's phase 9, step 2,
    // S4). The session thread is the authority: each frame hands over every snapshot copy that it published since the
    // last frame, oldest first. After the result the thread only pumps the network and publishes nothing more.
    // The duel state owns the session thread and stops it.
    public sealed class CoreDuelSource : IBattleSource
    {
        // onFrame: each frame after the copies, while the duel runs and its link holds.
        public CoreDuelSource(IRemoteBattle duel, Action onFrame)
        {
            _duel = duel;
            _onFrame = onFrame;
        }

        public int LocalSide => _duel.LocalSide;
        public bool IsRunning => !_completed;

        // A duel stops at its result.
        public bool IsStepping => !_completed;

        // The tick after the last handled snapshot: this thread does not know the session thread's own tick.
        public long CommandTick => _lastTick + 1;

        public bool IsRemote => true;
        public float Alpha => Mathf.Clamp01((float)(_duel.Now() - _publishedAt) / Battle.TickDuration);

        public void Run(IBattleSink sink, bool playerInput)
        {
            var thread = _duel.Thread;
            // Read before the copies: a thread that stopped has published all of its copies by then.
            var linkLoss = thread.Status.LinkLoss;
            var error = thread.Error;
            if (!_threadErrorLogged && error != null)
            {
                _threadErrorLogged = true;
                Debug.LogError($"CoreDuelSource: the duel session thread stopped at tick {thread.Status.Tick}: {error}");
            }

            while (thread.TryTake(out var snapshot, out var publishedAt))
            {
                if (IsRunning)
                {
                    _completed = snapshot.IsCompleted;
                    _publishedAt = publishedAt;
                    _lastTick = snapshot.Tick;
                    sink.Process(snapshot);
                }
                thread.Release(snapshot);
            }

            if (!IsRunning)
                return;

            if (linkLoss != SessionLinkLoss.None || error != null)
            {
                EndWithoutResult(sink, linkLoss);
                return;
            }

            _onFrame();
            if (playerInput)
                _duel.MarkPlayerInput();
        }

        public void TakeBroadcasts(long tick, Action<CoreEntityId, string, ColorData> show) =>
            _duel.TakeBroadcasts(tick, show);

        // Phase 7, step 8: the session thread stopped before the result. A lost link is a draw on this player (decision 3);
        // a thread error (linkLoss None) ends without a result. The battle freezes, then exits as after a result.
        private void EndWithoutResult(IBattleSink sink, SessionLinkLoss linkLoss)
        {
            _completed = true;
            _duel.LinkLoss = linkLoss;
            Debug.LogWarning($"CoreDuelSource: duel ended without a result ({linkLoss}), local side {LocalSide}, tick {_lastTick}");
            sink.EndWithoutResult();
        }

        private readonly IRemoteBattle _duel;
        private readonly Action _onFrame;
        // The last handled snapshot's tick, the result flag and the copy's publish time.
        private long _lastTick = -1;
        private bool _completed;
        private double _publishedAt;
        private bool _threadErrorLogged;
    }
}

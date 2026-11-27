using System;
using CombatSimulation.Runtime;
using GameDatabase.Model;
using CoreEntityId = CombatSimulation.Core.EntityId;

namespace Combat.Integration.Core
{
    // A running online battle that CoreDuelSource reads: the session thread and the state that the game keeps on the main
    // thread. Each game implements it with its own setup (Main's CoreDuelBattle).
    public interface IRemoteBattle
    {
        int LocalSide { get; }
        SessionThread Thread { get; }

        // The clock that the session thread runs on.
        Func<double> Now { get; }

        // Main thread: CoreDuelSource sets it when the session thread gave up the link before the result.
        SessionLinkLoss LinkLoss { get; set; }

        // The player steered in this frame: the autopilot holds back at the next tick.
        void MarkPlayerInput();

        // ShowMessage texts of the ticks up to the given one, in order.
        void TakeBroadcasts(long tick, Action<CoreEntityId, string, ColorData> show);
    }
}

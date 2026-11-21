using CoreEntityId = CombatSimulation.Core.EntityId;

namespace Combat.Integration.Core
{
    // The player pilots of a game's pilot provider (phase 9, step 2, S4): CoreBattleHost hands their commands to the
    // controls and releases them when their ships go. Each game's CorePilotProvider implements it.
    public interface IPlayerPilots
    {
        bool TryGetPlayerPilot(CoreEntityId ship, out PlayerPilot pilot);
        void Release(CoreEntityId ship);
        void Clear();
    }
}

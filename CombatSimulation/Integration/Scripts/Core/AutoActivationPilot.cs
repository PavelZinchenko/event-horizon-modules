using CombatSimulation.Core;
using CombatSimulation.Runtime;

namespace Combat.Integration.Core
{
    // Legacy ShipSystems applies the automatic key rule every frame, whatever the controller does ("Ship mapping (3a)",
    // row 10). A tree pilot submits nothing while its root keeps failing (T7), so this pilot submits the kept frame again
    // when the automatic bits change. One-shot commands are never sent twice.
    public sealed class AutoActivationPilot : IPilot
    {
        public AutoActivationPilot(IPilot pilot, CoreAutoActivation autoActivation)
        {
            Pilot = pilot;
            _autoActivation = autoActivation;
        }

        public IPilot Pilot { get; }

        public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
        {
            if (Pilot.Think(snapshot, self, out controls))
            {
                controls = With(controls, _autoActivation.Apply(snapshot, self, controls.Systems), keepCommands: true);
                _kept = With(controls, controls.Systems, keepCommands: false);
                return true;
            }

            var systems = _autoActivation.Apply(snapshot, self, _kept.Systems);
            if (systems == _kept.Systems)
                return false;

            _kept = With(_kept, systems, keepCommands: false);
            controls = _kept;
            return true;
        }

        private static PilotControls With(in PilotControls controls, ulong systems, bool keepCommands) =>
            PilotControls.Create(controls.HasCourse ? controls.Course : (float?)null, controls.Throttle, systems,
                keepCommands && controls.SelfDestruct, keepCommands && controls.Vanish,
                keepCommands && controls.ChangesAllegiance ? controls.Allegiance : (Allegiance?)null,
                controls.TurretTarget, keepCommands ? controls.TurretTargetSystems : 0,
                keepCommands ? controls.SearchTurretSystems : 0);

        private readonly CoreAutoActivation _autoActivation;

        // The frame in force: the last one submitted, without its commands. Before the first frame, zero controls.
        private PilotControls _kept = PilotControls.Create(null, 0f, 0UL);
    }
}

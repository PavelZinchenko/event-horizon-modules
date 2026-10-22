using System;
using System.Collections.Generic;
using CombatSimulation.Core;

namespace CombatSimulation.Runtime
{
    // Drives one battle: submits commands with unique command IDs, steps the core and runs pilots.
    // Pilots run synchronously after each step in locked mode, so a pilot is never late.
    // Free-running mode waits for the worker thread.
    public sealed class BattleRunner
    {
        public BattleRunner(BattleSetup setup, IPilotProvider pilots)
        {
            _battle = new Battle(setup);
            _provider = pilots ?? throw new ArgumentNullException(nameof(pilots));
        }

        public BattleId BattleId => _battle.BattleId;

        // The next tick to execute.
        public long Tick => _battle.Tick;

        public bool IsCompleted => _battle.IsCompleted;
        public BattleSnapshot Snapshot => _battle.Snapshot;

        public bool Deploy(RosterId rosterId, long targetTick) =>
            _battle.Submit(RosterCommand.Deploy(BattleId, _nextCommandId++, targetTick, rosterId));

        public bool Retreat(EntityId entityId, long targetTick) =>
            _battle.Submit(RosterCommand.Retreat(BattleId, _nextCommandId++, targetTick, entityId));

        public bool SelfDestruct(EntityId entityId, long targetTick) =>
            _battle.Submit(new SelfDestructCommand(BattleId, _nextCommandId++, targetTick, entityId));

        public bool Surrender(int side, long targetTick) =>
            _battle.Submit(RosterCommand.Surrender(BattleId, _nextCommandId++, targetTick, side));

        public bool KillAll(int requestingSide, long targetTick) =>
            _battle.Submit(RosterCommand.KillAll(BattleId, _nextCommandId++, targetTick, requestingSide));

        public void Step()
        {
            _battle.Step();

            var snapshot = _battle.Snapshot;
            UpdateRegistrations(snapshot);
            if (snapshot.IsCompleted) return;

            for (var i = 0; i < _pilots.Count; ++i)
            {
                var registration = _pilots[i];
                if (snapshot.Tick % registration.Schedule.Period != registration.Offset) continue;
                if (!snapshot.TryGetShip(registration.EntityId, out var ship)) continue;
                if (!registration.Pilot.Think(snapshot, ship, out var controls)) continue;

                _battle.Submit(ControlFrame.Create(BattleId, _nextCommandId++, snapshot.Tick + registration.Schedule.Delay,
                    registration.EntityId, controls.HasCourse ? controls.Course : (float?)null, controls.Throttle,
                    controls.Systems));
                if (controls.SelfDestruct)
                    SelfDestruct(registration.EntityId, snapshot.Tick + registration.Schedule.Delay);
            }
        }

        // Spawn events add pilots in event order; removal events drop them.
        private void UpdateRegistrations(BattleSnapshot snapshot)
        {
            var events = snapshot.Events;
            for (var i = 0; i < events.Length; ++i)
            {
                var item = events[i];

                // Bullets have no pilots.
                if (item.IsBullet) continue;

                if (item.Kind == BattleEventKind.Removal)
                {
                    for (var j = 0; j < _pilots.Count; ++j)
                    {
                        if (_pilots[j].EntityId != item.EntityId) continue;
                        _pilots.RemoveAt(j);
                        break;
                    }
                }
                else if (item.Kind == BattleEventKind.Spawn && snapshot.TryGetShip(item.EntityId, out var ship))
                {
                    if (!_battle.Setup.IsLocalOwner(ship.Owner)) continue;
                    var pilot = _provider.CreatePilot(snapshot, ship, out var schedule);
                    if (pilot == null) continue;
                    if (schedule.Period < 1)
                        throw new InvalidOperationException($"Pilot provider returned no schedule for entity {item.EntityId}.");
                    _pilots.Add(new Registration(item.EntityId, pilot, schedule, schedule.OffsetOf(item.EntityId)));
                }
            }
        }

        private readonly Battle _battle;
        private readonly IPilotProvider _provider;
        private readonly List<Registration> _pilots = new List<Registration>();
        private ulong _nextCommandId = 1;

        private readonly struct Registration
        {
            public readonly EntityId EntityId;
            public readonly IPilot Pilot;
            public readonly PilotSchedule Schedule;
            public readonly int Offset;

            public Registration(EntityId entityId, IPilot pilot, PilotSchedule schedule, int offset)
            {
                EntityId = entityId;
                Pilot = pilot;
                Schedule = schedule;
                Offset = offset;
            }
        }
    }
}

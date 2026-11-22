using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // The battle mode's hooks and actions (battle modes, phase 2). The mode runs at stage 1 before the queued commands,
    // and at stage 13. It never runs inside a stage.
    public sealed partial class Battle
    {
        public bool Submit(in ModeCommand command)
        {
            if (IsStopped) throw new InvalidOperationException("The battle has a result; it accepts no commands.");
            var reason = _commands.Add(command, Tick);
            return reason == null || Reject(command, reason);
        }

        internal IBattleMode Mode => _mode;
        internal ReadOnlySpan<BattleEvent> TickEvents => _world.Events.AsSpan();

        // Stage 1, first: the mode commands of the tick, then the mode's actions.
        private void BeginModeTick()
        {
            _commands.TakeDue(Tick, _dueModeCommands);
            if (_modeInputs.Length < _dueModeCommands.Count) _modeInputs = new ModeCommand[_dueModeCommands.Count * 2];
            _dueModeCommands.CopyTo(_modeInputs);
            _modeStage = ModeStage.Begin;
            try
            {
                _mode.BeginTick(_modeContext, new ReadOnlySpan<ModeCommand>(_modeInputs, 0, _dueModeCommands.Count));
            }
            finally
            {
                _modeStage = ModeStage.None;
            }
        }

        private bool AllowDeploy(Unit slot, out string reason)
        {
            _modeStage = ModeStage.Deploy;
            try
            {
                return _mode.AllowDeploy(_modeContext, new UnitState(slot), out reason);
            }
            finally
            {
                _modeStage = ModeStage.None;
            }
        }

        // A received transition, by its owner's tick (BattleNetwork).
        internal void NotifyMode(in UnitChange change) => _mode.OnTransition(change);

        // Stage 13.
        private void EndModeTick()
        {
            _modeStage = ModeStage.End;
            try
            {
                var result = _mode.EndTick(_modeContext, _network?.ResultFrontier() ?? Tick, out var candidate);
                TimerElapsedTicks = _mode.TimerElapsedTicks;
                // After the result (ContinueAfterResult) the mode keeps running, but the first result stays.
                if (IsCompleted) return;
                Result = result;
                CandidateResult = candidate;
            }
            finally
            {
                _modeStage = ModeStage.None;
            }
        }

        internal void ModeAddUnit(UnitSetup unit)
        {
            RequireAction();
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (Units.Find(unit.UnitId) != null)
                throw new ArgumentException($"Duplicate unit ID {unit.UnitId}.", nameof(unit));
            if (!Setup.IsLocalOwner(unit.Owner))
                throw new ArgumentException("A mode adds units of local owners only.", nameof(unit));
            Setup.ValidateDamageTypes(unit.Ship);
            Units.Add(unit);
        }

        internal bool ModeDeploy(UnitId id, in SpawnPlacement placement)
        {
            RequireAction();
            var slot = Units.Find(id);
            return slot != null && slot.Status == UnitStatus.Ready && DeploySlot(slot, placement);
        }

        internal bool ModeRespawn(UnitId id)
        {
            RequireAction();
            var slot = Units.Find(id);
            if (slot == null || slot.Status != UnitStatus.Destroyed) return false;
            slot.Respawn();
            AddTransition(slot, UnitStatus.Destroyed, UnitTransitionReason.Respawned, default);
            return true;
        }

        internal bool ModeRetreat(EntityId id)
        {
            RequireAction();
            var ship = _world.FindShip(id);
            if (ship?.Slot == null || ship.RetreatCompletionTick != ShipEntity.NoRetreat) return false;
            ship.RetreatCompletionTick = Tick + RetreatTicks;
            return true;
        }

        internal bool ModeVanish(EntityId id)
        {
            RequireAction();
            var ship = _world.FindShip(id);
            if (ship == null) return false;
            ship.PendingVanish = true;
            return true;
        }

        // Stage 1, before the engine and the physics. A body with a fixed position keeps its zero velocity.
        internal bool ModeSetVelocity(EntityId id, Vec2 velocity)
        {
            RequireAction();
            if (!velocity.IsFinite) throw new ArgumentOutOfRangeException(nameof(velocity), "The velocity must be finite.");
            var ship = _world.FindShip(id);
            if (ship == null) return false;
            World.SetLinearVelocity(ship.Body, velocity);
            return true;
        }

        internal bool ModeChangeAllegiance(EntityId id, Allegiance allegiance)
        {
            RequireAction();
            if (!Allegiances.IsValid(allegiance)) throw new ArgumentOutOfRangeException(nameof(allegiance));
            var ship = _world.FindShip(id);
            if (ship == null) return false;
            ChangeAllegiance(ship, allegiance);
            return true;
        }

        internal bool ModeDestroyUnit(UnitId id)
        {
            RequireAction();
            var slot = Units.Find(id);
            if (slot == null || slot.Status == UnitStatus.Destroyed) return false;
            if (!_destroyUnits.Contains(slot)) _destroyUnits.Add(slot);
            return true;
        }

        internal bool ModeRemoveUnit(UnitId id)
        {
            RequireAction();
            var slot = Units.Find(id);
            if (slot == null) return false;
            foreach (var removal in _removeUnits)
                if (removal.Unit == slot) return true;
            _removeUnits.Add(new Removal { Unit = slot });
            return true;
        }

        internal EntityId ModeSpawnAsteroid(AsteroidSetup asteroid)
        {
            RequireAction();
            if (asteroid == null) throw new ArgumentNullException(nameof(asteroid));
            return _world.Asteroids.Spawn(asteroid)?.Id ?? default;
        }

        internal bool ModeVanishAsteroid(EntityId id)
        {
            RequireAction();
            return _world.Asteroids.Vanish(id);
        }

        internal bool ModeSetNoDamage(UnitId id, bool noDamage)
        {
            RequireAction();
            var slot = Units.Find(id);
            if (slot == null) return false;
            slot.NoDamage = noDamage;
            return true;
        }

        internal void ModeDrain(UnitId id, float armorFraction, float shieldFraction)
        {
            if (_modeStage != ModeStage.Begin)
                throw new InvalidOperationException("A mode drains only in BeginTick.");
            var slot = Units.Find(id) ?? throw new ArgumentException($"Unknown unit ID {id}.", nameof(id));
            _drains.Add(new Drain(slot, armorFraction, shieldFraction));
        }

        internal void ModePublish(in ModeEvent item)
        {
            if (_modeStage != ModeStage.Begin && _modeStage != ModeStage.End)
                throw new InvalidOperationException("A mode publishes only in BeginTick and EndTick.");
            _world.Events.Add(BattleEvent.ModeItem(BattleId, Tick, _world.Events.Count, item));
        }

        // Stage 10, where the legacy overtime drain ran (DamageSystem.Apply): in call order.
        internal void ApplyModeDrains()
        {
            foreach (var drain in _drains)
            {
                var ship = drain.Unit.ActiveShip;
                if (ship == null || !Setup.IsLocalOwner(ship.Owner)) continue;
                ship.Armor.Consume(drain.ArmorFraction * ship.Armor.Max);
                ship.Shield.Consume(drain.ShieldFraction * ship.Shield.Max);
            }
            _drains.Clear();
        }

        // Stage 12, after surrender and kill-all.
        private void DestroyModeUnits()
        {
            foreach (var slot in _destroyUnits)
            {
                if (slot.Status == UnitStatus.Destroyed) continue;
                var previousStatus = slot.Status;
                var id = default(EntityId);
                if (slot.ActiveShip != null)
                {
                    id = slot.ActiveShip.Id;
                    RemoveShip(slot.ActiveShip, RemovalReason.Death);
                }
                slot.Destroy();
                AddTransition(slot, previousStatus, UnitTransitionReason.ForcedDestruction, id);
            }
            _destroyUnits.Clear();
        }

        // Stage 12, after the mode's destructions and before the drone removals: the removed units' ships vanish, and
        // their drones and clones are marked, so the drone pass removes them in its own order.
        private void VanishRemovedUnits()
        {
            for (var i = 0; i < _removeUnits.Count; ++i)
            {
                var removal = _removeUnits[i];
                var slot = removal.Unit;
                var ship = slot.ActiveShip;
                removal.PreviousStatus = slot.Status;
                if (ship != null)
                {
                    removal.EntityId = ship.Id;
                    RemoveShip(ship, RemovalReason.Vanish);
                }
                slot.Destroy();
                _removeUnits[i] = removal;

                foreach (var drone in _world.Drones)
                    if (drone.Bay?.Slot == slot) drone.PendingVanish = true;
            }
        }

        // Stage 12, after the drone removals: the units leave the table, in call order.
        private void RemoveModeUnits()
        {
            foreach (var removal in _removeUnits)
            {
                Units.Remove(removal.Unit);
                AddTransition(removal.Unit, removal.PreviousStatus, UnitTransitionReason.Removed, removal.EntityId);
            }
            _removeUnits.Clear();
        }

        // The placement of a local deployment. A unit that respawned gives its live drones to the new ship.
        private void PlaceDeployment(Unit slot, ShipEntity ship, BodyId body, in SpawnPlacement placement)
        {
            switch (placement.Kind)
            {
                case SpawnPlacementKind.At:
                    World.SetPose(body, World.Torus.Wrap(placement.Position), placement.Angle);
                    World.SetLinearVelocity(body, placement.LinearVelocity);
                    World.SetAngularVelocity(body, placement.AngularVelocity);
                    break;
                case SpawnPlacementKind.Near:
                    var position = World.Torus.Wrap(placement.Position);
                    World.SetPose(body, position, placement.Angle);
                    World.TryFindFreePosition(body, position, placement.SearchRadius, out var free);
                    World.SetPose(body, free, placement.Angle);
                    break;
                default:
                    _spawns.PlaceUnitShip(body, ship.Id, slot.Side);
                    break;
            }

            if (!slot.TakesLiveDrones) return;
            slot.TakesLiveDrones = false;
            foreach (var drone in _world.Drones)
                if (drone.Bay?.Slot == slot && !drone.MothershipOverride.HasValue) drone.MothershipOverride = ship.Id;
        }

        private void RequireAction()
        {
            if (_modeStage != ModeStage.Begin)
                throw new InvalidOperationException("A mode acts only in BeginTick.");
            if (Setup.HasRemoteOwners)
                throw new InvalidOperationException("A battle with remote owners has no mode actions (D10).");
        }

        private bool Reject(in ModeCommand command, string reason)
        {
            ++RejectedCommandCount;
            GameDiagnostics.Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected {command}: {reason}.");
            return false;
        }

        private enum ModeStage : byte { None, Begin, Deploy, End }

        private readonly struct Drain
        {
            public readonly Unit Unit;
            public readonly float ArmorFraction;
            public readonly float ShieldFraction;

            public Drain(Unit unit, float armorFraction, float shieldFraction)
            {
                Unit = unit;
                ArmorFraction = armorFraction;
                ShieldFraction = shieldFraction;
            }
        }

        private struct Removal
        {
            public Unit Unit;
            public UnitStatus PreviousStatus;
            public EntityId EntityId;
        }

        // ChangeRules replaces it (Battle.Continuity).
        private IBattleMode _mode;
        private readonly ModeContext _modeContext;
        private ModeStage _modeStage;
        private readonly List<ModeCommand> _dueModeCommands = new List<ModeCommand>();
        private ModeCommand[] _modeInputs = new ModeCommand[4];
        private readonly List<Drain> _drains = new List<Drain>();
        private readonly List<Unit> _destroyUnits = new List<Unit>();
        private readonly List<Removal> _removeUnits = new List<Removal>();
    }
}

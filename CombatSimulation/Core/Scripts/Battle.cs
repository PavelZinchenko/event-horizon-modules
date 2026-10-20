using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using GameDiagnostics;
using Utilites;

namespace CombatSimulation.Core
{
    public sealed class Battle
    {
        public const float TickDuration = 0.02f;
        public const int PhysicsSubsteps = 8;

        // Legacy ShipRetreatEffect(7.0f): 7 s at 50 Hz.
        public const long RetreatTicks = 350;

        // Legacy Scene.FindFreePlace: up to 100 random candidates, at least 40 units from every enemy ship.
        internal const int SpawnAttempts = 100;
        internal const float SpawnMinEnemyDistance = 40f;

        // How far TryFindFreePosition may move a spawn to clear an overlap.
        internal const float SpawnSearchDistance = 40f;

        public Battle(BattleSetup setup)
        {
            Setup = setup ?? throw new ArgumentNullException(nameof(setup));
            World = new PhysicsWorld(new WorldSettings
            {
                Width = setup.Width,
                Height = setup.Height,
                Substeps = PhysicsSubsteps,
            });
            Roster = new Roster(setup);
        }

        public BattleSetup Setup { get; }
        public BattleId BattleId => Setup.BattleId;

        // The next tick to execute. The first Step processes tick 0, then leaves Tick at 1.
        public long Tick { get; private set; }

        public bool IsCompleted => Result.IsCompleted;

        // Physics handles and mutable state never leave Core's trusted assembly boundary.
        internal PhysicsWorld World { get; }
        internal Roster Roster { get; }
        internal BattleResult Result { get; private set; }

        // Ticks counted toward the time limit since the last reset.
        internal long TimerElapsedTicks { get; private set; }

        // Commands rejected at Submit or at stage 1. Phase 2 has no rejection events.
        internal int RejectedCommandCount { get; private set; }

        // A defect (such as a live duplicate EntityId) throws when set; otherwise it is logged and the operation fails.
        internal bool ThrowOnDefect { get; set; }

        public bool Submit(in RosterCommand command)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no commands.");

            if (command.BattleId != BattleId)
                return Reject(command, "wrong battle");
            if (command.TargetTick < Tick)
                return Reject(command, "past tick");
            if (!IsValidKind(command.Kind))
                return Reject(command, "unknown kind");
            if ((command.Kind == RosterCommandKind.Surrender || command.Kind == RosterCommandKind.KillAll) &&
                (command.Side < 0 || command.Side >= BattleSetup.SideCount))
                return Reject(command, "invalid side");
            if (!_pendingCommandIds.Add(command.CommandId))
                return Reject(command, "duplicate command ID");

            _commands.Insert(FindInsertIndex(command), command);
            return true;
        }

        public void Step()
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it cannot step.");
            if (Tick == long.MaxValue)
                throw new InvalidOperationException("Battle tick counter exhausted.");

            ApplyCommands();
            UpdateSystems();
            EvaluateModifiers();
            UpdateMotion();
            ApplySpawnRequests();
            RunQueries();
            StepPhysics();
            CollectContacts();
            ResolveContacts();
            ApplyDamage();
            MarkDeaths();
            ApplyStructureChanges();
            UpdateBattleFlow();
            Publish();
        }

        // Requests a death at stage 11 of the current tick. The damage stage adds its deaths to the same list.
        internal void QueueDeath(EntityId id) => _deathRequests.Add(id);

        internal ShipEntity FindShip(EntityId id) =>
            _entities.TryGet(id, out var handle) ? _ships.Get(handle) : null;

        internal bool TryRegister(EntityId id, EntityHandle handle)
        {
            if (_entities.TryAdd(id, handle)) return true;

            var message = $"Battle {BattleId}: entity ID {id} is already alive.";
            if (ThrowOnDefect) throw new InvalidOperationException(message);
            Trace.LogError(message);
            return false;
        }

        // Stage 1. Roster commands for this tick, in command ID order. Control frames come in step 3.
        private void ApplyCommands()
        {
            var count = 0;
            while (count < _commands.Count && _commands[count].TargetTick == Tick)
            {
                var command = _commands[count++];
                _pendingCommandIds.Remove(command.CommandId);
                ApplyRosterCommand(command);
            }

            if (count > 0) _commands.RemoveRange(0, count);
        }

        private void UpdateSystems() { }
        private void EvaluateModifiers() { }
        private void UpdateMotion() { }
        private void ApplySpawnRequests() { }
        private void RunQueries() { }
        private void StepPhysics() => World.Step(TickDuration);
        private void CollectContacts() { }
        private void ResolveContacts() { }
        private void ApplyDamage() { }

        // Stage 11. Marks deaths; stage 12 applies them.
        private void MarkDeaths()
        {
            for (var i = 0; i < _deathRequests.Count; ++i)
            {
                var ship = FindShip(_deathRequests[i]);
                if (ship != null) ship.PendingDeath = true;
            }

            _deathRequests.Clear();
        }

        // Stage 12. One batch, in roster order: deaths, then retreat completions, then surrender and kill-all.
        private void ApplyStructureChanges()
        {
            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var slot = slots[i];
                var ship = slot.ActiveShip;
                if (ship == null) continue;

                if (ship.PendingDeath)
                {
                    RemoveShip(ship);
                    slot.Destroy();
                }
                else if (ship.RetreatCompletionTick == Tick)
                {
                    RemoveShip(ship);
                    slot.CompleteRetreat();
                }
            }

            for (var side = 0; side < BattleSetup.SideCount; ++side)
            {
                if (!_destroySide[side]) continue;
                _destroySide[side] = false;

                for (var i = 0; i < slots.Count; ++i)
                {
                    var slot = slots[i];
                    if (slot.Side != side || slot.Status == RosterStatus.Destroyed) continue;
                    if (slot.ActiveShip != null) RemoveShip(slot.ActiveShip);
                    slot.Destroy();
                }
            }
        }

        // Stage 13. Elimination first, then the timer.
        private void UpdateBattleFlow()
        {
            var alive0 = Roster.HasAlive(0);
            var alive1 = Roster.HasAlive(1);
            if (!alive0 && !alive1)
            {
                Result = new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.Elimination, Tick);
                return;
            }
            if (!alive0 || !alive1)
            {
                Result = new BattleResult(BattleOutcome.Victory, alive0 ? 0 : 1, BattleEndReason.Elimination, Tick);
                return;
            }

            // B5: the timer does not advance while either side has no active ship.
            if (Roster.CountActive(0) == 0 || Roster.CountActive(1) == 0) return;

            ++TimerElapsedTicks;
            if (Setup.TimeLimitTicks > 0 && TimerElapsedTicks >= Setup.TimeLimitTicks)
                Result = new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.Timeout, Tick);
        }

        private void Publish()
        {
            // Later, publish the snapshot and events for Tick before advancing it.
            ++Tick;
        }

        private void ApplyRosterCommand(in RosterCommand command)
        {
            switch (command.Kind)
            {
                case RosterCommandKind.Deploy:
                    Deploy(command);
                    break;
                case RosterCommandKind.Retreat:
                    StartRetreat(command);
                    break;
                case RosterCommandKind.Surrender:
                    _destroySide[command.Side] = true;
                    break;
                case RosterCommandKind.KillAll:
                    _destroySide[1 - command.Side] = true;
                    break;
            }
        }

        private void Deploy(in RosterCommand command)
        {
            var slot = Roster.Find(command.RosterId);
            if (slot == null) { Reject(command, "unknown roster ID"); return; }
            if (slot.Status != RosterStatus.Ready) { Reject(command, $"slot is {slot.Status}"); return; }
            if (Roster.CountActive(slot.Side) >= Setup.MaxActiveShipsPerSide) { Reject(command, "side limit"); return; }

            var id = SpawnIdentity.CreateRoot(BattleId, slot.RosterId, slot.DeploymentCount + 1, Tick);
            var ship = _ships.Allocate();
            if (!TryRegister(id, ship.Handle))
            {
                _ships.Release(ship);
                Reject(command, "entity ID collision");
                return;
            }

            var definition = slot.Setup.Ship;
            var body = World.CreateBody(new BodyDef { Mass = definition.Mass });
            World.AddCircle(body, new Circle(Vec2.Zero, definition.HullRadius));
            PlaceSpawn(body, id, slot.Side);

            ship.Initialize(id, slot, body);
            slot.Activate(ship, ++_deploymentOrder);
            ResetTimer();
        }

        private void StartRetreat(in RosterCommand command)
        {
            var ship = FindShip(command.EntityId);
            if (ship == null) { Reject(command, "unknown entity"); return; }
            if (ship.Slot == null) { Reject(command, "not a roster ship"); return; }
            if (ship.RetreatCompletionTick != ShipEntity.NoRetreat) { Reject(command, "already retreating"); return; }

            ship.RetreatCompletionTick = Tick + RetreatTicks;
        }

        // Legacy Scene.FindFreePlace and ShipInfo.Create, with counter-based random.
        private void PlaceSpawn(BodyId body, EntityId id, int side)
        {
            var random = CounterRandom.Create(Setup.MatchSeed, id, Tick, RandomPurpose.SpawnPlacement);
            var angle = random.NextUint() % 360 * (float)(Math.PI / 180.0);

            var best = Vec2.Zero;
            var bestDistance = float.MinValue;
            for (var i = 0; i < SpawnAttempts; ++i)
            {
                var candidate = World.Torus.Wrap(new Vec2(random.NextFloat() * Setup.Width, random.NextFloat() * Setup.Height));
                var distance = DistanceToNearestShip(candidate, 1 - side);
                if (distance > bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
                if (distance >= SpawnMinEnemyDistance) break;
            }

            World.SetPose(body, best, angle);
            World.TryFindFreePosition(body, best, SpawnSearchDistance, out var position);
            World.SetPose(body, position, angle);
        }

        private float DistanceToNearestShip(Vec2 position, int side)
        {
            var nearest = float.MaxValue;
            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var ship = slots[i].ActiveShip;
                if (ship == null || ship.Side != side) continue;
                var distance = World.Torus.Delta(position, World.GetPosition(ship.Body)).Length;
                if (distance < nearest) nearest = distance;
            }
            return nearest;
        }

        private void RemoveShip(ShipEntity ship)
        {
            World.DestroyBody(ship.Body);
            _entities.Remove(ship.Id);
            _ships.Release(ship);
            ResetTimer();
        }

        // Owner (2026-10-20): any ship deployment or removal, on either side, resets the timer.
        private void ResetTimer() => TimerElapsedTicks = 0;

        private int FindInsertIndex(in RosterCommand command)
        {
            var low = 0;
            var high = _commands.Count;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                var other = _commands[middle];
                var before = other.TargetTick < command.TargetTick ||
                             (other.TargetTick == command.TargetTick && other.CommandId < command.CommandId);
                if (before) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private bool Reject(in RosterCommand command, string reason)
        {
            ++RejectedCommandCount;
            Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected {command}: {reason}.");
            return false;
        }

        private static bool IsValidKind(RosterCommandKind kind) =>
            kind == RosterCommandKind.Deploy || kind == RosterCommandKind.Retreat ||
            kind == RosterCommandKind.Surrender || kind == RosterCommandKind.KillAll;

        private readonly ShipStore _ships = new ShipStore();
        private readonly EntityRegistry _entities = new EntityRegistry();
        private readonly List<RosterCommand> _commands = new List<RosterCommand>();
        private readonly HashSet<ulong> _pendingCommandIds = new HashSet<ulong>();
        private readonly List<EntityId> _deathRequests = new List<EntityId>();
        private readonly bool[] _destroySide = new bool[BattleSetup.SideCount];
        private long _deploymentOrder;
    }
}

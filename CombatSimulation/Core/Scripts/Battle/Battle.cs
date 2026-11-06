using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using GameDiagnostics;

namespace CombatSimulation.Core
{
    public sealed partial class Battle
    {
        public const float TickDuration = 0.02f;
        public const int PhysicsSubsteps = 8;

        // Legacy ShipRetreatEffect(7.0f): 7 s at 50 Hz.
        public const long RetreatTicks = 350;

        // Legacy CombatManager: the 3 s cooldown before IsPlayerDefeated ends an OnlyOneShip battle.
        public const long ShipLostTicks = 150;

        public Battle(BattleSetup setup)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            _world = new BattleWorld(setup);
            _commands = new CommandQueue(setup.BattleId);
            _flow = new CommittedBattleFlow(setup);
            _targets = new TargetGrid(World.Torus);
            _tails = new WormTails(_world, this);
            _triggers = new BulletTriggers(_world, this);
            var bulletContacts = new BulletContactLedger();
            if (setup.HasRemoteOwners) _network = new BattleNetwork(_world, this, _tails, _triggers, bulletContacts);
            _spawns = new SpawnSystem(_world, this, _triggers, _network);
            DeviceContext = new DeviceContext(_world, _spawns, _tails, _network);
            _contacts = new ContactSystem(_world, this, _tails, _triggers, _network, bulletContacts);
            _damage = new DamageSystem(_world, this, _tails);

            _published = new BattleSnapshot(BattleId, Roster.Slots.Count);
            _writing = new BattleSnapshot(BattleId, Roster.Slots.Count);
            _published.Write(this, -1, World, Roster, _world.Events);
        }

        public BattleSetup Setup => _world.Setup;
        public IBattleObserver Observer { get; set; }
        public BattleId BattleId => Setup.BattleId;

        // The next tick to execute. The first Step processes tick 0, then leaves Tick at 1.
        public long Tick => _world.Tick;

        public bool IsCompleted => Result.IsCompleted;

        // The state and events of the last completed tick (tick -1 before the first step).
        // A reader finishes with it before the next Step returns.
        public BattleSnapshot Snapshot => _published;

        // Physics handles and mutable state never leave Core's trusted assembly boundary.
        internal PhysicsWorld World => _world.Physics;
        internal DeviceContext DeviceContext { get; }
        internal Roster Roster => _world.Roster;
        internal BattleResult Result { get; private set; }
        internal BattleResult CandidateResult { get; private set; }
        private readonly CommittedBattleFlow _flow;

        // Ticks counted toward the time limit since the last reset.
        internal long TimerElapsedTicks { get; private set; }

        // Commands rejected at Submit or at stage 1. Phase 2 has no rejection events.
        internal int RejectedCommandCount { get; private set; }

        internal bool ThrowOnDefect
        {
            get => _world.ThrowOnDefect;
            set => _world.ThrowOnDefect = value;
        }

        public bool Submit(in RosterCommand command)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no commands.");
            var reason = _commands.Add(command, Tick);
            return reason == null || Reject(command, reason);
        }

        // The entity is checked at stage 1 of the target tick: a frame may name a ship that deploys in that tick.
        public bool Submit(in ControlFrame frame)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no commands.");
            var reason = _commands.Add(frame, Tick);
            return reason == null || Reject(frame, reason);
        }

        // Call before Step with records from the reliable ordered stream. Sender identity comes from
        // Runtime's peer binding. Roster revision gaps are held independently of transport sequencing.
        public bool Submit(int senderOwner, in OwnerOperation operation)
        {
            RequireRunning();
            return _network != null && _network.Submit(senderOwner, operation);
        }

        public bool Submit(in InboundHit record)
        {
            RequireRunning();
            return _network != null && _network.Submit(record);
        }

        // Call after the operations the watermark covers. It takes effect once every operation sequence up to
        // OperationSequence has been submitted; older or repeated watermarks are ignored.
        public bool Submit(int senderOwner, in OwnerWatermark watermark)
        {
            RequireRunning();
            return _network != null && _network.Submit(senderOwner, watermark);
        }

        // Holds each valid correction on its remote entity until stage 1 after its tick. A correction that is not
        // newer than the held or last applied one for its entity is dropped. Returns the number held.
        public int Submit(int senderOwner, CorrectionBatch batch)
        {
            RequireRunning();
            if (_network != null) return _network.Submit(senderOwner, batch);
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            return 0;
        }

        // Call after Step: the batch describes the last completed tick. AppliedHitSequence acknowledges the hits
        // of peerOwner that this device has applied by then.
        public void WriteCorrections(int owner, int peerOwner, CorrectionBatch batch)
        {
            if (_network != null)
            {
                _network.WriteCorrections(owner, peerOwner, batch);
                return;
            }
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (!Setup.IsLocalOwner(owner)) throw new ArgumentException("The owner is not local.", nameof(owner));
            throw new ArgumentException("The peer is not a remote owner.", nameof(peerOwner));
        }

        private void RequireRunning()
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no records.");
        }

        public void Step()
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it cannot step.");
            if (Tick == long.MaxValue)
                throw new InvalidOperationException("Battle tick counter exhausted.");

            _network?.ApplyRemoteOperations();
            _network?.ApplyCorrections();
            ApplyCommands();
            UpdateSystems();
            EvaluateModifiers();
            UpdateMotion();
            ApplySpawnRequests();
            _contacts.RunQueries();
            ObserveProjectiles();
            StepPhysics();
            _contacts.Collect();
            _contacts.Resolve();
            _network?.AccumulateRemoteDamage();
            _damage.Apply();
            _damage.MarkDeaths();
            ApplyStructureChanges();
            UpdateBattleFlow();
            Publish();
        }

        internal CommittedBattleFlow Flow => _flow;
        internal TargetGrid Targets => _targets;

        // Contact and damage members for tails, the network part and tests (8b, decision 1).
        internal void QueueDeath(EntityId id) => _damage.QueueDeath(id);
        internal ContactResolver Contacts => _contacts.Resolver;
        internal RammingTracker Ramming => _contacts.Ramming;
        internal BeamQueries Beams => _contacts.Beams;
        internal void ApplyPush(ShipEntity target, Vec2 impulse) => _contacts.ApplyPush(target, impulse);
        internal Vec2 PushImpulse(in BulletEntity bullet, Vec2 target, float falloff) =>
            _contacts.PushImpulse(bullet, target, falloff);
        internal void ApplyShieldFirst(ref ResourcePool shield, ref ResourcePool armor, ref ResourcePool energy,
            float[] damage, in PendingEffects effects, ShipDefinition definition, float resistanceShare,
            float[] damageIgnoringShield) =>
            _damage.ApplyShieldFirst(ref shield, ref armor, ref energy, damage, effects, definition, resistanceShare,
                damageIgnoringShield);
        internal void ForgetContacts(EntityId id) => _contacts.Forget(id);

        // World members for the bullet and point-defense queries, the snapshot and tests (8b, decision 1).
        internal BulletStore Bullets => _world.Bullets;
        internal ShapeTable Shapes => _world.Shapes;
        internal TailSegmentStore Segments => _world.Segments;
        internal ShipEntity FindShip(EntityId id) => _world.FindShip(id);
        internal bool TryGetBullet(EntityId id, out int index)
        {
            index = -1;
            return _world.Entities.TryGet(id, out var handle) && handle.Kind == EntityKind.Bullet &&
                   _world.Bullets.TryGet(handle, out index);
        }
        internal int ShipEntryCount => _world.ShipEntryCount;
        internal ShipEntity GetShipEntry(int index) => _world.GetShipEntry(index);
        internal bool TryRegister(EntityId id, EntityHandle handle) => _world.TryRegister(id, handle);
        internal BattleEventAuthority Authority(int owner) => _world.Authority(owner);

        // Tail and trigger members for beam queries and tests (8b, decision 1).
        internal TailSegment FindSegment(EntityId id) => _tails.FindSegment(id);
        internal void QueueSegmentHit(TailSegment segment, float damage, Vec2 impulse) =>
            _tails.QueueSegmentHit(segment, damage, impulse);
        internal int TriggerParentCount => _triggers.TriggerParentCount;

        // Network members for tails, triggers, homing, the snapshot, Runtime and tests (8b, decision 1). Without remote
        // owners there is no network part (decision 3b): each member gives the value of a network part that received
        // and sent nothing.
        internal List<OwnerOperation> OutgoingOperations => _network?.OutgoingOperations ?? NoOperations;
        internal List<OwnerOperation> AppliedOperations => _network?.AppliedOperations ?? NoOperations;
        internal List<ResolvedHit> OutgoingHits => _network?.OutgoingHits.Records ?? NoHits;
        internal bool AcceptsInputFrom(EntityId entity, int owner) =>
            _network != null ? _network.AcceptsInputFrom(entity, owner) : _world.FindShip(entity)?.Owner == owner;
        internal long ConfirmedThrough(int owner) => _network?.ConfirmedThrough(owner) ?? -1;
        internal int PredictedRemovalCount => _network?.PredictedRemovalCount ?? 0;
        internal int PredictedHitCount => _network?.PredictedHitCount ?? 0;
        internal ulong AppliedHitSequence(int sender) => _network?.AppliedHitSequence(sender) ?? 0;
        internal void MissileCorrectionSent(EntityId id, long tick) => _network?.MissileCorrectionSent(id, tick);
        internal void CaptureMissileTarget(in BulletEntity bullet, EntityId target, bool targetIsSegment = false) =>
            _network?.CaptureMissileTarget(bullet, target, targetIsSegment);
        internal void CaptureRemoval(EntityId id, int owner, RemovalReason reason, DroneBayKey? bay = null) =>
            _network?.CaptureRemoval(id, owner, reason, bay);
        internal void CaptureBulletSpawn(in BulletEntity bullet, int source, int barrelIndex, uint sequence, long spawnTick,
            bool sendLaunch = false, Vec2 muzzle = default, float angle = 0f) =>
            _network?.CaptureBulletSpawn(bullet, source, barrelIndex, sequence, spawnTick, sendLaunch, muzzle, angle);
        internal void CaptureSegmentSpawn(ShipEntity ship, int deviceIndex, int number, EntityId id) =>
            _network?.CaptureSegmentSpawn(ship, deviceIndex, number, id);
        internal bool HasDueRemoval(EntityId id) => _network != null && _network.HasDueRemoval(id);
        internal bool RecordBulletContact(EntityId attacker, EntityId target, long tick) =>
            _contacts.Ledger.Record(attacker, target, tick);
        internal void ClaimBulletContact(EntityId attacker, int attackerOwner, int deviceIndex, in BulletEntity target,
            HitImpactKind kind, float amount) =>
            _network?.ClaimBulletContact(attacker, attackerOwner, deviceIndex, target, kind, amount);
        internal void SendBulletContact(in ResolvedHit hit) => _network?.SendBulletContact(hit);

        // True while received records apply: their changes are commits and send no records.
        private bool ApplyingRemoteOperation => _network != null && _network.ApplyingRemoteOperation;

        // The reasons an owner sends: Unconfirmed is local to a receiver.
        internal static bool IsSentRemovalReason(RemovalReason reason) =>
            reason >= RemovalReason.Death && reason <= RemovalReason.Disposal || reason == RemovalReason.Vanish;

        internal static float WrapAngle(float angle)
        {
            const float twoPi = (float)(2.0 * Math.PI);
            angle %= twoPi;
            if (angle > Math.PI) angle -= twoPi;
            else if (angle < -Math.PI) angle += twoPi;
            return angle;
        }

        public bool Submit(in SelfDestructCommand command)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no commands.");
            var reason = _commands.Add(command, Tick);
            return reason == null || Reject(command, reason);
        }

        public bool Submit(in ShipCommand command)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no commands.");
            var reason = _commands.Add(command, Tick);
            return reason == null || Reject(command, reason);
        }

        // Stage 1. Roster commands, self-destruct requests, ship commands, then control frames; each group in command
        // ID order.
        private void ApplyCommands()
        {
            _commands.TakeDue(Tick, _dueRosterCommands);
            foreach (var command in _dueRosterCommands)
                ApplyRosterCommand(command);

            _commands.TakeDue(Tick, _dueSelfDestructs);
            foreach (var command in _dueSelfDestructs)
            {
                var ship = _world.FindShip(command.EntityId);
                if (ship == null || !ship.IsDrone) Reject(command, "not a live drone");
                else if (!Setup.IsLocalOwner(ship.Owner)) Reject(command, "remote owner");
                else QueueDeath(ship.Id);
            }

            _commands.TakeDue(Tick, _dueShipCommands);
            foreach (var command in _dueShipCommands)
            {
                var ship = _world.FindShip(command.EntityId);
                if (ship == null) Reject(command, "unknown entity");
                else if (!Setup.IsLocalOwner(ship.Owner)) Reject(command, "remote owner");
                else if (command.Kind == ShipCommandKind.Vanish) ship.PendingVanish = true;
                else ChangeAllegiance(ship, command.Allegiance);
            }

            for (var i = 0; i < _world.ShipEntryCount; ++i)
                _world.GetShipEntry(i)?.BeginControlTick();

            _commands.TakeDue(Tick, _dueFrames);
            foreach (var frame in _dueFrames)
            {
                // A frame for a removed or unknown entity is dropped (commands, rule 4).
                var ship = _world.FindShip(frame.EntityId);
                if (ship == null) Reject(frame, "unknown entity");
                else ship.ApplyControls(frame);
            }
        }

        // Stage 2. Resources recharge first, before devices spend (legacy: stats update before systems).
        private void UpdateSystems()
        {
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null) continue;

                BarrelAiming.Update(_world, ship);
                ship.EngineHasEnergy = ship.Energy.Value > 0f;
                ship.Energy.Update(TickDuration);
                if (Setup.IsLocalOwner(ship.Owner))
                {
                    ship.Armor.Update(TickDuration);
                    ship.Shield.Update(TickDuration);
                }

                // Legacy ShipRetreatEffect.CanActivateSystem: a retreating ship activates no system.
                for (var d = 0; d < ship.DeviceCount; ++d)
                {
                    var device = ship.GetDevice(d);
                    if (ship.RetreatCompletionTick != ShipEntity.NoRetreat) device.UpdateRetreating(DeviceContext, ship);
                    else device.Update(DeviceContext, ship);
                }
            }

            _tails.UpdateDetachedSegments();
        }

        // Stage 3 (phase 5, step 7). Collects the status of each ship's running device uses. A change of NoContacts
        // changes the hull filters; physics follows at this tick's step.
        private void EvaluateModifiers()
        {
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null) continue;

                var status = new ShipStatus();
                for (var d = 0; d < ship.DeviceCount; ++d)
                    ship.GetDevice(d).AddStatus(DeviceContext, ship, ref status);

                var contactsChanged = ((ship.Status ^ status.Flags) & ShipStatusFlags.NoContacts) != 0;
                ship.Status = status.Flags;
                ship.BlockedDevices = status.BlockedDevices;
                ship.Tint = status.Tint;
                ship.ResistanceShare = status.ResistanceShare;
                if (contactsChanged) _world.SetHullFilters(ship);
            }
        }

        // Stage 4. The target grid takes the ship poses at the start of the step. A retreating ship has its engine
        // off and drifts (legacy ShipRetreatEffect); it stays a missile target. Then homing bullets steer.
        private void UpdateMotion()
        {
            _targets.Clear();
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship != null)
                    _targets.Add(ship.Id, ship.Allegiance, World.GetPosition(ship.Body), ship.Definition.HullRadius);
            }

            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null) continue;
                var input = ShipEngine.Input(ship);
                ship.StepEngineInput = input;
                ShipEngine.Update(World, ship.Body, input, TickDuration);

                // Physics reports a touch after the solve, so ramming uses the velocity from before the step.
                ship.PreStepVelocity = World.GetLinearVelocity(ship.Body);
            }
            _tails.UpdateSegmentMotion();

            // Each missile reads only its own state and the grid, so the storage order has no effect. Owner target
            // records of this tick apply first (P3). A rocket and a beam bolt read only their own state (phase 5, steps
            // 4a and 4e); a sticky bullet its own state and its target (step 4b).
            _network?.ApplyRemoteOperations(4);
            for (var i = 0; i < _world.Bullets.Capacity; ++i)
            {
                if (!_world.Bullets.IsAlive(i)) continue;
                ref var bullet = ref _world.Bullets[i];
                // A child spawned at stage 12 has not moved yet in its spawn tick.
                if (bullet.IsBeam || bullet.SpawnTick >= Tick) continue;
                if (bullet.Definition.IsHoming) HomingSteering.Update(this, ref bullet);
                else if (bullet.Definition.IsRocket) RocketSteering.Update(World, ref bullet);
                else if (bullet.Definition.IsSticky) StickyMine.Update(this, i);
                else if (bullet.Definition.IsHarpoon) Harpoons.Update(this, i);
                else if (bullet.Definition.IsBeamBolt) BeamBolts.Update(World, ref bullet, Tick);
            }
        }

        // Stage 5. The spawn system creates the requested spawns. Fire confirmations of this tick follow the local
        // spawns (operation stage 6), then unconfirmed bullets leave. Then auras grow (phase 5, step 4g).
        private void ApplySpawnRequests()
        {
            _network?.RecordSpawnSamples();
            _network?.ApplyRemoteOperations(5);
            _spawns.ApplyRequests();
            _network?.ApplyRemoteOperations(6);
            _network?.RemoveUnconfirmedBullets();
            _triggers.PruneTriggerParents();
            BarrelAiming.UpdateAttachments(_world);
            Auras.Update(_world, Tick);
            Waves.Update(_world, Tick);
        }

        private void StepPhysics() => World.Step(TickDuration);

        // Stage 12. One batch, in roster order: deaths, then retreat completions, then surrender and kill-all.
        // Drone deaths follow the roster batch, then tail segments. Bullets leave last, by ID. Attached beams die with
        // their ship.
        private void ApplyStructureChanges()
        {
            _network?.ApplyRemoteOperations(12);
            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var slot = slots[i];
                var ship = slot.ActiveShip;
                if (ship == null || !Setup.IsLocalOwner(ship.Owner)) continue;

                // RemoveShip releases the entity, so its ID is read first.
                var id = ship.Id;
                if (ship.PendingDeath)
                {
                    RemoveShip(ship, RemovalReason.Death);
                    slot.Destroy();
                    AddTransition(slot, RosterStatus.Active, RosterTransitionReason.Killed, id);
                }
                // A vanished ship retreats at once (D12) and keeps its damage for a redeployment.
                else if (ship.PendingVanish || ship.RetreatCompletionTick == Tick)
                {
                    var armorDeficit = ship.Armor.Deficit;
                    var shieldDeficit = ship.Shield.Deficit;
                    RemoveShip(ship, ship.PendingVanish ? RemovalReason.Vanish : RemovalReason.Retreat);
                    slot.CompleteRetreat(armorDeficit, shieldDeficit);
                    AddTransition(slot, RosterStatus.Active, RosterTransitionReason.Retreated, id);
                }
            }

            for (var side = 0; side < BattleSetup.SideCount; ++side)
            {
                if (!_destroySide[side]) continue;
                _destroySide[side] = false;

                for (var i = 0; i < slots.Count; ++i)
                {
                    var slot = slots[i];
                    if (slot.Side != side || slot.Status == RosterStatus.Destroyed || !Setup.IsLocalOwner(slot.Owner)) continue;

                    var previousStatus = slot.Status;
                    var id = default(EntityId);
                    if (slot.ActiveShip != null)
                    {
                        id = slot.ActiveShip.Id;
                        RemoveShip(slot.ActiveShip, RemovalReason.Death);
                    }
                    slot.Destroy();
                    AddTransition(slot, previousStatus, RosterTransitionReason.ForcedDestruction, id);
                }
            }

            // Mothership removal does not remove drones. Only their own damage or pilot command does.
            for (var i = 0; i < _world.Drones.Count;)
            {
                var drone = _world.Drones[i];
                if (!(drone.PendingDeath || drone.PendingVanish) || !Setup.IsLocalOwner(drone.Owner)) { ++i; continue; }
                RemoveShip(drone, drone.PendingDeath ? RemovalReason.Death : RemovalReason.Vanish);
                _world.Drones.RemoveAt(i);
            }

            _tails.RemoveDeadSegments();
            RemoveBullets();
        }

        // The owner runs the triggers of each bullet before its removal record, so the stream names the parent of every
        // child. A bullet with only Hit triggers due stays.
        private void RemoveBullets()
        {
            _world.Bullets.CollectRemovals(_bulletRemovals);
            for (var i = 0; i < _bulletRemovals.Count; ++i)
            {
                var index = _bulletRemovals[i];
                _triggers.RunStructureTriggers(index);
                ref readonly var bullet = ref _world.Bullets[index];
                if (bullet.PendingRemoval == RemovalReason.None) continue;
                _network?.OutgoingHits.RemoveAttacker(bullet.Id, Tick + 1);
                _world.Events.Add(BattleEvent.BulletRemoval(BattleId, Tick, _world.Events.Count, bullet, _world.Authority(bullet.Owner)));
                CaptureRemoval(bullet.Id, bullet.Owner, bullet.PendingRemoval);
                if (!bullet.Confirmed && Setup.HasRemoteOwners && !Setup.IsLocalOwner(bullet.Owner))
                    _network.RecordPredictedRemoval(bullet);
                ReleaseBullet(index);
            }
        }

        internal void ReleaseBullet(int index)
        {
            ref readonly var bullet = ref _world.Bullets[index];
            if (!Setup.IsLocalOwner(bullet.Owner)) _triggers.ForgetTriggerParentCopy(bullet);
            if (World.IsAlive(bullet.HarpoonRope)) World.DestroyJoint(bullet.HarpoonRope);
            if (!bullet.IsBeam) World.DestroyBullet(bullet.Body);
            _contacts.Ledger.Forget(bullet.Id);
            _world.Entities.Remove(bullet.Id);
            _world.Bullets.Release(index);
        }

        // Stage 13. Elimination first, then the timer.
        private void UpdateBattleFlow()
        {
            EvaluateResult();
            if (Result.IsCompleted)
                _world.Events.Add(BattleEvent.ResultEvent(BattleId, Tick, _world.Events.Count, Result));
        }

        private void EvaluateResult()
        {
            Result = _flow.Evaluate(Tick, _network?.ResultFrontier() ?? Tick, out var candidate, out var elapsed);
            CandidateResult = candidate;
            TimerElapsedTicks = elapsed;
        }

        // Stage 14. Writes the snapshot and the event batch of this tick, then swaps the buffers.
        private void Publish()
        {
            _network?.RecordBodySamples();
            _network?.RecordPredictedHits();
            _writing.Write(this, Tick, World, Roster, _world.Events);
            _world.Events.Clear();
            _network?.ClearTickRecords();

            var published = _writing;
            _writing = _published;
            _published = published;
            _world.AdvanceTick();
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
            if (!Setup.IsLocalOwner(slot.Owner)) { Reject(command, "remote owner"); return; }
            if (slot.Status != RosterStatus.Ready) { Reject(command, $"slot is {slot.Status}"); return; }
            if (Roster.CountActive(slot.Side) >= Setup.MaxActiveShips(slot.Side)) { Reject(command, "side limit"); return; }
            if (Setup.OnlyOneShip && slot.Side == Setup.PlayerSide && Roster.HasDestroyed(slot.Side))
            { Reject(command, "only one ship"); return; }

            if (!DeploySlot(slot)) Reject(command, "entity ID collision");
        }

        // Local deployment uses spawn placement. Received deployments use the owner's recorded pose.
        internal bool DeploySlot(RosterSlot slot)
        {
            var id = SpawnIdentity.CreateRoot(BattleId, slot.RosterId, slot.DeploymentCount + 1, Tick);
            var ship = _world.Ships.Allocate();
            if (!_world.TryRegister(id, ship.Handle))
            {
                _world.Ships.Release(ship);
                return false;
            }

            var definition = slot.Setup.Ship;
            var body = World.CreateBody(new BodyDef { Mass = definition.Mass });
            ship.Initialize(id, slot, body, Tick, Setup.DamageTypes.Count);

            _world.AddShipShapes(ship, CollisionFilters.Ship(ship.Allegiance));

            _spawns.PlaceRosterShip(body, id, slot.Side);
            slot.Activate(ship, ++_world.DeploymentOrder);

            _world.Events.Add(BattleEvent.Spawn(BattleId, Tick, _world.Events.Count, ship, _world.Authority(ship.Owner)));
            AddTransition(slot, RosterStatus.Ready, RosterTransitionReason.Deployed, id);
            return true;
        }

        private void StartRetreat(in RosterCommand command)
        {
            var ship = _world.FindShip(command.EntityId);
            if (ship == null) { Reject(command, "unknown entity"); return; }
            if (ship.Slot == null) { Reject(command, "not a roster ship"); return; }
            if (!Setup.IsLocalOwner(ship.Owner)) { Reject(command, "remote owner"); return; }
            if (ship.RetreatCompletionTick != ShipEntity.NoRetreat) { Reject(command, "already retreating"); return; }

            ship.RetreatCompletionTick = Tick + RetreatTicks;
            _network?.CaptureRetreat(ship);
        }

        internal void RemoveShip(ShipEntity ship, RemovalReason reason)
        {
            _tails.RemoveShipSegments(ship, reason);
            for (var i = 0; i < ship.DeviceCount; ++i) ship.GetDevice(i).Stop(DeviceContext);
            RemoveAttachedBullets(ship);
            _world.Events.Add(BattleEvent.Removal(BattleId, Tick, _world.Events.Count, ship, reason));
            if (ship.IsDrone) CaptureRemoval(ship.Id, ship.Owner, reason, ship.Bay.Key);
            for (var i = 0; i < ship.ShapeCount; ++i)
                _world.Shapes.Release(ship.GetShape(i));
            _contacts.Ramming.Forget(ship.Id);
            if (!Setup.IsLocalOwner(ship.Owner)) _network.ForgetPredictedHits(ship.Id);
            World.DestroyBody(ship.Body);
            _world.Entities.Remove(ship.Id);
            if (ship.IsDrone)
            {
                ship.Bay.RecordLoss();
                var mothership = ship.Bay.Slot.ActiveShip;
                mothership?.GetDevice(ship.Bay.Key.DeviceIndex).OnShipLost(DeviceContext, mothership, ship.Id, Tick);
            }
            _world.Ships.Release(ship);
        }

        // A bullet attached to the ship leaves with it, as a beam does, without triggers (owner, 2026-11-02). Its copies
        // on other devices leave with their ship copy too. A continuous beam left when its device stopped; a shot beam
        // that has not expired leaves here (legacy BulletCompositeDisposable, phase 5, step 4d).
        private void RemoveAttachedBullets(ShipEntity ship)
        {
            for (var i = 0; i < _world.Bullets.Capacity; ++i)
            {
                if (!_world.Bullets.IsAlive(i)) continue;
                ref var bullet = ref _world.Bullets[i];
                if (bullet.ParentId != ship.Id || !bullet.IsBeam && !bullet.Definition.AttachedToParent &&
                    !bullet.Definition.IsHarpoon) continue;
                bullet.SkipTriggers = true;
                if (bullet.PendingRemoval == RemovalReason.None) bullet.PendingRemoval = RemovalReason.Disposal;
            }
        }

        // D13: the ship, its hull filters and its live bullets and beams take the new allegiance. Legacy bullets read
        // their owner's side, so they change with it. Phase 5 friendly fire must keep its own allegiance here.
        private void ChangeAllegiance(ShipEntity ship, Allegiance allegiance)
        {
            if (ship.Allegiance == allegiance) return;
            _world.SetAllegiance(ship, allegiance);
            _network?.CaptureAllegiance(ship, allegiance);
        }

        internal void AddTransition(RosterSlot slot, RosterStatus previousStatus, RosterTransitionReason reason, EntityId id)
        {
            _world.Events.Add(BattleEvent.Transition(BattleId, Tick, _world.Events.Count, slot, previousStatus, reason, id,
                ApplyingRemoteOperation ? BattleEventAuthority.Committed : _world.Authority(slot.Owner)));
            if (!ApplyingRemoteOperation) _flow.Add(Tick, slot.Side, previousStatus, slot.Status);
            _network?.CaptureRoster(slot, previousStatus, reason, id);
        }

        private bool Reject(in RosterCommand command, string reason)
        {
            ++RejectedCommandCount;
            Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected {command}: {reason}.");
            return false;
        }

        private bool Reject(in ControlFrame frame, string reason)
        {
            ++RejectedCommandCount;
            Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected {frame}: {reason}.");
            return false;
        }

        private bool Reject(in ShipCommand command, string reason)
        {
            ++RejectedCommandCount;
            Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected {command}: {reason}.");
            return false;
        }

        private bool Reject(in SelfDestructCommand command, string reason)
        {
            ++RejectedCommandCount;
            Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected self-destruct #{command.CommandId} for {command.EntityId}: {reason}.");
            return false;
        }

        private readonly BattleWorld _world;
        private readonly CommandQueue _commands;
        private readonly List<RosterCommand> _dueRosterCommands = new List<RosterCommand>();
        private readonly List<SelfDestructCommand> _dueSelfDestructs = new List<SelfDestructCommand>();
        private readonly List<ShipCommand> _dueShipCommands = new List<ShipCommand>();
        private readonly List<ControlFrame> _dueFrames = new List<ControlFrame>();
        private readonly List<int> _bulletRemovals = new List<int>();
        // Null without remote owners (decision 3b).
        private readonly BattleNetwork _network;
        private readonly WormTails _tails;
        private readonly BulletTriggers _triggers;
        private readonly SpawnSystem _spawns;
        private readonly ContactSystem _contacts;
        private readonly DamageSystem _damage;
        private readonly TargetGrid _targets;
        private readonly bool[] _destroySide = new bool[BattleSetup.SideCount];
        private BattleSnapshot _published;
        private BattleSnapshot _writing;

        // The records of a battle without a network part. Nothing writes them.
        private static readonly List<OwnerOperation> NoOperations = new List<OwnerOperation>();
        private static readonly List<ResolvedHit> NoHits = new List<ResolvedHit>();
    }
}

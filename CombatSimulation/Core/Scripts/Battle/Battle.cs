using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using GameDiagnostics;
using Utilites;

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

        // Legacy Scene.FindFreePlace: up to 100 random candidates, at least 40 units from every enemy ship.
        internal const int SpawnAttempts = 100;
        internal const float SpawnMinEnemyDistance = 40f;

        // How far TryFindFreePosition may move a spawn to clear an overlap.
        internal const float SpawnSearchDistance = 40f;

        public Battle(BattleSetup setup)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            _world = new BattleWorld(setup);
            _commands = new CommandQueue(setup.BattleId);
            _flow = new CommittedBattleFlow(setup);
            _targets = new TargetGrid(World.Torus);
            _tails = new WormTails(_world, this);
            _triggers = new BulletTriggers(_world, this);
            if (setup.HasRemoteOwners) _network = new BattleNetwork(_world, this, _tails, _triggers, _bulletContacts);

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
            RunQueries();
            ObserveProjectiles();
            StepPhysics();
            CollectContacts();
            ResolveContacts();
            _network?.AccumulateRemoteDamage();
            ApplyDamage();
            MarkDeaths();
            ApplyStructureChanges();
            UpdateBattleFlow();
            Publish();
        }

        // Requests a death at stage 11 of the current tick. The damage stage adds its deaths to the same list.
        internal void QueueDeath(EntityId id) => _deathRequests.Add(id);

        // Stage 2: a device asks for a bullet; stage 5 creates it.
        internal void RequestSpawn(in SpawnRequest request) => _spawnRequests.Add(request);

        internal CommittedBattleFlow Flow => _flow;
        internal TargetGrid Targets => _targets;
        internal ContactResolver Contacts => _contacts;
        internal RammingTracker Ramming => _ramming;
        internal BeamQueries Beams => _beams;

        // World members for devices, AI, the snapshot and tests, which take the battle until DeviceContext (8b,
        // decision 1).
        internal BulletStore Bullets => _world.Bullets;
        internal ShapeTable Shapes => _world.Shapes;
        internal TailSegmentStore Segments => _world.Segments;
        internal ShipEntity FindShip(EntityId id) => _world.FindShip(id);
        internal int ShipEntryCount => _world.ShipEntryCount;
        internal ShipEntity GetShipEntry(int index) => _world.GetShipEntry(index);
        internal bool TryRegister(EntityId id, EntityHandle handle) => _world.TryRegister(id, handle);
        internal BattleEventAuthority Authority(int owner) => _world.Authority(owner);

        // Tail and trigger members for devices, beam queries and tests (8b, decision 1).
        internal TailSegment FindSegment(EntityId id) => _tails.FindSegment(id);
        internal int CountAttachedSegments(WormTailDevice device) => _tails.CountAttachedSegments(device);
        internal void GrowTail(ShipEntity ship, WormTailDevice device) => _tails.GrowTail(ship, device);
        internal void QueueSegmentHit(TailSegment segment, float damage, Vec2 impulse) =>
            _tails.QueueSegmentHit(segment, damage, impulse);
        internal int TriggerParentCount => _triggers.TriggerParentCount;

        // Network members for devices, tails, triggers, the snapshot, Runtime and tests (8b, decision 1). Without remote
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
        internal void CaptureDeviceUse(ShipEntity ship, int deviceIndex, bool started, long tick) =>
            _network?.CaptureDeviceUse(ship, deviceIndex, started, tick);
        internal void CaptureMissileTarget(in BulletEntity bullet, EntityId target) =>
            _network?.CaptureMissileTarget(bullet, target);
        internal void CaptureRemoval(EntityId id, int owner, RemovalReason reason, DroneBayKey? bay = null) =>
            _network?.CaptureRemoval(id, owner, reason, bay);
        internal void CaptureBulletSpawn(in BulletEntity bullet, int source, int barrelIndex, uint sequence, long spawnTick,
            bool sendLaunch = false, Vec2 muzzle = default, float angle = 0f) =>
            _network?.CaptureBulletSpawn(bullet, source, barrelIndex, sequence, spawnTick, sendLaunch, muzzle, angle);
        internal void CaptureSegmentSpawn(ShipEntity ship, int deviceIndex, int number, EntityId id) =>
            _network?.CaptureSegmentSpawn(ship, deviceIndex, number, id);
        internal bool HasDueRemoval(EntityId id) => _network != null && _network.HasDueRemoval(id);
        internal bool RecordBulletContact(EntityId attacker, EntityId target, long tick) =>
            _bulletContacts.Record(attacker, target, tick);
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
                    if (ship.RetreatCompletionTick != ShipEntity.NoRetreat) ship.GetDevice(d).UpdateRetreating(this, ship);
                    else ship.GetDevice(d).Update(this, ship);
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
                    ship.GetDevice(d).AddStatus(this, ship, ref status);

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
            // records of this tick apply first (P3).
            _network?.ApplyRemoteOperations(4);
            for (var i = 0; i < _world.Bullets.Capacity; ++i)
            {
                if (!_world.Bullets.IsAlive(i)) continue;
                ref var bullet = ref _world.Bullets[i];
                // A child spawned at stage 12 has not moved yet in its spawn tick.
                if (!bullet.IsBeam && bullet.Definition.IsHoming && bullet.SpawnTick < Tick)
                    HomingSteering.Update(this, ref bullet);
            }
        }

        // Stage 5. Roster ships first, then drones by ID; devices in index order. One spawn slot stays adjacent.
        // Fire confirmations of this tick follow the local spawns (operation stage 6), then unconfirmed bullets leave.
        private void ApplySpawnRequests()
        {
            _network?.RecordSpawnSamples();
            _network?.ApplyRemoteOperations(5);
            for (var i = 0; i < _spawnRequests.Count; ++i)
            {
                var request = _spawnRequests[i];
                var sequence = 0u;
                for (var j = i - 1; j >= 0 && _spawnRequests[j].HasSameSlot(request); --j) ++sequence;
                if (request.LaunchesShip) SpawnDrone(request, sequence);
                else if (request.Beam != null) SpawnBeam(request, sequence);
                else SpawnBullet(request, sequence);
            }

            _spawnRequests.Clear();
            _network?.ApplyRemoteOperations(6);
            _network?.RemoveUnconfirmedBullets();
            _triggers.PruneTriggerParents();
        }

        // A ship of the parent's bay inventory: a drone, or a clone (phase 5, step 7c).
        private void SpawnDrone(in SpawnRequest request, uint sequence)
        {
            var parent = request.Ship;
            if (!Setup.IsLocalOwner(parent.Owner)) return;
            var bay = parent.Slot.GetBay(request.DeviceIndex);
            var id = SpawnIdentity.CreateChild(parent.Id, Tick, (uint)request.DeviceIndex,
                (uint)request.BarrelIndex, sequence);
            var ship = _world.Ships.Allocate();
            if (!_world.TryRegister(id, ship.Handle))
            {
                _world.Ships.Release(ship);
                return;
            }

            var body = World.CreateBody(new BodyDef { Mass = bay.Drone.Mass });
            ship.InitializeDrone(id, bay, body, Tick, Setup.DamageTypes.Count);
            _world.AddShipShapes(ship, CollisionFilters.Drone(ship.Allegiance));
            if (parent.Definition.Devices[request.DeviceIndex] is ClonningCenterDefinition) PlaceClone(parent, ship);
            else
            {
                var origin = World.GetPosition(parent.Body);
                var angle = World.GetAngle(parent.Body);
                World.SetPose(body, origin, angle);
                World.TryFindFreePosition(body, origin, SpawnSearchDistance, out var position);
                World.SetPose(body, position, angle);
            }

            _world.AddDrone(ship);
            bay.RecordLaunch();
            _world.Events.Add(BattleEvent.Spawn(BattleId, Tick, _world.Events.Count, ship, _world.Authority(ship.Owner)));
            _network?.CaptureDroneSpawn(ship, parent.Id);
            parent.GetDevice(request.DeviceIndex).OnShipLaunched(this, parent, id, Tick);
        }

        // Legacy ClonningDevice.SpawnShip: the mothership position plus its scale in a random direction of whole
        // degrees, that direction as the rotation, and the mothership velocity. Legacy seeds System.Random from a hash
        // code; the core draws the direction counter-based, so it is new, not a copy (approved).
        private void PlaceClone(ShipEntity mothership, ShipEntity clone)
        {
            var random = CounterRandom.Create(Setup.MatchSeed, clone.Id, Tick, RandomPurpose.CloneSpawn);
            var angle = random.NextUint() % 360 * (float)(Math.PI / 180.0);
            var direction = Rot.FromAngle(angle);
            var position = World.GetPosition(mothership.Body) + mothership.Definition.Scale * new Vec2(direction.C, direction.S);
            World.SetPose(clone.Body, World.Torus.Wrap(position), angle);
            World.SetLinearVelocity(clone.Body, World.GetLinearVelocity(mothership.Body));
        }

        // Legacy BulletFactory.ConfigureBody at the muzzle pose, plus the Q1 recoil (owner, 2026-10-20; phase 4 E1):
        // Recoil x the bullet velocity relative to the ship (legacy BulletFactory.ApplyRecoil), at the muzzle.
        private void SpawnBullet(in SpawnRequest request, uint sequence)
        {
            var ship = request.Ship;
            var sample = _world.ReadSpawnSample(ship);
            var index = _world.CreateBullet(ship, request.DeviceIndex, request.BarrelIndex, sequence, request.Bullet,
                request.Spread, Tick, sample, out var muzzle, out var direction, out var angle);
            if (index < 0) return;

            var definition = request.Bullet;
            if (definition.Recoil > 0f && !definition.AttachedToParent)
            {
                var relative = definition.Speed * definition.StartingVelocityMultiplier * direction +
                               (definition.ParentVelocityEffect - 1f) * sample.LinearVelocity;
                World.ApplyImpulse(ship.Body, -definition.Recoil * relative, muzzle);
            }

            ref readonly var bullet = ref _world.Bullets[index];
            _world.Events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _world.Events.Count, bullet, _world.Authority(bullet.Owner)));
            CaptureBulletSpawn(bullet, bullet.DeviceIndex, (int)SpawnRequest.SpawnSlot(request.BarrelIndex), sequence, Tick,
                !definition.IsHoming, muzzle, angle);
            _triggers.RunCreatedTriggers(index);
        }

        private void SpawnBeam(in SpawnRequest request, uint sequence)
        {
            var index = _world.CreateBeam(request.Ship, request.Beam, sequence, Tick);
            if (index < 0) return;
            ref readonly var beam = ref _world.Bullets[index];
            _world.Events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _world.Events.Count, beam, _world.Authority(beam.Owner)));
            CaptureBulletSpawn(beam, beam.DeviceIndex, request.BarrelIndex, sequence, Tick);
        }

        private void RunQueries()
        {
            _beams.Run(this);
            _pointDefense.Run(this);
        }
        private void StepPhysics() => World.Step(TickDuration);

        // Stage 8. Every source adds its candidates; the resolver sorts them by the contact key.
        private void CollectContacts()
        {
            _contacts.BeginStep();
            _beams.Collect(_contacts);
            _pointDefense.Collect(_contacts);
            _world.Bullets.CollectHits(World, _world.Shapes, _world.Ships, _world.Segments, _contacts);
            _ramming.Collect(World, _world.Shapes, _world.Ships, _world.Segments, _contacts, Setup.DamageTypes.RammingDamageType);

            var duplicates = _contacts.Sort();
            if (duplicates > 0)
                _world.ReportDefect($"Battle {BattleId}, tick {Tick}: {duplicates} contact candidates share a key.");
        }

        // Stage 9. Ship contacts belong to the attacker; bullet damage belongs to the target bullet.
        private void ResolveContacts()
        {
            _contacts.Resolve();
            _beams.Complete(this, _contacts);
            _network?.OutgoingHits.BeginContacts();

            var accepted = _contacts.Accepted;
            for (var i = 0; i < accepted.Length; ++i)
            {
                ref readonly var hit = ref accepted[i];
                ObserveContact(hit);
                if (hit.Kind == ContactKind.Disarm)
                {
                    if (!_world.Bullets.TryGet(hit.TargetHandle, out var disarmed)) continue;
                    // Legacy: point defense is a contact, so Hit triggers run before Disarmed ones. A bullet that cannot
                    // be disarmed counts each zap once, whether this device or a remote one saw it.
                    if (hit.TargetKeepsFlying)
                    {
                        if (RecordBulletContact(hit.Attacker, hit.Target, Tick)) _triggers.AddPendingHit(disarmed);
                    }
                    else
                    {
                        if (_world.Bullets[disarmed].PendingRemoval == RemovalReason.None) _triggers.AddPendingHit(disarmed);
                        _world.Bullets.MarkRemoval(hit.TargetHandle, RemovalReason.Disposal);
                    }
                    _world.Events.Add(BattleEvent.DisarmEvent(BattleId, Tick, _world.Events.Count, hit,
                        _world.Authority(_world.Bullets[disarmed].Owner)));
                    var defender = _world.Ships.Get(hit.AttackerHandle);
                    if (defender != null)
                        ClaimBulletContact(hit.Attacker, defender.Owner, hit.AttackerDeviceIndex, _world.Bullets[disarmed],
                            HitImpactKind.Disarm, 0f);
                    continue;
                }
                if (hit.Kind != ContactKind.Hit) continue;

                // A projectile stops on either copy. Its removal has its own authority flag.
                if (hit.Source != ContactSource.Ray && _world.Bullets.TryGet(hit.AttackerHandle, out var stopped))
                {
                    if (_world.Bullets[stopped].PendingRemoval == RemovalReason.None) _triggers.AddPendingHit(stopped);
                    if (!hit.AttackerPassThrough) _world.Bullets.MarkRemoval(hit.AttackerHandle, RemovalReason.Death);
                }

                BattleEventAuthority authority;
                if (hit.TargetHandle.Kind == EntityKind.Bullet)
                {
                    if (!_world.Bullets.TryGet(hit.TargetHandle, out var index)) continue;
                    ref readonly var targetBullet = ref _world.Bullets[index];
                    authority = _world.Authority(targetBullet.Owner);
                    var attackerBullet = _world.Bullets.TryGet(hit.AttackerHandle, out var attackerIndex);
                    var attackerOwner = attackerBullet ? _world.Bullets[attackerIndex].Owner : _world.Ships.Get(hit.AttackerHandle)?.Owner ?? -1;
                    var ray = hit.Source == ContactSource.Ray;
                    // A ray and damage over time deal one tick per contact; any other contact deals its damage once.
                    var perTick = ray || (attackerBullet && !_world.Bullets[attackerIndex].IsBeam &&
                                          _world.Bullets[attackerIndex].Definition.Impact == BulletImpact.OverTime);
                    if (authority == BattleEventAuthority.Committed)
                    {
                        // A remote attacker's contact counts once, whether this device or the attacker's owner saw it.
                        if (Setup.IsLocalOwner(attackerOwner) || RecordBulletContact(hit.Attacker, hit.Target, perTick ? Tick : -1))
                        {
                            _world.Bullets.AddDamage(hit.TargetHandle, hit.Damage);
                            _triggers.AddPendingHit(index);
                        }
                        // A remote projectile that stops at a local bullet stops on its owner too.
                        if (attackerBullet && !hit.AttackerPassThrough && !ray)
                            ClaimBulletContact(hit.Target, targetBullet.Owner, -1, _world.Bullets[attackerIndex],
                                HitImpactKind.Stop, 0f);
                    }
                    else
                    {
                        // Resolver walk damage decides predicted removal without changing remote hit points.
                        if (_contacts.GetStopTime(hit.TargetHandle) < float.PositiveInfinity)
                            _world.Bullets.MarkRemoval(hit.TargetHandle, RemovalReason.Death);
                        if (attackerOwner >= 0)
                            ClaimBulletContact(hit.Attacker, attackerOwner, hit.AttackerDeviceIndex, targetBullet,
                                perTick ? HitImpactKind.DamageOverTime : HitImpactKind.Impact, hit.Damage);
                    }
                }
                else if (hit.TargetHandle.Kind == EntityKind.Segment)
                {
                    var segment = _world.Segments.Get(hit.TargetHandle);
                    if (segment == null) continue;
                    authority = _world.Authority(segment.Owner);
                    _tails.ResolveSegmentHit(hit, segment);
                }
                else
                {
                    var target = _world.Ships.Get(hit.TargetHandle);
                    if (target == null) continue;
                    int owner, deviceIndex;
                    HitImpactKind kind;
                    var amount = hit.Damage;
                    var damage = hit.Damage;
                    var damageType = hit.DamageType;
                    BulletDefinition projectile = null;
                    var impulse = Vec2.Zero;
                    var falloff = 1f;
                    var shape = hit.TargetShapeOrder;
                    if (_world.Bullets.TryGet(hit.AttackerHandle, out var index))
                    {
                        ref readonly var attacker = ref _world.Bullets[index];
                        owner = attacker.Owner;
                        deviceIndex = attacker.DeviceIndex;
                        kind = attacker.IsBeam ? HitImpactKind.DamageOverTime :
                            attacker.Definition.IsMissile ? HitImpactKind.Impact : HitImpactKind.Energy;
                        if (attacker.IsBeam)
                        {
                            amount = attacker.BeamDefinition.DamagePerSecond;
                        }
                        else if (attacker.Definition.Impact == BulletImpact.OverTime)
                        {
                            // One damage type at a fixed rate (BulletDefinition). The record names shape 0, so the
                            // interval stays one contact while the bullet moves over the hull parts.
                            kind = HitImpactKind.DamageOverTime;
                            amount = attacker.Definition.Damage;
                            damage = amount * TickDuration;
                            damageType = attacker.Definition.DamageType;
                            shape = 0;
                        }
                        else
                        {
                            // The candidate holds the total; a ship takes each damage effect with its own type.
                            projectile = attacker.Definition;
                            if (projectile.HasFalloff) falloff = hit.Falloff;
                            amount = damage = BulletStore.EffectDamage(projectile, projectile.Damage,
                                projectile.DamageFalloff, falloff);
                            damageType = projectile.DamageType;
                            impulse = PushImpulse(attacker, World.GetPosition(target.Body), falloff);
                        }
                    }
                    else
                    {
                        var attacker = _world.Ships.Get(hit.AttackerHandle);
                        if (attacker == null) continue;
                        owner = attacker.Owner;
                        deviceIndex = hit.AttackerDeviceIndex;
                        kind = hit.PointDefense != null ? HitImpactKind.Energy : HitImpactKind.Impact;
                    }
                    authority = _world.Authority(owner);
                    if (authority == BattleEventAuthority.Committed)
                    {
                        // A remote target's copy takes the same damage and push as a prediction (N3); it never dies of
                        // it. The push on a remote target travels in the first record (phase 4 E4).
                        var remote = !Setup.IsLocalOwner(target.Owner);
                        target.AddPendingDamage(damageType, damage);
                        if (remote)
                            _network.OutgoingHits.Add(new ResolvedHit(BattleId, Tick, hit.Attacker, owner, deviceIndex,
                                hit.Target, target.Owner, shape, HitContactState.Start, kind, amount,
                                damageType: (byte)damageType, impulse: impulse));
                        if (projectile != null)
                        {
                            var extra = projectile.ExtraDamage;
                            for (var e = 0; e < extra.Count; ++e)
                            {
                                var extraAmount = BulletStore.EffectDamage(projectile, extra[e].Amount, extra[e].Falloff,
                                    falloff);
                                target.AddPendingDamage(extra[e].DamageType, extraAmount);
                                if (remote)
                                    _network.OutgoingHits.Add(new ResolvedHit(BattleId, Tick, hit.Attacker, owner, deviceIndex,
                                        hit.Target, target.Owner, hit.TargetShapeOrder, HitContactState.Start, kind,
                                        extraAmount, damageType: (byte)extra[e].DamageType));
                            }
                            if (impulse.X != 0f || impulse.Y != 0f) ApplyPush(target, impulse);
                        }
                    }
                }

                _world.Events.Add(BattleEvent.HitEvent(BattleId, Tick, _world.Events.Count, hit, authority));
            }
            _network?.OutgoingHits.CompleteContacts(Tick);
        }

        // Legacy PushAction: the impulse acts at the target's origin, so it never turns the target.
        internal void ApplyPush(ShipEntity target, Vec2 impulse) =>
            World.ApplyImpulse(target.Body, impulse, World.GetPosition(target.Body));

        // Legacy PushAction (Push x the bullet velocity) plus RadialPushAction (phase 5, step 3): away from the bullet
        // centre toward the target's origin. Legacy applies the radial impulse at the bullet centre; that point lies on
        // the same line through the origin, so the torque is the same.
        internal Vec2 PushImpulse(in BulletEntity bullet, Vec2 target, float falloff)
        {
            var definition = bullet.Definition;
            var impulse = Vec2.Zero;
            if (definition.Push != 0f) impulse = definition.Push * World.GetBulletVelocity(bullet.Body);
            var radial = definition.RadialPush + definition.RadialPushWithFalloff * falloff;
            if (radial != 0f)
                impulse += radial * World.Torus.Delta(World.GetBulletPosition(bullet.Body), target).Normalized;
            return impulse;
        }

        // Stage 10. Tail segments first: the damage a tail cannot take passes to its ship (phase 5, step 7d). Then one
        // pass, shield first, then armor (legacy Impact.ApplyShield and ShipStats.ApplyDamage).
        // Received hits add to a local target's pending damage. A remote target's pending damage is only the damage
        // this device sent it (N3): a prediction that corrections replace.
        private void ApplyDamage()
        {
            _tails.ApplySegmentHits();
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null || !ship.HasPendingDamage) continue;

                ApplyShieldFirst(ship);
                ship.ClearPendingDamage();
            }

            DrainPlayerShips();
            ReactToLethalDamage();
            _world.Bullets.ApplyDamage(Setup);
        }

        private void ApplyShieldFirst(ShipEntity ship) =>
            ApplyShieldFirst(ref ship.Shield, ref ship.Armor, ref ship.Energy, ship.PendingDamage, ship.Definition,
                ship.ResistanceShare);

        // Legacy Impact.ApplyShield and ShipStats.ApplyDamage, by damage type (D4). The shield meets the types group by
        // group in shield order; each type's load is its damage less its shield resistance. A group larger than the
        // shield left keeps the same share of each type. Armor then takes what passed, less its armor resistance, and
        // energy gains its share of what passed. damage holds raw amounts by type; this changes it to what passed. A
        // resistance share (ShipStatus) moves each armor resistance toward the ceiling of its type.
        internal void ApplyShieldFirst(ref ResourcePool shield, ref ResourcePool armor, ref ResourcePool energy,
            float[] damage, ShipDefinition definition, float resistanceShare)
        {
            var types = Setup.DamageTypes;
            var walk = types.ShieldWalk;
            var left = shield.Max > 0f && shield.Value > 0f ? shield.Value : 0f;
            var absorbed = 0f;
            for (var start = 0; start < walk.Length && left > 0f;)
            {
                var order = types[walk[start]].ShieldOrder;
                var end = start;
                var load = 0f;
                for (; end < walk.Length && types[walk[end]].ShieldOrder == order; ++end)
                    load += damage[walk[end]] * (1f - definition.Resistance(walk[end]).Shield);

                if (load > 0f)
                {
                    if (load <= left)
                    {
                        for (var i = start; i < end; ++i) damage[walk[i]] = 0f;
                        absorbed += load;
                        left -= load;
                    }
                    else
                    {
                        for (var i = start; i < end; ++i) damage[walk[i]] -= left * damage[walk[i]] / load;
                        absorbed += left;
                        left = 0f;
                    }
                }
                start = end;
            }
            if (absorbed > 0f) shield.Consume(absorbed);

            var armorDamage = 0f;
            var energyGain = 0f;
            for (var type = 0; type < damage.Length; ++type)
            {
                if (!(damage[type] > 0f)) continue;
                var resistance = definition.Resistance(type);
                var armorResistance = resistanceShare > 0f
                    ? resistance.Armor + resistanceShare * (types[type].ResistanceCeiling - resistance.Armor)
                    : resistance.Armor;
                armorDamage += damage[type] * (1f - armorResistance);
                energyGain += damage[type] * resistance.EnergyGain;
            }
            armor.Consume(armorDamage);
            // A gain does not restart the energy delay (legacy Energy.Get with a negative amount).
            if (energyGain > 0f) energy.Consume(-energyGain);
        }

        // End of stage 10 (phase 5, step 7, H3): every hit of the tick counts first. A device may save a local ship
        // that has no armor left; stage 11 then finds none to mark. A remote copy follows its owner's record.
        private void ReactToLethalDamage()
        {
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null || !Setup.IsLocalOwner(ship.Owner) || ship.Armor.Value > 0f) continue;
                for (var d = 0; d < ship.DeviceCount; ++d)
                    if (ship.GetDevice(d).OnLethalDamage(this, ship)) break;
            }
        }

        // Legacy CombatTimer, DrainPlayerHp (TM3): after the limit, each active player roster ship loses k % of its
        // maximum armor and of its maximum shield per second, k being the whole overtime seconds already passed.
        // No resistances; both restart their delay. It reads the timer of the previous tick. Only the owner drains;
        // a remote copy follows through corrections. Drones are not drained.
        private void DrainPlayerShips()
        {
            if (Setup.TimerMode != TimerMode.DrainPlayerHp || Setup.TimeLimitTicks <= 0) return;
            var overtime = TimerElapsedTicks - Setup.TimeLimitTicks;
            if (overtime < DrainTicksPerSecond) return;

            var fraction = overtime / DrainTicksPerSecond * 0.01f * TickDuration;
            foreach (var slot in Roster.Slots)
            {
                var ship = slot.ActiveShip;
                if (slot.Side != Setup.PlayerSide || ship == null || !Setup.IsLocalOwner(ship.Owner)) continue;
                ship.Armor.Consume(fraction * ship.Armor.Max);
                ship.Shield.Consume(fraction * ship.Shield.Max);
            }
        }

        private const long DrainTicksPerSecond = 50;

        // Stage 11. Marks deaths and bullet expiry; stage 12 applies them. Armor 0 is a death; so are missile hit points 0.
        private void MarkDeaths()
        {
            _world.Bullets.MarkDeaths(Tick, Setup);

            for (var i = 0; i < _deathRequests.Count; ++i)
            {
                var ship = _world.FindShip(_deathRequests[i]);
                if (ship != null && Setup.IsLocalOwner(ship.Owner)) ship.PendingDeath = true;
            }

            _deathRequests.Clear();

            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship != null && Setup.IsLocalOwner(ship.Owner) && ship.Armor.Value <= 0f) ship.PendingDeath = true;
            }
        }

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

        // A removed segment leaves the ramming tracker and the bullet contact list.
        internal void ForgetContacts(EntityId id)
        {
            _ramming.Forget(id);
            _bulletContacts.Forget(id);
        }

        internal void ReleaseBullet(int index)
        {
            ref readonly var bullet = ref _world.Bullets[index];
            if (!Setup.IsLocalOwner(bullet.Owner)) _triggers.ForgetTriggerParentCopy(bullet);
            if (!bullet.IsBeam) World.DestroyBullet(bullet.Body);
            _bulletContacts.Forget(bullet.Id);
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

            PlaceSpawn(body, id, slot.Side);
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

        internal void RemoveShip(ShipEntity ship, RemovalReason reason)
        {
            _tails.RemoveShipSegments(ship, reason);
            for (var i = 0; i < ship.DeviceCount; ++i) ship.GetDevice(i).Stop(this);
            RemoveAttachedBullets(ship);
            _world.Events.Add(BattleEvent.Removal(BattleId, Tick, _world.Events.Count, ship, reason));
            if (ship.IsDrone) CaptureRemoval(ship.Id, ship.Owner, reason, ship.Bay.Key);
            for (var i = 0; i < ship.ShapeCount; ++i)
                _world.Shapes.Release(ship.GetShape(i));
            _ramming.Forget(ship.Id);
            if (!Setup.IsLocalOwner(ship.Owner)) _network.ForgetPredictedHits(ship.Id);
            World.DestroyBody(ship.Body);
            _world.Entities.Remove(ship.Id);
            if (ship.IsDrone)
            {
                ship.Bay.RecordLoss();
                var mothership = ship.Bay.Slot.ActiveShip;
                mothership?.GetDevice(ship.Bay.Key.DeviceIndex).OnShipLost(this, mothership, ship.Id, Tick);
            }
            _world.Ships.Release(ship);
        }

        // A bullet attached to the ship leaves with it, as a beam does, without triggers (owner, 2026-11-02). Its copies
        // on other devices leave with their ship copy too.
        private void RemoveAttachedBullets(ShipEntity ship)
        {
            for (var i = 0; i < _world.Bullets.Capacity; ++i)
            {
                if (!_world.Bullets.IsAlive(i)) continue;
                ref var bullet = ref _world.Bullets[i];
                if (bullet.IsBeam || bullet.ParentId != ship.Id || !bullet.Definition.AttachedToParent) continue;
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
        private readonly BeamQueries _beams = new BeamQueries();
        private readonly PointDefenseQueries _pointDefense = new PointDefenseQueries();
        private readonly List<SpawnRequest> _spawnRequests = new List<SpawnRequest>();
        private readonly List<int> _bulletRemovals = new List<int>();
        // Null without remote owners (decision 3b).
        private readonly BattleNetwork _network;
        private readonly BulletContactLedger _bulletContacts = new BulletContactLedger();
        private readonly WormTails _tails;
        private readonly BulletTriggers _triggers;
        private readonly ContactResolver _contacts = new ContactResolver();
        private readonly RammingTracker _ramming = new RammingTracker();
        private readonly TargetGrid _targets;
        private readonly List<EntityId> _deathRequests = new List<EntityId>();
        private readonly bool[] _destroySide = new bool[BattleSetup.SideCount];
        private BattleSnapshot _published;
        private BattleSnapshot _writing;

        // The records of a battle without a network part. Nothing writes them.
        private static readonly List<OwnerOperation> NoOperations = new List<OwnerOperation>();
        private static readonly List<ResolvedHit> NoHits = new List<ResolvedHit>();
    }
}

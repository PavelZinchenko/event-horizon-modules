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

        // Legacy Scene.FindFreePlace: up to 100 random candidates, at least 40 units from every enemy ship.
        internal const int SpawnAttempts = 100;
        internal const float SpawnMinEnemyDistance = 40f;

        // How far TryFindFreePosition may move a spawn to clear an overlap.
        internal const float SpawnSearchDistance = 40f;

        public Battle(BattleSetup setup)
        {
            Setup = setup ?? throw new ArgumentNullException(nameof(setup));
            _replayDamage = new float[setup.DamageTypes.Count];
            World = new PhysicsWorld(new WorldSettings
            {
                Width = setup.Width,
                Height = setup.Height,
                Substeps = PhysicsSubsteps,
            });
            Roster = new Roster(setup);
            _flow = new CommittedBattleFlow(setup);
            _targets = new TargetGrid(World.Torus);

            _published = new BattleSnapshot(BattleId, Roster.Slots.Count);
            _writing = new BattleSnapshot(BattleId, Roster.Slots.Count);
            _published.Write(this, -1, World, Roster, _events);
        }

        public BattleSetup Setup { get; }
        public IBattleObserver Observer { get; set; }
        public BattleId BattleId => Setup.BattleId;

        // The next tick to execute. The first Step processes tick 0, then leaves Tick at 1.
        public long Tick { get; private set; }

        public bool IsCompleted => Result.IsCompleted;

        // The state and events of the last completed tick (tick -1 before the first step).
        // A reader finishes with it before the next Step returns.
        public BattleSnapshot Snapshot => _published;

        // Physics handles and mutable state never leave Core's trusted assembly boundary.
        internal PhysicsWorld World { get; }
        internal Roster Roster { get; }
        internal BattleResult Result { get; private set; }
        internal BattleResult CandidateResult { get; private set; }
        private readonly CommittedBattleFlow _flow;

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

        // The entity is checked at stage 1 of the target tick: a frame may name a ship that deploys in that tick.
        public bool Submit(in ControlFrame frame)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no commands.");

            if (frame.BattleId != BattleId)
                return Reject(frame, "wrong battle");
            if (frame.TargetTick < Tick)
                return Reject(frame, "past tick");
            if (frame.EntityId.IsNull)
                return Reject(frame, "no entity");
            if (!(frame.Throttle >= 0f && frame.Throttle <= 1f))
                return Reject(frame, "throttle out of range");
            if (frame.HasCourse && (float.IsNaN(frame.Course) || float.IsInfinity(frame.Course)))
                return Reject(frame, "invalid course");
            if (_pendingCommandIds.Contains(frame.CommandId))
                return Reject(frame, "duplicate command ID");
            if (!_pendingFrameKeys.Add(new FrameKey(frame.EntityId, frame.TargetTick)))
                return Reject(frame, "second frame for the entity and tick");

            _pendingCommandIds.Add(frame.CommandId);
            _frames.Insert(FindInsertIndex(frame), frame);
            return true;
        }

        public void Step()
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it cannot step.");
            if (Tick == long.MaxValue)
                throw new InvalidOperationException("Battle tick counter exhausted.");

            ApplyRemoteOperations();
            ApplyCorrections();
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
            AccumulateRemoteDamage();
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

            ReportDefect($"Battle {BattleId}: entity ID {id} is already alive.");
            return false;
        }

        // Stage 2: a device asks for a bullet; stage 5 creates it.
        internal void RequestSpawn(in SpawnRequest request) => _spawnRequests.Add(request);

        internal BulletStore Bullets => _bullets;
        internal List<ResolvedHit> OutgoingHits => _outgoingHits.Records;
        internal BattleEventAuthority Authority(int owner) => Setup.IsLocalOwner(owner)
            ? BattleEventAuthority.Committed : BattleEventAuthority.Predicted;
        internal TargetGrid Targets => _targets;
        internal ShapeTable Shapes => _shapes;
        internal ContactResolver Contacts => _contacts;
        internal RammingTracker Ramming => _ramming;
        internal BeamQueries Beams => _beams;

        // Roster entries can be empty; live drones follow them in stable entity ID order.
        internal int ShipEntryCount => Roster.Slots.Count + _drones.Count;
        internal ShipEntity GetShipEntry(int index) => index < Roster.Slots.Count
            ? Roster.Slots[index].ActiveShip : _drones[index - Roster.Slots.Count];

        public bool Submit(in SelfDestructCommand command)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result; it accepts no commands.");
            if (command.BattleId != BattleId) return Reject(command, "wrong battle");
            if (command.TargetTick < Tick) return Reject(command, "past tick");
            if (command.EntityId.IsNull) return Reject(command, "no entity");
            if (!_pendingCommandIds.Add(command.CommandId)) return Reject(command, "duplicate command ID");

            var index = 0;
            while (index < _selfDestructs.Count &&
                (_selfDestructs[index].TargetTick < command.TargetTick ||
                 (_selfDestructs[index].TargetTick == command.TargetTick && _selfDestructs[index].CommandId < command.CommandId)))
                ++index;
            _selfDestructs.Insert(index, command);
            return true;
        }

        // Stage 1. Roster commands, self-destruct requests, then control frames; each group in command ID order.
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

            count = 0;
            while (count < _selfDestructs.Count && _selfDestructs[count].TargetTick == Tick)
            {
                var command = _selfDestructs[count++];
                _pendingCommandIds.Remove(command.CommandId);
                var ship = FindShip(command.EntityId);
                if (ship == null || !ship.IsDrone) Reject(command, "not a live drone");
                else if (!Setup.IsLocalOwner(ship.Owner)) Reject(command, "remote owner");
                else QueueDeath(ship.Id);
            }
            if (count > 0) _selfDestructs.RemoveRange(0, count);

            for (var i = 0; i < ShipEntryCount; ++i)
                GetShipEntry(i)?.BeginControlTick();

            count = 0;
            while (count < _frames.Count && _frames[count].TargetTick == Tick)
            {
                var frame = _frames[count++];
                _pendingCommandIds.Remove(frame.CommandId);
                _pendingFrameKeys.Remove(new FrameKey(frame.EntityId, frame.TargetTick));

                // A frame for a removed or unknown entity is dropped (commands, rule 4).
                var ship = FindShip(frame.EntityId);
                if (ship == null) Reject(frame, "unknown entity");
                else ship.ApplyControls(frame);
            }

            if (count > 0) _frames.RemoveRange(0, count);
        }

        // Stage 2. Resources recharge first, before devices spend (legacy: stats update before systems).
        private void UpdateSystems()
        {
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
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
                    if (ship.RetreatCompletionTick != ShipEntity.NoRetreat) ship.GetDevice(d).Deactivate(this);
                    else ship.GetDevice(d).Update(this, ship);
                }
            }
        }

        private void EvaluateModifiers() { }

        // Stage 4. The target grid takes the ship poses at the start of the step. A retreating ship has its engine
        // off and drifts (legacy ShipRetreatEffect); it stays a missile target. Then homing bullets steer.
        private void UpdateMotion()
        {
            _targets.Clear();
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship != null)
                    _targets.Add(ship.Id, ship.Side, World.GetPosition(ship.Body), ship.Definition.HullRadius);
            }

            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship == null) continue;
                var input = ShipEngine.Input(ship);
                ship.StepEngineInput = input;
                ShipEngine.Update(World, ship.Body, input, TickDuration);

                // Physics reports a touch after the solve, so ramming uses the velocity from before the step.
                ship.PreStepVelocity = World.GetLinearVelocity(ship.Body);
            }

            // Each missile reads only its own state and the grid, so the storage order has no effect. Owner target
            // records of this tick apply first (P3).
            ApplyRemoteOperations(4);
            for (var i = 0; i < _bullets.Capacity; ++i)
            {
                if (!_bullets.IsAlive(i)) continue;
                ref var bullet = ref _bullets[i];
                if (!bullet.IsBeam && bullet.Definition.IsHoming) HomingSteering.Update(this, ref bullet);
            }
        }

        // Stage 5. Roster ships first, then drones by ID; devices in index order. One spawn slot stays adjacent.
        // Fire confirmations of this tick follow the local spawns (operation stage 6), then unconfirmed bullets leave.
        private void ApplySpawnRequests()
        {
            RecordSpawnSamples();
            ApplyRemoteOperations(5);
            for (var i = 0; i < _spawnRequests.Count; ++i)
            {
                var request = _spawnRequests[i];
                var sequence = 0u;
                for (var j = i - 1; j >= 0 && _spawnRequests[j].HasSameSlot(request); --j) ++sequence;
                if (request.DroneBay != null) SpawnDrone(request, sequence);
                else if (request.Beam != null) SpawnBeam(request, sequence);
                else SpawnBullet(request, sequence);
            }

            _spawnRequests.Clear();
            ApplyRemoteOperations(6);
            RemoveUnconfirmedBullets();
        }

        private void SpawnDrone(in SpawnRequest request, uint sequence)
        {
            var parent = request.Ship;
            if (!Setup.IsLocalOwner(parent.Owner)) return;
            var bay = parent.Slot.GetBay(request.DeviceIndex);
            var id = SpawnIdentity.CreateChild(parent.Id, Tick, (uint)request.DeviceIndex,
                (uint)request.BarrelIndex, sequence);
            var ship = _ships.Allocate();
            if (!TryRegister(id, ship.Handle))
            {
                _ships.Release(ship);
                return;
            }

            var body = World.CreateBody(new BodyDef { Mass = bay.Definition.Drone.Mass });
            ship.InitializeDrone(id, bay, body, Tick, Setup.DamageTypes.Count);
            AddShipShapes(ship, CollisionFilters.Drone(ship.Side));
            var origin = World.GetPosition(parent.Body);
            var angle = World.GetAngle(parent.Body);
            World.SetPose(body, origin, angle);
            World.TryFindFreePosition(body, origin, SpawnSearchDistance, out var position);
            World.SetPose(body, position, angle);

            var index = 0;
            while (index < _drones.Count && _drones[index].Id.Value < id.Value) ++index;
            _drones.Insert(index, ship);
            bay.RecordLaunch();
            _events.Add(BattleEvent.Spawn(BattleId, Tick, _events.Count, ship, Authority(ship.Owner)));
            CaptureDroneSpawn(ship, parent.Id);
        }

        // Legacy BulletFactory.ConfigureBody at the muzzle pose, plus the Q1 recoil (owner, 2026-10-20).
        private void SpawnBullet(in SpawnRequest request, uint sequence)
        {
            var ship = request.Ship;
            var index = CreateBullet(ship, request.DeviceIndex, request.BarrelIndex, sequence, request.Bullet,
                request.Spread, Tick, ReadSpawnSample(ship), out var muzzle, out var direction, out var angle);
            if (index < 0) return;

            var definition = request.Bullet;
            if (definition.Recoil > 0f) World.ApplyImpulse(ship.Body, -definition.Recoil * direction, muzzle);

            ref readonly var bullet = ref _bullets[index];
            _events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _events.Count, bullet, Authority(bullet.Owner)));
            CaptureBulletSpawn(bullet, request.BarrelIndex, sequence, !definition.IsHoming, muzzle, angle);
        }

        // Legacy BulletFactory.ConfigureBody at the muzzle of the sample. A spawn tick before the present advances the
        // bullet on a line, step by step as physics moves it, without hit tests. Returns -1 on an ID collision.
        // With an owner launch pose (P1), the muzzle and angle come from it; the inherited velocity stays the sample's.
        private int CreateBullet(ShipEntity ship, int deviceIndex, int barrelIndex, uint sequence,
            BulletDefinition definition, float spread, long spawnTick, in SpawnSample sample, out Vec2 muzzle,
            out Vec2 direction, out float angle, in OwnerOperation launch = default)
        {
            var id = SpawnIdentity.CreateChild(ship.Id, spawnTick, (uint)deviceIndex, (uint)barrelIndex, sequence);
            var index = _bullets.Allocate();
            if (!TryRegister(id, _bullets.GetHandle(index)))
            {
                _bullets.Release(index);
                muzzle = direction = default;
                angle = 0f;
                return -1;
            }

            Launch(ship, barrelIndex, id, definition, spread, spawnTick, sample, launch, out muzzle, out angle,
                out direction, out var velocity, out var position);

            var body = World.CreateBullet(new BulletDef
            {
                Position = position,
                Angle = angle,
                LinearVelocity = velocity,
                Owner = ship.Body,

                // The core selects the first valid hit, so physics reports every target (contact ordering, rule 1).
                PassThrough = true,
                Filter = CollisionFilters.Bullet(definition, ship.Side),
            }, new Circle(Vec2.Zero, definition.Radius));

            ref var bullet = ref _bullets[index];
            bullet.Id = id;
            bullet.ParentId = ship.Id;
            bullet.Side = ship.Side;
            bullet.Owner = ship.Owner;
            bullet.DeviceIndex = deviceIndex;
            bullet.Definition = definition;
            bullet.ExpiryTick = spawnTick + definition.LifetimeTicks - 1;
            bullet.HitPoints = definition.HitPoints;
            bullet.SpawnTick = spawnTick;
            bullet.Confirmed = Setup.IsLocalOwner(ship.Owner);

            // Legacy runs no search at creation (its timer check is strict); the first one is in the next tick.
            bullet.NextSearchTick = spawnTick + 1;
            _bullets.SetBody(index, body);
            return index;
        }

        // The launch of a projectile and its position at the present tick, before this tick's physics.
        private void Launch(ShipEntity ship, int barrelIndex, EntityId id, BulletDefinition definition, float spread,
            long spawnTick, in SpawnSample sample, in OwnerOperation launch, out Vec2 muzzle, out float angle,
            out Vec2 direction, out Vec2 velocity, out Vec2 position)
        {
            if (launch.HasLaunchPose)
            {
                muzzle = launch.Pose.Position;
                angle = launch.Pose.Angle;
            }
            else
            {
                var barrel = ship.Definition.Barrels[barrelIndex];
                muzzle = sample.Position + sample.Rotation.Rotate(barrel.Position);
                var random = CounterRandom.Create(Setup.MatchSeed, id, spawnTick, RandomPurpose.WeaponSpread);
                angle = sample.Angle + barrel.Angle + (random.NextFloat() - 0.5f) * spread;
            }

            direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            velocity = definition.Speed * definition.StartingVelocityMultiplier * direction +
                       definition.ParentVelocityEffect * sample.LinearVelocity;

            position = muzzle;
            if (spawnTick < Tick)
            {
                position = World.Torus.Wrap(position);
                for (var tick = spawnTick; tick < Tick; ++tick)
                    position = World.Torus.Wrap(position + TickDuration * velocity);
            }
        }

        private void SpawnBeam(in SpawnRequest request, uint sequence)
        {
            var index = CreateBeam(request.Ship, request.Beam, sequence, Tick);
            if (index < 0) return;
            ref readonly var beam = ref _bullets[index];
            _events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _events.Count, beam, Authority(beam.Owner)));
            CaptureBulletSpawn(beam, request.BarrelIndex, sequence);
        }

        // Returns -1 on an ID collision.
        private int CreateBeam(ShipEntity ship, BeamDevice device, uint sequence, long spawnTick)
        {
            var id = SpawnIdentity.CreateChild(ship.Id, spawnTick, (uint)device.Index,
                (uint)device.Definition.BarrelIndex, sequence);
            var index = _bullets.Allocate();
            var handle = _bullets.GetHandle(index);
            if (!TryRegister(id, handle))
            {
                _bullets.Release(index);
                return -1;
            }

            ref var beam = ref _bullets[index];
            beam.Id = id;
            beam.ParentId = ship.Id;
            beam.Side = ship.Side;
            beam.Owner = ship.Owner;
            beam.DeviceIndex = device.Index;
            beam.BeamDefinition = device.Definition;
            beam.ExpiryTick = long.MaxValue; // Device-owned lifetime, without projectile expiry.
            beam.SpawnTick = spawnTick;
            beam.Confirmed = Setup.IsLocalOwner(ship.Owner);
            device.Attach(handle);
            return index;
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
            _bullets.CollectHits(World, _shapes, _ships, _contacts);
            _ramming.Collect(World, _shapes, _ships, _contacts, Setup.DamageTypes.RammingDamageType);

            var duplicates = _contacts.Sort();
            if (duplicates > 0)
                ReportDefect($"Battle {BattleId}, tick {Tick}: {duplicates} contact candidates share a key.");
        }

        // Stage 9. Ship contacts belong to the attacker; bullet damage belongs to the target bullet.
        private void ResolveContacts()
        {
            _contacts.Resolve();
            _beams.Complete(this, _contacts);
            _outgoingHits.BeginContacts();

            var accepted = _contacts.Accepted;
            for (var i = 0; i < accepted.Length; ++i)
            {
                ref readonly var hit = ref accepted[i];
                ObserveContact(hit);
                if (hit.Kind == ContactKind.Disarm)
                {
                    if (!_bullets.TryGet(hit.TargetHandle, out var disarmed)) continue;
                    _bullets.MarkRemoval(hit.TargetHandle, RemovalReason.Disposal);
                    _events.Add(BattleEvent.DisarmEvent(BattleId, Tick, _events.Count, hit,
                        Authority(_bullets[disarmed].Owner)));
                    var defender = _ships.Get(hit.AttackerHandle);
                    if (defender != null)
                        ClaimBulletContact(hit.Attacker, defender.Owner, hit.AttackerDeviceIndex, _bullets[disarmed],
                            HitImpactKind.Disarm, 0f);
                    continue;
                }
                if (hit.Kind != ContactKind.Hit) continue;

                // A projectile stops on either copy. Its removal has its own authority flag.
                if (!hit.AttackerPassThrough && hit.Source != ContactSource.Ray)
                    _bullets.MarkRemoval(hit.AttackerHandle, RemovalReason.Death);

                BattleEventAuthority authority;
                if (hit.TargetHandle.Kind == EntityKind.Bullet)
                {
                    if (!_bullets.TryGet(hit.TargetHandle, out var index)) continue;
                    ref readonly var targetBullet = ref _bullets[index];
                    authority = Authority(targetBullet.Owner);
                    var attackerBullet = _bullets.TryGet(hit.AttackerHandle, out var attackerIndex);
                    var attackerOwner = attackerBullet ? _bullets[attackerIndex].Owner : _ships.Get(hit.AttackerHandle)?.Owner ?? -1;
                    var ray = hit.Source == ContactSource.Ray;
                    if (authority == BattleEventAuthority.Committed)
                    {
                        // A remote attacker's contact counts once, whether this device or the attacker's owner saw it.
                        if (Setup.IsLocalOwner(attackerOwner) || RecordBulletContact(hit.Attacker, hit.Target, ray ? Tick : -1))
                            _bullets.AddDamage(hit.TargetHandle, hit.Damage);
                        // A remote projectile that stops at a local bullet stops on its owner too.
                        if (attackerBullet && !hit.AttackerPassThrough && !ray)
                            ClaimBulletContact(hit.Target, targetBullet.Owner, -1, _bullets[attackerIndex],
                                HitImpactKind.Stop, 0f);
                    }
                    else
                    {
                        // Resolver walk damage decides predicted removal without changing remote hit points.
                        if (_contacts.GetStopTime(hit.TargetHandle) < float.PositiveInfinity)
                            _bullets.MarkRemoval(hit.TargetHandle, RemovalReason.Death);
                        if (attackerOwner >= 0)
                            ClaimBulletContact(hit.Attacker, attackerOwner, hit.AttackerDeviceIndex, targetBullet,
                                ray ? HitImpactKind.DamageOverTime : HitImpactKind.Impact, hit.Damage);
                    }
                }
                else
                {
                    var target = _ships.Get(hit.TargetHandle);
                    if (target == null) continue;
                    int owner, deviceIndex;
                    HitImpactKind kind;
                    var amount = hit.Damage;
                    if (_bullets.TryGet(hit.AttackerHandle, out var index))
                    {
                        ref readonly var attacker = ref _bullets[index];
                        owner = attacker.Owner;
                        deviceIndex = attacker.DeviceIndex;
                        kind = attacker.IsBeam ? HitImpactKind.DamageOverTime :
                            attacker.Definition.IsMissile ? HitImpactKind.Impact : HitImpactKind.Energy;
                        if (attacker.IsBeam) amount = attacker.BeamDefinition.DamagePerSecond;
                    }
                    else
                    {
                        var attacker = _ships.Get(hit.AttackerHandle);
                        if (attacker == null) continue;
                        owner = attacker.Owner;
                        deviceIndex = hit.AttackerDeviceIndex;
                        kind = hit.PointDefense != null ? HitImpactKind.Energy : HitImpactKind.Impact;
                    }
                    authority = Authority(owner);
                    if (authority == BattleEventAuthority.Committed)
                    {
                        // A remote target's copy takes the same damage as a prediction (N3); it never dies of it.
                        target.AddPendingDamage(hit.DamageType, hit.Damage);
                        if (!Setup.IsLocalOwner(target.Owner))
                            _outgoingHits.Add(new ResolvedHit(BattleId, Tick, hit.Attacker, owner, deviceIndex,
                                hit.Target, target.Owner, hit.TargetShapeOrder, HitContactState.Start, kind, amount,
                                damageType: (byte)hit.DamageType));
                    }
                }

                _events.Add(BattleEvent.HitEvent(BattleId, Tick, _events.Count, hit, authority));
            }
            _outgoingHits.CompleteContacts(Tick);
        }

        // Stage 10. One pass, shield first, then armor (legacy Impact.ApplyShield and ShipStats.ApplyDamage).
        // Received hits add to a local target's pending damage. A remote target's pending damage is only the damage
        // this device sent it (N3): a prediction that corrections replace.
        private void ApplyDamage()
        {
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship == null || !ship.HasPendingDamage) continue;

                ApplyShieldFirst(ship);
                ship.ClearPendingDamage();
            }

            _bullets.ApplyDamage(Setup);
        }

        // Stage 11. Marks deaths and bullet expiry; stage 12 applies them. Armor 0 is a death; so are missile hit points 0.
        private void MarkDeaths()
        {
            _bullets.MarkDeaths(Tick, Setup);

            for (var i = 0; i < _deathRequests.Count; ++i)
            {
                var ship = FindShip(_deathRequests[i]);
                if (ship != null && Setup.IsLocalOwner(ship.Owner)) ship.PendingDeath = true;
            }

            _deathRequests.Clear();

            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship != null && Setup.IsLocalOwner(ship.Owner) && ship.Armor.Value <= 0f) ship.PendingDeath = true;
            }
        }

        // Stage 12. One batch, in roster order: deaths, then retreat completions, then surrender and kill-all.
        // Drone deaths follow the roster batch. Bullets leave last, by ID. Attached beams die with their ship.
        private void ApplyStructureChanges()
        {
            ApplyRemoteOperations(12);
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
                else if (ship.RetreatCompletionTick == Tick)
                {
                    var armorDeficit = ship.Armor.Deficit;
                    var shieldDeficit = ship.Shield.Deficit;
                    RemoveShip(ship, RemovalReason.Retreat);
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
            for (var i = 0; i < _drones.Count;)
            {
                var drone = _drones[i];
                if (!drone.PendingDeath || !Setup.IsLocalOwner(drone.Owner)) { ++i; continue; }
                RemoveShip(drone, RemovalReason.Death);
                _drones.RemoveAt(i);
            }

            RemoveBullets();
        }

        private void RemoveBullets()
        {
            _bullets.CollectRemovals(_bulletRemovals);
            for (var i = 0; i < _bulletRemovals.Count; ++i)
            {
                var index = _bulletRemovals[i];
                ref readonly var bullet = ref _bullets[index];
                _outgoingHits.RemoveAttacker(bullet.Id, Tick + 1);
                _events.Add(BattleEvent.BulletRemoval(BattleId, Tick, _events.Count, bullet, Authority(bullet.Owner)));
                CaptureRemoval(bullet.Id, bullet.Owner, bullet.PendingRemoval);
                if (!bullet.Confirmed && Setup.HasRemoteOwners && !Setup.IsLocalOwner(bullet.Owner))
                    RecordPredictedRemoval(bullet);
                ReleaseBullet(index);
            }
        }

        private void ReleaseBullet(int index)
        {
            ref readonly var bullet = ref _bullets[index];
            if (!bullet.IsBeam) World.DestroyBullet(bullet.Body);
            if (_bulletContacts.Count > 0) ForgetBulletContacts(bullet.Id);
            _entities.Remove(bullet.Id);
            _bullets.Release(index);
        }

        // Stage 13. Elimination first, then the timer.
        private void UpdateBattleFlow()
        {
            EvaluateResult();
            if (Result.IsCompleted)
                _events.Add(BattleEvent.ResultEvent(BattleId, Tick, _events.Count, Result));
        }

        private void EvaluateResult()
        {
            var frontier = Tick;
            for (var i = 0; i < Setup.Roster.Count; ++i)
            {
                var slot = Setup.Roster[i];
                if (Setup.IsLocalOwner(slot.Owner)) continue;
                if (!_watermarks.TryGetValue(slot.Owner, out var watermark)) { frontier = -1; break; }
                watermark.Promote(OperationsThrough(slot.Owner));
                frontier = Math.Min(frontier, watermark.Through);
            }
            // Received operations may still wait for a roster revision or their original stage.
            foreach (var operation in _remoteOperations)
                frontier = Math.Min(frontier, operation.Tick - 1);
            Result = _flow.Evaluate(Tick, frontier, out var candidate, out var elapsed);
            CandidateResult = candidate;
            TimerElapsedTicks = elapsed;
        }

        // Stage 14. Writes the snapshot and the event batch of this tick, then swaps the buffers.
        private void Publish()
        {
            RecordBodySamples();
            RecordPredictedHits();
            _writing.Write(this, Tick, World, Roster, _events);
            _events.Clear();
            _outgoingHits.Records.Clear();
            _outgoingOperations.Clear();
            _appliedOperations.Clear();

            var published = _writing;
            _writing = _published;
            _published = published;
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
            if (!Setup.IsLocalOwner(slot.Owner)) { Reject(command, "remote owner"); return; }
            if (slot.Status != RosterStatus.Ready) { Reject(command, $"slot is {slot.Status}"); return; }
            if (Roster.CountActive(slot.Side) >= Setup.MaxActiveShipsPerSide) { Reject(command, "side limit"); return; }

            if (!DeploySlot(slot)) Reject(command, "entity ID collision");
        }

        // Local deployment uses spawn placement. Received deployments use the owner's recorded pose.
        internal bool DeploySlot(RosterSlot slot)
        {
            var id = SpawnIdentity.CreateRoot(BattleId, slot.RosterId, slot.DeploymentCount + 1, Tick);
            var ship = _ships.Allocate();
            if (!TryRegister(id, ship.Handle))
            {
                _ships.Release(ship);
                return false;
            }

            var definition = slot.Setup.Ship;
            var body = World.CreateBody(new BodyDef { Mass = definition.Mass });
            ship.Initialize(id, slot, body, Tick, Setup.DamageTypes.Count);

            AddShipShapes(ship, CollisionFilters.Ship(slot.Side));

            PlaceSpawn(body, id, slot.Side);
            slot.Activate(ship, ++_deploymentOrder);

            _events.Add(BattleEvent.Spawn(BattleId, Tick, _events.Count, ship, Authority(ship.Owner)));
            AddTransition(slot, RosterStatus.Ready, RosterTransitionReason.Deployed, id);
            return true;
        }

        private void AddShipShapes(ShipEntity ship, Filter filter)
        {
            var shapeDef = ShapeDef.Default;
            shapeDef.Filter = filter;
            var parts = ship.Definition.HullParts;
            for (var i = 0; i < parts.Count; ++i)
            {
                var shape = AddHullPart(ship.Body, parts[i], shapeDef);
                ship.AddShape(shape);
                _shapes.Register(shape, new ShapeEntry(ship.Handle, ship.Id, ShapeHitPriority.Hull, i));
            }
        }

        private ShapeId AddHullPart(BodyId body, in HullPart part, in ShapeDef shapeDef)
        {
            switch (part.Type)
            {
                case HullPartType.Circle:
                    return World.AddCircle(body, part.Circle, shapeDef);
                case HullPartType.Capsule:
                    return World.AddCapsule(body, part.Capsule, shapeDef);
                default:
                    return World.AddPolygon(body, part.Polygon, shapeDef);
            }
        }

        private void StartRetreat(in RosterCommand command)
        {
            var ship = FindShip(command.EntityId);
            if (ship == null) { Reject(command, "unknown entity"); return; }
            if (ship.Slot == null) { Reject(command, "not a roster ship"); return; }
            if (!Setup.IsLocalOwner(ship.Owner)) { Reject(command, "remote owner"); return; }
            if (ship.RetreatCompletionTick != ShipEntity.NoRetreat) { Reject(command, "already retreating"); return; }

            ship.RetreatCompletionTick = Tick + RetreatTicks;
            if (ShouldCapture(ship.Owner))
                _outgoingOperations.Add(OwnerOperation.RetreatStarted(BattleId, ship.Owner, NextOperationSequence(ship.Owner), Tick, ship.Id));
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

        private void RemoveShip(ShipEntity ship, RemovalReason reason)
        {
            for (var i = 0; i < ship.DeviceCount; ++i) ship.GetDevice(i).Deactivate(this);
            _events.Add(BattleEvent.Removal(BattleId, Tick, _events.Count, ship, reason));
            if (ship.IsDrone) CaptureRemoval(ship.Id, ship.Owner, reason, ship.Bay.Key);
            for (var i = 0; i < ship.ShapeCount; ++i)
                _shapes.Release(ship.GetShape(i));
            _ramming.Forget(ship.Id);
            if (!Setup.IsLocalOwner(ship.Owner)) ForgetPredictedHits(ship.Id);
            World.DestroyBody(ship.Body);
            _entities.Remove(ship.Id);
            if (ship.IsDrone) ship.Bay.RecordLoss();
            _ships.Release(ship);
        }

        private void AddTransition(RosterSlot slot, RosterStatus previousStatus, RosterTransitionReason reason, EntityId id)
        {
            _events.Add(BattleEvent.Transition(BattleId, Tick, _events.Count, slot, previousStatus, reason, id,
                _applyingRemoteOperation ? BattleEventAuthority.Committed : Authority(slot.Owner)));
            if (!_applyingRemoteOperation) _flow.Add(Tick, slot.Side, previousStatus, slot.Status);
            CaptureRoster(slot, previousStatus, reason, id);
        }

        private void ReportDefect(string message)
        {
            if (ThrowOnDefect) throw new InvalidOperationException(message);
            Trace.LogError(message);
        }

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

        private int FindInsertIndex(in ControlFrame frame)
        {
            var low = 0;
            var high = _frames.Count;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                var other = _frames[middle];
                var before = other.TargetTick < frame.TargetTick ||
                             (other.TargetTick == frame.TargetTick && other.CommandId < frame.CommandId);
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

        private bool Reject(in ControlFrame frame, string reason)
        {
            ++RejectedCommandCount;
            Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected {frame}: {reason}.");
            return false;
        }

        private bool Reject(in SelfDestructCommand command, string reason)
        {
            ++RejectedCommandCount;
            Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected self-destruct #{command.CommandId} for {command.EntityId}: {reason}.");
            return false;
        }

        private static bool IsValidKind(RosterCommandKind kind) =>
            kind == RosterCommandKind.Deploy || kind == RosterCommandKind.Retreat ||
            kind == RosterCommandKind.Surrender || kind == RosterCommandKind.KillAll;

        private readonly ShipStore _ships = new ShipStore();
        private readonly List<ShipEntity> _drones = new List<ShipEntity>();
        private readonly List<SelfDestructCommand> _selfDestructs = new List<SelfDestructCommand>();
        private readonly BulletStore _bullets = new BulletStore();
        private readonly BeamQueries _beams = new BeamQueries();
        private readonly PointDefenseQueries _pointDefense = new PointDefenseQueries();
        private readonly List<SpawnRequest> _spawnRequests = new List<SpawnRequest>();
        private readonly List<int> _bulletRemovals = new List<int>();
        private readonly EntityRegistry _entities = new EntityRegistry();
        private readonly ShapeTable _shapes = new ShapeTable();
        private readonly ContactResolver _contacts = new ContactResolver();
        private readonly RammingTracker _ramming = new RammingTracker();
        private readonly TargetGrid _targets;
        private readonly List<RosterCommand> _commands = new List<RosterCommand>();
        private readonly List<ControlFrame> _frames = new List<ControlFrame>();
        private readonly HashSet<FrameKey> _pendingFrameKeys = new HashSet<FrameKey>();
        private readonly HashSet<ulong> _pendingCommandIds = new HashSet<ulong>();
        private readonly List<EntityId> _deathRequests = new List<EntityId>();
        private readonly bool[] _destroySide = new bool[BattleSetup.SideCount];
        private readonly OutgoingHitBuffer _outgoingHits = new OutgoingHitBuffer();
        private readonly BattleEventBuffer _events = new BattleEventBuffer();
        private BattleSnapshot _published;
        private BattleSnapshot _writing;
        private long _deploymentOrder;

        // One control frame per entity and tick (commands, rule 2).
        private readonly struct FrameKey : IEquatable<FrameKey>
        {
            private readonly EntityId _entityId;
            private readonly long _tick;

            public FrameKey(EntityId entityId, long tick)
            {
                _entityId = entityId;
                _tick = tick;
            }

            public bool Equals(FrameKey other) => _entityId == other._entityId && _tick == other._tick;
            public override bool Equals(object obj) => obj is FrameKey other && Equals(other);
            public override int GetHashCode() => (_entityId.GetHashCode() * 397) ^ _tick.GetHashCode();
        }
    }
}

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
            _targets = new TargetGrid(World.Torus);

            _published = new BattleSnapshot(BattleId, Roster.Slots.Count);
            _writing = new BattleSnapshot(BattleId, Roster.Slots.Count);
            _published.Write(this, -1, World, Roster, _events);
        }

        public BattleSetup Setup { get; }
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

            ReportDefect($"Battle {BattleId}: entity ID {id} is already alive.");
            return false;
        }

        // Stage 2: a device asks for a bullet; stage 5 creates it.
        internal void RequestSpawn(in SpawnRequest request) => _spawnRequests.Add(request);

        internal BulletStore Bullets => _bullets;
        internal TargetGrid Targets => _targets;
        internal ShapeTable Shapes => _shapes;
        internal ContactResolver Contacts => _contacts;
        internal RammingTracker Ramming => _ramming;
        internal BeamQueries Beams => _beams;

        // Stage 1. Roster commands for this tick, then control frames, each in command ID order.
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

            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
                slots[i].ActiveShip?.BeginControlTick();

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
            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var ship = slots[i].ActiveShip;
                if (ship == null) continue;

                ship.EngineHasEnergy = ship.Energy.Value > 0f;
                ship.Energy.Update(TickDuration);
                ship.Armor.Update(TickDuration);
                ship.Shield.Update(TickDuration);

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
            var slots = Roster.Slots;
            _targets.Clear();
            for (var i = 0; i < slots.Count; ++i)
            {
                var ship = slots[i].ActiveShip;
                if (ship != null)
                    _targets.Add(ship.Id, ship.Side, World.GetPosition(ship.Body), ship.Definition.HullRadius);
            }

            for (var i = 0; i < slots.Count; ++i)
            {
                var ship = slots[i].ActiveShip;
                if (ship == null) continue;
                if (ship.RetreatCompletionTick == ShipEntity.NoRetreat) ShipEngine.Update(World, ship, TickDuration);

                // Physics reports a touch after the solve, so ramming uses the velocity from before the step.
                ship.PreStepVelocity = World.GetLinearVelocity(ship.Body);
            }

            // Each missile reads only its own state and the grid, so the storage order has no effect.
            for (var i = 0; i < _bullets.Capacity; ++i)
            {
                if (!_bullets.IsAlive(i)) continue;
                ref var bullet = ref _bullets[i];
                if (!bullet.IsBeam && bullet.Definition.IsHoming) HomingSteering.Update(this, ref bullet);
            }
        }

        // Stage 5. Requests come in roster order, then device order, so requests of one spawn slot are adjacent.
        private void ApplySpawnRequests()
        {
            for (var i = 0; i < _spawnRequests.Count; ++i)
            {
                var request = _spawnRequests[i];
                var sequence = 0u;
                for (var j = i - 1; j >= 0 && _spawnRequests[j].HasSameSlot(request); --j) ++sequence;
                if (request.Beam != null) SpawnBeam(request, sequence);
                else SpawnBullet(request, sequence);
            }

            _spawnRequests.Clear();
        }

        // Legacy BulletFactory.ConfigureBody at the muzzle pose, plus the Q1 recoil (owner, 2026-10-20).
        private void SpawnBullet(in SpawnRequest request, uint sequence)
        {
            var ship = request.Ship;
            var id = SpawnIdentity.CreateChild(ship.Id, Tick, (uint)request.DeviceIndex, (uint)request.BarrelIndex, sequence);
            var index = _bullets.Allocate();
            if (!TryRegister(id, _bullets.GetHandle(index)))
            {
                _bullets.Release(index);
                return;
            }

            var definition = request.Bullet;
            var barrel = ship.Definition.Barrels[request.BarrelIndex];
            var muzzle = World.GetPosition(ship.Body) + World.GetRotation(ship.Body).Rotate(barrel.Position);

            var random = CounterRandom.Create(Setup.MatchSeed, id, Tick, RandomPurpose.WeaponSpread);
            var angle = World.GetAngle(ship.Body) + barrel.Angle + (random.NextFloat() - 0.5f) * request.Spread;
            var direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            var velocity = definition.Speed * definition.StartingVelocityMultiplier * direction +
                           definition.ParentVelocityEffect * World.GetLinearVelocity(ship.Body);

            var body = World.CreateBullet(new BulletDef
            {
                Position = muzzle,
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
            bullet.Definition = definition;
            bullet.ExpiryTick = Tick + definition.LifetimeTicks - 1;
            bullet.HitPoints = definition.HitPoints;

            // Legacy runs no search at creation (its timer check is strict); the first one is in the next tick.
            bullet.NextSearchTick = Tick + 1;
            _bullets.SetBody(index, body);

            if (definition.Recoil > 0f) World.ApplyImpulse(ship.Body, -definition.Recoil * direction, muzzle);

            _events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _events.Count, bullet));
        }

        private void SpawnBeam(in SpawnRequest request, uint sequence)
        {
            var ship = request.Ship;
            var id = SpawnIdentity.CreateChild(ship.Id, Tick, (uint)request.DeviceIndex, (uint)request.BarrelIndex, sequence);
            var index = _bullets.Allocate();
            var handle = _bullets.GetHandle(index);
            if (!TryRegister(id, handle))
            {
                _bullets.Release(index);
                return;
            }

            ref var beam = ref _bullets[index];
            beam.Id = id;
            beam.ParentId = ship.Id;
            beam.Side = ship.Side;
            beam.Owner = ship.Owner;
            beam.BeamDefinition = request.Beam.Definition;
            beam.ExpiryTick = long.MaxValue; // Device-owned lifetime, without projectile expiry.
            request.Beam.Attach(handle);
            _events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _events.Count, beam));
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
            _ramming.Collect(World, _shapes, _ships, _contacts);

            var duplicates = _contacts.Sort();
            if (duplicates > 0)
                ReportDefect($"Battle {BattleId}, tick {Tick}: {duplicates} contact candidates share a key.");
        }

        // Stage 9. Selects the valid candidates and turns hits on ships and missiles into pending damage.
        private void ResolveContacts()
        {
            _contacts.Resolve();
            _beams.Complete(this, _contacts);

            var accepted = _contacts.Accepted;
            for (var i = 0; i < accepted.Length; ++i)
            {
                ref readonly var hit = ref accepted[i];
                if (hit.Kind == ContactKind.Disarm)
                {
                    _bullets.MarkRemoval(hit.TargetHandle, RemovalReason.Disposal);
                    _events.Add(BattleEvent.DisarmEvent(BattleId, Tick, _events.Count, hit));
                    continue;
                }
                if (hit.Kind != ContactKind.Hit) continue;

                // A projectile stops permanently; a ray only consumes its first hit for this tick.
                if (!hit.AttackerPassThrough && hit.Source != ContactSource.Ray)
                    _bullets.MarkRemoval(hit.AttackerHandle, RemovalReason.Death);

                if (hit.TargetHandle.Kind == EntityKind.Bullet)
                {
                    _bullets.AddDamage(hit.TargetHandle, hit.Damage);
                }
                else
                {
                    var target = _ships.Get(hit.TargetHandle);
                    if (target == null) continue;
                    target.PendingDamage += hit.Damage;
                }

                _events.Add(BattleEvent.HitEvent(BattleId, Tick, _events.Count, hit));
            }
        }

        // Stage 10. One pass, shield first, then armor (legacy Impact.ApplyShield and ShipStats.ApplyDamage).
        // In phase 2 the local runtime owns every entity, so it applies all damage.
        private void ApplyDamage()
        {
            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var ship = slots[i].ActiveShip;
                if (ship == null || !(ship.PendingDamage > 0f)) continue;

                var damage = ship.PendingDamage;
                ship.PendingDamage = 0f;

                var absorbed = ship.Shield.Max > 0f && ship.Shield.Value > 0f ? Math.Min(damage, ship.Shield.Value) : 0f;
                if (absorbed > 0f) ship.Shield.Consume(absorbed);
                ship.Armor.Consume(damage - absorbed);
            }

            _bullets.ApplyDamage();
        }

        // Stage 11. Marks deaths and bullet expiry; stage 12 applies them. Armor 0 is a death; so are missile hit points 0.
        private void MarkDeaths()
        {
            _bullets.MarkDeaths(Tick);

            for (var i = 0; i < _deathRequests.Count; ++i)
            {
                var ship = FindShip(_deathRequests[i]);
                if (ship != null) ship.PendingDeath = true;
            }

            _deathRequests.Clear();

            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var ship = slots[i].ActiveShip;
                if (ship != null && ship.Armor.Value <= 0f) ship.PendingDeath = true;
            }
        }

        // Stage 12. One batch, in roster order: deaths, then retreat completions, then surrender and kill-all.
        // Bullets leave last, in entity ID order. Projectiles outlive their parent; attached beams do not.
        private void ApplyStructureChanges()
        {
            var slots = Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var slot = slots[i];
                var ship = slot.ActiveShip;
                if (ship == null) continue;

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
                    if (slot.Side != side || slot.Status == RosterStatus.Destroyed) continue;

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

            RemoveBullets();
        }

        private void RemoveBullets()
        {
            _bullets.CollectRemovals(_bulletRemovals);
            for (var i = 0; i < _bulletRemovals.Count; ++i)
            {
                var index = _bulletRemovals[i];
                ref readonly var bullet = ref _bullets[index];
                _events.Add(BattleEvent.BulletRemoval(BattleId, Tick, _events.Count, bullet));
                if (!bullet.IsBeam) World.DestroyBullet(bullet.Body);
                _entities.Remove(bullet.Id);
                _bullets.Release(index);
            }
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

        // Stage 14. Writes the snapshot and the event batch of this tick, then swaps the buffers.
        private void Publish()
        {
            _writing.Write(this, Tick, World, Roster, _events);
            _events.Clear();

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
            ship.Initialize(id, slot, body, Tick);

            var shapeDef = ShapeDef.Default;
            shapeDef.Filter = CollisionFilters.Ship(slot.Side);
            var parts = definition.HullParts;
            for (var i = 0; i < parts.Count; ++i)
            {
                var shape = World.AddCircle(body, parts[i], shapeDef);
                ship.AddShape(shape);
                _shapes.Register(shape, new ShapeEntry(ship.Handle, id, ShapeHitPriority.Hull, i));
            }

            PlaceSpawn(body, id, slot.Side);
            slot.Activate(ship, ++_deploymentOrder);
            ResetTimer();

            _events.Add(BattleEvent.Spawn(BattleId, Tick, _events.Count, ship));
            AddTransition(slot, RosterStatus.Ready, RosterTransitionReason.Deployed, id);
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

        private void RemoveShip(ShipEntity ship, RemovalReason reason)
        {
            for (var i = 0; i < ship.DeviceCount; ++i) ship.GetDevice(i).Deactivate(this);
            _events.Add(BattleEvent.Removal(BattleId, Tick, _events.Count, ship, reason));
            for (var i = 0; i < ship.ShapeCount; ++i)
                _shapes.Release(ship.GetShape(i));
            _ramming.Forget(ship.Id);
            World.DestroyBody(ship.Body);
            _entities.Remove(ship.Id);
            _ships.Release(ship);
            ResetTimer();
        }

        private void AddTransition(RosterSlot slot, RosterStatus previousStatus, RosterTransitionReason reason, EntityId id) =>
            _events.Add(BattleEvent.Transition(BattleId, Tick, _events.Count, slot, previousStatus, reason, id));

        private void ReportDefect(string message)
        {
            if (ThrowOnDefect) throw new InvalidOperationException(message);
            Trace.LogError(message);
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

        private static bool IsValidKind(RosterCommandKind kind) =>
            kind == RosterCommandKind.Deploy || kind == RosterCommandKind.Retreat ||
            kind == RosterCommandKind.Surrender || kind == RosterCommandKind.KillAll;

        private readonly ShipStore _ships = new ShipStore();
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

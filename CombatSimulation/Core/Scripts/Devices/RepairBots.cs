using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // A separate contact target, excluded from ship searches, roster results and kill counts.
    internal sealed class RepairBot
    {
        public EntityId Id, ShipId;
        public EntityHandle Handle;
        public BodyId Body;
        public ShapeId Shape;
        public int Owner, DeviceIndex;
        public Allegiance Allegiance;
        public RepairBotDefinition Definition;
        public long SpawnTick;
        public float HitPoints;
        public float Damage;
        public bool Repairing;
        public RemovalReason Removal;
        public Vec2 PreStepVelocity;
        public long LastCorrectionTick = -1, CorrectionTick = -1, LastSentTick = -1;
        public MissileCorrection Correction;
        public float Scale => Definition.Size;
    }

    public readonly struct RepairBotState
    {
        public readonly EntityId Id, ShipId;
        public readonly int DeviceIndex;
        public readonly RepairBotDefinition Definition;
        public readonly Vec2 Position;
        public readonly float Angle, Scale;
        public readonly bool Repairing;
        internal RepairBotState(RepairBot bot, PhysicsWorld physics)
        {
            Id = bot.Id;
            ShipId = bot.ShipId;
            DeviceIndex = bot.DeviceIndex;
            Definition = bot.Definition;
            Position = physics.GetPosition(bot.Body);
            Angle = physics.GetAngle(bot.Body);
            Scale = bot.Scale;
            Repairing = bot.Repairing;
        }
    }

    internal sealed class RepairBots
    {
        public RepairBots(BattleWorld world, Battle battle) { _world = world; _battle = battle; }
        public IReadOnlyList<RepairBot> Items => _items;
        public RepairBot Get(EntityHandle handle) => handle.Kind == EntityKind.RepairBot && handle.Index >= 0 &&
            handle.Index < _items.Count && _items[handle.Index]?.Handle.Generation == handle.Generation ? _items[handle.Index] : null;
        public RepairBot Find(EntityId id) => _world.Entities.TryGet(id, out var handle) ? Get(handle) : null;

        public void Launch(ShipEntity ship, RepairBotDevice device)
        {
            var id = SpawnIdentity.CreateChild(ship.Id, _world.Tick, (uint)device.Index, 0, 0);
            var position = _world.Physics.GetPosition(ship.Body);
            var angle = _world.Physics.GetAngle(ship.Body);
            if (Create(ship, device, id, _world.Tick, position, angle) != null)
                _battle.CaptureRepairBotSpawn(ship, device.Index, id, position, angle);
        }

        private RepairBot Create(ShipEntity ship, RepairBotDevice device, EntityId id, long tick, Vec2 position, float angle)
        {
            var index = _free.Count == 0 ? _items.Count : _free.Pop();
            var bot = new RepairBot { Handle = new EntityHandle(EntityKind.RepairBot, index, ++_generation) };
            if (!_world.TryRegister(id, bot.Handle)) { _free.Push(index); return null; }
            var physics = _world.Physics;
            bot.Id = id;
            bot.ShipId = ship.Id;
            bot.DeviceIndex = device.Index;
            bot.Owner = ship.Owner;
            bot.Allegiance = ship.Allegiance;
            bot.Definition = device.Definition;
            bot.SpawnTick = tick;
            bot.HitPoints = device.Definition.HitPoints;
            bot.Body = physics.CreateBody(new BodyDef { Position = position, Angle = angle, Mass = bot.Scale });
            var shape = ShapeDef.Default;
            shape.Filter = CollisionFilters.Drone(ship.Allegiance);
            bot.Shape = physics.AddCircle(bot.Body, new Circle(Vec2.Zero, 0.5f * bot.Scale), shape);
            _world.Shapes.Register(bot.Shape, new ShapeEntry(bot.Handle, id, ShapeHitPriority.Hull, 0));
            if (index == _items.Count) _items.Add(bot); else _items[index] = bot;
            device.Bot = id;
            return bot;
        }

        public bool ApplySpawn(ShipEntity ship, RepairBotDevice device, in OwnerOperation operation)
        {
            if (!device.Bot.IsNull || operation.BarrelIndex != 0 || operation.SpawnSequence != 0 || !operation.HasLaunchPose) return false;
            if (_battle.HasDueRemoval(operation.EntityId)) return true;
            var bot = Create(ship, device, operation.EntityId, operation.Tick, operation.Pose.Position, operation.Pose.Angle);
            if (bot == null) return false;
            // Replay only the bot's motion. Late starts never replay healing against current ship resources.
            for (var tick = operation.Tick + 1; tick < _world.Tick; ++tick)
            {
                Steer(bot, ship, device.Running, tick);
                var physics = _world.Physics;
                physics.SetPose(bot.Body, physics.GetPosition(bot.Body) + Battle.TickDuration * physics.GetLinearVelocity(bot.Body),
                    physics.GetAngle(bot.Body) + Battle.TickDuration * physics.GetAngularVelocity(bot.Body));
            }
            return true;
        }

        // Stage 4. The third second of each three-second cycle repairs, measured from creation, not activation.
        public void Update()
        {
            foreach (var bot in _items)
            {
                if (bot == null) continue;
                var ship = _world.FindShip(bot.ShipId);
                if (ship == null) continue;
                var device = (RepairBotDevice)ship.GetDevice(bot.DeviceIndex);
                bot.Allegiance = ship.Allegiance;
                _world.Physics.SetShapeFilter(bot.Shape, CollisionFilters.Drone(bot.Allegiance));
                if (bot.SpawnTick >= _world.Tick) continue;
                Steer(bot, ship, device.Running, _world.Tick);
                bot.Repairing = device.Running && (_world.Tick - bot.SpawnTick - 1) % 150 >= 100;
                if (bot.Repairing) ship.AddPendingRepair(ship.Armor.Max * bot.Definition.RepairPercent * 0.01f * Battle.TickDuration);
                if (!device.Running && _world.Physics.Torus.Delta(_world.Physics.GetPosition(bot.Body),
                    _world.Physics.GetPosition(ship.Body)).Length < bot.Scale) bot.Removal = RemovalReason.Vanish;
                bot.PreStepVelocity = _world.Physics.GetLinearVelocity(bot.Body);
            }
        }

        private void Steer(RepairBot bot, ShipEntity ship, bool enabled, long tick)
        {
            var physics = _world.Physics;
            var center = physics.GetPosition(ship.Body);
            var delta = physics.Torus.Delta(physics.GetPosition(bot.Body), center);
            var slot = center;
            if (enabled)
            {
                var radius = ship.Definition.Scale * 0.5f + bot.Definition.Range;
                var orbit = (tick - bot.SpawnTick) * Battle.TickDuration * (120f * (float)Math.PI / 180f) / radius;
                slot += radius * new Vec2((float)Math.Cos(orbit), (float)Math.Sin(orbit));
            }
            Satellites.Steer(physics, bot.Body, bot.Definition.Motion, slot, (float)Math.Atan2(delta.Y, delta.X),
                physics.GetLinearVelocity(ship.Body));
        }

        public void Resolve(in ContactCandidate hit, RepairBot bot)
        {
            var bulletHit = _world.Bullets.TryGet(hit.AttackerHandle, out var index);
            var attacker = _world.Ships.Get(hit.AttackerHandle);
            var owner = bulletHit ? _world.Bullets[index].Owner : attacker?.Owner ?? -1;
            var deviceIndex = bulletHit ? _world.Bullets[index].DeviceIndex : hit.PointDefense?.Index ?? -1;
            var perTick = !bulletHit || (_world.Bullets[index].IsBeam
                ? _world.Bullets[index].BeamDefinition.Impact == BulletImpact.OverTime
                : _world.Bullets[index].Definition.Impact == BulletImpact.OverTime);
            var impulse = Vec2.Zero;
            if (bulletHit && !_world.Bullets[index].IsBeam)
            {
                ref readonly var projectile = ref _world.Bullets[index];
                impulse = _battle.PushImpulse(projectile, _world.Physics.GetPosition(bot.Body),
                    projectile.Definition.HasFalloff ? hit.Falloff : 1f);
            }
            if (_world.Setup.IsLocalOwner(bot.Owner))
            {
                if (!_world.Setup.IsLocalOwner(owner) && !_battle.RecordBulletContact(hit.Attacker, bot.Id, perTick ? _world.Tick : -1)) return;
                bot.Damage += hit.Damage;
                if (bulletHit && !_world.Bullets[index].IsBeam)
                {
                    ref readonly var bullet = ref _world.Bullets[index];
                    _world.Physics.ApplyImpulseToCenter(bot.Body, impulse);
                    if (!hit.AttackerPassThrough && !_world.Setup.IsLocalOwner(owner))
                        _battle.ClaimBulletContact(bot.Id, bot.Owner, -1, bullet, HitImpactKind.Stop, 0f);
                }
            }
            else if (_world.Setup.IsLocalOwner(owner))
                _battle.SendBulletContact(new ResolvedHit(_world.BattleId, _world.Tick, hit.Attacker, owner, deviceIndex,
                    bot.Id, bot.Owner, 0, HitContactState.Start, perTick ? HitImpactKind.DamageOverTime : HitImpactKind.Energy,
                    hit.Damage, targetIsBullet: true, impulse: impulse));
        }

        public void ApplyHit(in ResolvedHit hit, RepairBot bot)
        {
            if (!_world.Setup.IsLocalOwner(bot.Owner) || hit.TargetOwner != bot.Owner || hit.Effect != HitEffect.Damage ||
                hit.ImpactKind == HitImpactKind.Disarm || hit.ImpactKind == HitImpactKind.Stop) return;
            if (_battle.RecordBulletContact(hit.AttackerId, bot.Id, hit.ImpactKind == HitImpactKind.DamageOverTime ? hit.Tick : -1))
            {
                bot.Damage += hit.Amount;
                _world.Physics.ApplyImpulseToCenter(bot.Body, hit.Impulse);
                _world.Events.Add(BattleEvent.RemoteHit(_world.BattleId, _world.Tick, _world.Events.Count, hit, hit.Amount));
            }
        }

        // The existing free-projectile correction format also carries bot poses. These entities already have
        // bullet-stream names through their launch records; no new wire fields or codec version are needed.
        public void WriteCorrections(int owner, CorrectionBatch batch)
        {
            var physics = _world.Physics;
            foreach (var bot in _items)
                if (bot != null && bot.Owner == owner)
                    batch.AddMissile(new MissileCorrection(bot.Id, physics.GetPosition(bot.Body), physics.GetAngle(bot.Body),
                        true, physics.GetLinearVelocity(bot.Body), bot.LastSentTick < 0 ? 1e6f : _world.Tick - bot.LastSentTick));
        }

        public bool HoldCorrection(RepairBot bot, int owner, long tick, in MissileCorrection correction)
        {
            if (bot.Owner != owner || tick < bot.SpawnTick || tick <= bot.LastCorrectionTick || tick <= bot.CorrectionTick ||
                !correction.HasVelocity || !correction.Position.IsFinite || !correction.Velocity.IsFinite ||
                float.IsNaN(correction.Angle) || float.IsInfinity(correction.Angle)) return false;
            bot.CorrectionTick = tick;
            bot.Correction = correction;
            return true;
        }

        public void ApplyCorrections()
        {
            foreach (var bot in _items)
            {
                if (bot == null || bot.CorrectionTick < 0 || bot.CorrectionTick >= _world.Tick) continue;
                var physics = _world.Physics;
                physics.SetPose(bot.Body, bot.Correction.Position, bot.Correction.Angle);
                physics.SetLinearVelocity(bot.Body, bot.Correction.Velocity);
                var ship = _world.FindShip(bot.ShipId);
                var device = ship?.GetDevice(bot.DeviceIndex) as RepairBotDevice;
                if (device != null)
                    for (var tick = bot.CorrectionTick + 1; tick < _world.Tick; ++tick)
                    {
                        Steer(bot, ship, device.Running, tick);
                        physics.SetPose(bot.Body, physics.GetPosition(bot.Body) + Battle.TickDuration * physics.GetLinearVelocity(bot.Body),
                            physics.GetAngle(bot.Body) + Battle.TickDuration * physics.GetAngularVelocity(bot.Body));
                    }
                bot.LastCorrectionTick = bot.CorrectionTick;
                bot.CorrectionTick = -1;
            }
        }

        // Stage 10; legacy destroys strictly below zero.
        public void ApplyDamage()
        {
            foreach (var bot in _items)
            {
                if (bot == null || !_world.Setup.IsLocalOwner(bot.Owner)) continue;
                bot.HitPoints -= bot.Damage;
                bot.Damage = 0f;
                if (bot.HitPoints < 0f) bot.Removal = RemovalReason.Death;
            }
        }

        public void RemoveFinished()
        {
            for (var i = 0; i < _items.Count; ++i)
                if (_items[i] != null && _items[i].Removal != RemovalReason.None && _world.Setup.IsLocalOwner(_items[i].Owner))
                    Remove(_items[i], _items[i].Removal, _world.Tick);
        }

        public void Remove(RepairBot bot, RemovalReason reason, long tick)
        {
            var ship = _world.FindShip(bot.ShipId);
            if (ship?.GetDevice(bot.DeviceIndex) is RepairBotDevice device && device.Bot == bot.Id)
            {
                device.Bot = default;
                if (reason == RemovalReason.Death) device.RespawnTick = tick + 1 + device.Definition.RespawnTicks;
            }
            _battle.CaptureRemoval(bot.Id, bot.Owner, reason);
            _world.Shapes.Release(bot.Shape);
            _battle.ForgetContacts(bot.Id);
            _world.Physics.DestroyBody(bot.Body);
            _world.Entities.Remove(bot.Id);
            _items[bot.Handle.Index] = null;
            _free.Push(bot.Handle.Index);
        }

        public void RemoveShip(ShipEntity ship, RemovalReason reason)
        {
            for (var i = 0; i < _items.Count; ++i)
                if (_items[i]?.ShipId == ship.Id) Remove(_items[i], reason, _world.Tick);
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly List<RepairBot> _items = new List<RepairBot>();
        private readonly Stack<int> _free = new Stack<int>();
        private int _generation;
    }
}

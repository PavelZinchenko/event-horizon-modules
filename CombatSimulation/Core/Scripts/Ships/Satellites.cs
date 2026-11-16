using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy Satellite with SatelliteController or AutoAimingSatelliteController (phase 5, step 12c), and Frontier
    // DestructibleSatellite. The mapper gives the slot and the arc limits, so the core never derives a left or right slot.
    public sealed class SatelliteDefinition
    {
        // Legacy SatelliteController: the velocity and angular velocity factors of UnitExtensions.MoveTowards.
        public const float DefaultVelocityFactor = 0.75f;
        public const float DefaultAngularFactor = 3f;

        // Frontier SatelliteFactory: a satellite with fewer hit points has no collider.
        public const float MinHitPoints = 1f;

        public SatelliteDefinition(Vec2 slot, float angle, float mass, float minAngle = 0f, float maxAngle = 0f,
            float velocityFactor = DefaultVelocityFactor, float angularFactor = DefaultAngularFactor, HullShape shape = null,
            float hitPoints = 0f, float respawnTime = 0f)
        {
            if (!slot.IsFinite) throw new ArgumentOutOfRangeException(nameof(slot), "Satellite slot must be finite.");
            if (float.IsNaN(angle) || float.IsInfinity(angle)) throw new ArgumentOutOfRangeException(nameof(angle));
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentOutOfRangeException(nameof(mass), "Satellite mass must be finite and positive.");
            if (float.IsNaN(minAngle) || float.IsInfinity(minAngle) || float.IsNaN(maxAngle) || float.IsInfinity(maxAngle) ||
                maxAngle < minAngle)
                throw new ArgumentOutOfRangeException(nameof(maxAngle), "Satellite arc limits must be finite and ordered.");
            if (!(velocityFactor >= 0f) || float.IsInfinity(velocityFactor)) throw new ArgumentOutOfRangeException(nameof(velocityFactor));
            if (!(angularFactor >= 0f) || float.IsInfinity(angularFactor)) throw new ArgumentOutOfRangeException(nameof(angularFactor));
            if (!(hitPoints >= 0f) || float.IsInfinity(hitPoints)) throw new ArgumentOutOfRangeException(nameof(hitPoints));
            if (!(respawnTime >= 0f) || float.IsInfinity(respawnTime)) throw new ArgumentOutOfRangeException(nameof(respawnTime));

            Slot = slot;
            Angle = angle;
            Mass = mass;
            MinAngle = minAngle;
            MaxAngle = maxAngle;
            VelocityFactor = velocityFactor;
            AngularFactor = angularFactor;
            Shape = shape;
            HitPoints = hitPoints;
            RespawnTime = respawnTime;
            RespawnTicks = respawnTime > 0f ? TickTime.AtLeast(respawnTime) : 0;
            if (shape != null)
                foreach (var part in shape.Parts) Scale = Math.Max(Scale, 2f * part.Reach);
            WeightFactor = 1f / ((float)Math.Sqrt(1f + mass) - 1f);
        }

        // In the ship frame, relative to the body origin.
        public Vec2 Slot { get; }

        // The rest heading relative to the ship heading, in radians (legacy platform Rotation).
        public float Angle { get; }
        public float Mass { get; }

        // Relative to Angle, in radians. An aiming satellite turns its whole body toward its barrel's target while the
        // direction from the slot lies inside Angle + MinAngle .. Angle + MaxAngle; otherwise it keeps Angle.
        public float MinAngle { get; }
        public float MaxAngle { get; }
        public bool IsAiming => MaxAngle > MinAngle;

        // Frontier Steadiness; legacy 0.75.
        public float VelocityFactor { get; }
        public float AngularFactor { get; }

        // The collider. Used only by a destructible satellite: every built-in satellite takes no contacts (owner decision 7).
        public HullShape Shape { get; }

        // Frontier DestructibleSatellite (satellite plan, phase 6). The mapper scales the hit points by the ship's
        // HitPointsMultiplier. Below MinHitPoints, or without a shape, the satellite takes no contacts and never dies.
        public float HitPoints { get; }

        // In seconds; 0: a destroyed satellite stays destroyed until its ship leaves.
        public float RespawnTime { get; }
        public bool IsDestructible => Shape != null && HitPoints >= MinHitPoints;

        internal int RespawnTicks { get; }

        // Twice the reach of the shape: the target scale of a bullet's damage falloff.
        internal float Scale { get; }

        // Legacy MoveTowards: 1 / (sqrt(1 + mass) - 1).
        internal float WeightFactor { get; }
    }

    // A value copy in BattleSnapshot.
    public readonly struct SatelliteState
    {
        public readonly Vec2 Position;
        public readonly float Angle;

        // A destroyed satellite keeps its body and follows its slot, without contacts or shots, until it respawns.
        public readonly bool IsActive;

        // The owner's value; a remote copy keeps the full value until the owner's record destroys it. 0 when destroyed.
        public readonly float HitPoints;

        internal SatelliteState(PhysicsWorld world, Satellite satellite)
        {
            Position = world.GetPosition(satellite.Body);
            Angle = world.GetAngle(satellite.Body);
            IsActive = !satellite.Destroyed;
            HitPoints = satellite.HitPoints;
        }
    }

    internal sealed class Satellite
    {
        public BodyId Body;

        // A destroyed satellite and its barrels are inactive before this tick; long.MaxValue when it never respawns.
        // Built-in satellites are never inactive.
        public long ActiveTick = long.MinValue;
        public bool Destroyed;

        public ShapeId[] Shapes = Array.Empty<ShapeId>();

        // A destructible satellite is a contact target of its own (EntityKind.Satellite); the others have a null Id.
        public EntityId Id;
        public EntityHandle Handle;
        public ShipEntity Ship;
        public int Index;
        public float HitPoints;

        // Stage 9 adds, stage 10 applies; a death waits for stage 12.
        public float Damage;
        public bool PendingDeath;

        // Linear velocity at the end of stage 4 (ramming speed).
        public Vec2 PreStepVelocity;

        public SatelliteDefinition Definition => Ship.Definition.Satellites[Index];
        public int Owner => Ship.Owner;
        public Allegiance Allegiance => Ship.Allegiance;
        public float Scale => Definition.Scale;
    }

    internal static class Satellites
    {
        // Legacy satellite prefabs: Rigidbody2D angular drag.
        private const float AngularDrag = 0.05f;

        // Legacy UnitExtensions.MoveTowards: a satellite farther from its slot jumps there.
        private const float JumpDistance = 50f;

        // The spawn identity source of a satellite: no device has this index.
        private const uint SpawnSource = uint.MaxValue;

        // After the ship pose is set. Legacy SatelliteFactory starts each satellite at the ship centre and heading, at rest.
        // Only a destructible satellite gets its shapes and an entity of its own.
        public static void Add(BattleWorld world, ShipEntity ship)
        {
            var definitions = ship.Definition.Satellites;
            var physics = world.Physics;
            for (var i = 0; i < definitions.Count; ++i)
            {
                var definition = definitions[i];
                var satellite = ship.GetSatellite(i);
                satellite.ActiveTick = long.MinValue;
                satellite.Destroyed = false;
                satellite.Ship = ship;
                satellite.Index = i;
                satellite.Id = default;
                satellite.Handle = default;
                satellite.HitPoints = definition.HitPoints;
                satellite.Damage = 0f;
                satellite.PendingDeath = false;
                satellite.PreStepVelocity = Vec2.Zero;
                satellite.Body = physics.CreateBody(new BodyDef
                {
                    Position = physics.GetPosition(ship.Body),
                    Angle = physics.GetAngle(ship.Body),
                    Mass = definition.Mass,
                });

                // Both devices derive the same ID from the ship's, so a hit record names it without a spawn record.
                var parts = definition.Shape?.Parts;
                var count = definition.IsDestructible &&
                    world.SatelliteStore.Register(satellite, SpawnIdentity.CreateChild(ship.Id, 0, SpawnSource, (uint)i, 0))
                    ? parts.Count : 0;
                if (satellite.Shapes.Length != count) satellite.Shapes = count == 0 ? Array.Empty<ShapeId>() : new ShapeId[count];
                var shapeDef = ShapeDef.Default;
                shapeDef.Filter = Filter(ship, satellite);
                for (var p = 0; p < count; ++p)
                {
                    satellite.Shapes[p] = world.AddHullPart(satellite.Body, parts[p], shapeDef);
                    world.Shapes.Register(satellite.Shapes[p], new ShapeEntry(satellite.Handle, satellite.Id, ShapeHitPriority.Hull, p));
                }
            }
        }

        // Legacy SatelliteController: the ship's death destroys the satellite, a retreat or vanish makes it vanish. The
        // removal event of the ship tells the view which.
        public static void Remove(BattleWorld world, ShipEntity ship)
        {
            for (var i = 0; i < ship.Definition.Satellites.Count; ++i)
            {
                var satellite = ship.GetSatellite(i);
                foreach (var shape in satellite.Shapes) world.Shapes.Release(shape);
                if (!satellite.Id.IsNull) world.SatelliteStore.Release(satellite);
                if (world.Physics.IsAlive(satellite.Body)) world.Physics.DestroyBody(satellite.Body);
                satellite.Body = default;
                satellite.Shapes = Array.Empty<ShapeId>();
                satellite.Ship = null;
            }
        }

        // Ship contact flags (NoContacts) switch satellite contacts off too (Frontier SatelliteBase).
        public static void SetFilters(BattleWorld world, ShipEntity ship)
        {
            for (var i = 0; i < ship.Definition.Satellites.Count; ++i)
            {
                var satellite = ship.GetSatellite(i);
                foreach (var shape in satellite.Shapes)
                    world.Physics.SetShapeFilter(shape, Filter(ship, satellite));
            }
        }

        // Stage 2, before the barrels and devices: Frontier DestructibleSatellite.FakeRespawn moves the satellite to the
        // ship centre and heading with full hit points; it then steers to its slot.
        public static void Respawn(BattleWorld world, ShipEntity ship)
        {
            for (var i = 0; i < ship.Definition.Satellites.Count; ++i)
            {
                var satellite = ship.GetSatellite(i);
                if (!satellite.Destroyed || satellite.ActiveTick > world.Tick) continue;
                var physics = world.Physics;
                satellite.Destroyed = false;
                satellite.HitPoints = satellite.Definition.HitPoints;
                satellite.Damage = 0f;
                physics.SetPose(satellite.Body, physics.GetPosition(ship.Body), physics.GetAngle(ship.Body));
                foreach (var shape in satellite.Shapes) physics.SetShapeFilter(shape, Filter(ship, satellite));
                world.Events.Add(BattleEvent.SatelliteChange(world.BattleId, world.Tick, world.Events.Count, ship, i,
                    physics.GetPosition(satellite.Body), false, world.Authority(ship.Owner)));
            }
        }

        // Stage 4, after every engine: legacy SatelliteController and AutoAimingSatelliteController.UpdatePhysics read
        // the ship after its engine. Each satellite reads only its ship, its own body and its barrel's target. A destroyed
        // satellite keeps following its slot (Frontier DestructibleSatellite keeps its controller).
        public static void Update(BattleWorld world, ShipEntity ship)
        {
            var definitions = ship.Definition.Satellites;
            if (definitions.Count == 0) return;

            var physics = world.Physics;
            var position = physics.GetPosition(ship.Body);
            var rotation = physics.GetRotation(ship.Body);
            var angle = physics.GetAngle(ship.Body);
            var velocity = physics.GetLinearVelocity(ship.Body);
            for (var i = 0; i < definitions.Count; ++i)
            {
                var definition = definitions[i];
                var satellite = ship.GetSatellite(i);
                var slot = position + rotation.Rotate(definition.Slot);
                var course = definition.IsAiming ? Course(world, ship, i, slot, angle) : definition.Angle;
                Steer(physics, satellite.Body, definition, slot, angle + course, velocity);
                satellite.PreStepVelocity = physics.GetLinearVelocity(satellite.Body);
            }
        }

        // Legacy AutoAimingSatelliteController.GetTargetCourse: the heading toward the lead point from the slot, relative
        // to the ship, when it lies inside the arc; else the rest heading.
        private static float Course(BattleWorld world, ShipEntity ship, int index, Vec2 slot, float shipAngle)
        {
            var definition = ship.Definition.Satellites[index];
            var barrelIndex = ship.Definition.AimingBarrel(index);
            if (barrelIndex < 0) return definition.Angle;
            var barrel = ship.GetBarrel(barrelIndex);
            var target = world.FindShip(barrel.Target);
            if (target == null) return definition.Angle;

            var physics = world.Physics;
            var delta = physics.Torus.Delta(slot, physics.GetPosition(target.Body));
            if (barrel.Speed > 0f)
            {
                var velocity = physics.GetLinearVelocity(target.Body) -
                               barrel.RelativeVelocity * physics.GetLinearVelocity(ship.GetSatellite(index).Body);
                delta = BarrelAiming.Lead(delta, velocity, barrel.Speed, target.Definition.Scale * 0.5f);
            }

            var course = (float)Math.Atan2(delta.Y, delta.X) - shipAngle;
            return IsInArc(course, definition.Angle + definition.MinAngle, definition.Angle + definition.MaxAngle)
                ? course : definition.Angle;
        }

        // Legacy RotationHelpers.IsRotationInArc: the angles normalized to one turn, the arc can cross zero.
        private static bool IsInArc(float angle, float min, float max)
        {
            angle = Normalize(angle);
            min = Normalize(min);
            max = Normalize(max);
            return min < max ? angle >= min && angle <= max : angle >= min || angle <= max;
        }

        private static float Normalize(float angle)
        {
            const float circle = (float)(2.0 * Math.PI);
            angle %= circle;
            return angle < 0f ? angle + circle : angle;
        }

        // Legacy UnitExtensions.MoveTowards. The impulses there set the velocities: the ship velocity plus the slot
        // offset times the factors, and the heading error times the factors. Unity's angular drag acts in its step.
        internal static void Steer(PhysicsWorld physics, BodyId body, SatelliteDefinition definition, Vec2 slot,
            float heading, Vec2 shipVelocity)
        {
            var direction = physics.Torus.Delta(physics.GetPosition(body), slot);
            if (direction.Length > JumpDistance)
                physics.SetPose(body, slot, physics.GetAngle(body));
            else
                physics.SetLinearVelocity(body, shipVelocity + definition.VelocityFactor * definition.WeightFactor * direction);

            var angular = BarrelAiming.Delta(physics.GetAngle(body), heading) * definition.AngularFactor * definition.WeightFactor;
            physics.SetAngularVelocity(body, angular / (1f + Battle.TickDuration * AngularDrag));
        }

        // Legacy satellites are drone units. A destroyed satellite touches nothing and no query finds it (Frontier
        // DestructibleSatellite disables its collider).
        private static Filter Filter(ShipEntity ship, Satellite satellite)
        {
            if (satellite.Destroyed) return NoContacts;
            var filter = CollisionFilters.Drone(ship.Allegiance);
            return (ship.Status & ShipStatusFlags.NoContacts) != 0 ? new Filter(filter.Categories, ~0UL) : filter;
        }

        private static readonly Filter NoContacts = new Filter(0, ~0UL);

        // Stage 12 on the owner, or a received death record: Frontier DestructibleSatellite.FakeDestroy, or Destroy
        // without a respawn time. The satellite's barrels stop at once; its body stays.
        internal static void Destroy(BattleWorld world, Satellite satellite, long tick)
        {
            if (satellite.Destroyed) return;
            var ship = satellite.Ship;
            var respawn = satellite.Definition.RespawnTicks;
            satellite.Destroyed = true;
            satellite.PendingDeath = false;
            satellite.HitPoints = 0f;
            satellite.Damage = 0f;
            satellite.ActiveTick = respawn > 0 ? tick + 1 + respawn : long.MaxValue;
            foreach (var shape in satellite.Shapes) world.Physics.SetShapeFilter(shape, NoContacts);
            world.Events.Add(BattleEvent.SatelliteChange(world.BattleId, world.Tick, world.Events.Count, ship,
                satellite.Index, world.Physics.GetPosition(satellite.Body), true, world.Authority(ship.Owner)));
        }
    }

    // The destructible satellites as contact targets (satellite plan, phase 6). Repair bots are the model: the owner
    // keeps the hit points, a remote attacker's contact travels as a hit record, and the owner's death record destroys
    // the remote copy. The respawn tick follows from the death tick on every device, so it needs no record.
    internal sealed class SatelliteStore
    {
        public SatelliteStore(BattleWorld world, Battle battle) { _world = world; _battle = battle; }

        public Satellite Get(EntityHandle handle) => handle.Kind == EntityKind.Satellite && handle.Index >= 0 &&
            handle.Index < _items.Count && _items[handle.Index]?.Handle.Generation == handle.Generation ? _items[handle.Index] : null;
        public Satellite Find(EntityId id) => _world.Entities.TryGet(id, out var handle) ? Get(handle) : null;

        public bool Register(Satellite satellite, EntityId id)
        {
            var index = _free.Count == 0 ? _items.Count : _free.Pop();
            var handle = new EntityHandle(EntityKind.Satellite, index, ++_generation);
            if (!_world.TryRegister(id, handle))
            {
                _free.Push(index);
                return false;
            }
            satellite.Id = id;
            satellite.Handle = handle;
            if (index == _items.Count) _items.Add(satellite); else _items[index] = satellite;
            return true;
        }

        // With its ship. The shapes are released by the caller.
        public void Release(Satellite satellite)
        {
            _battle.ForgetContacts(satellite.Id);
            _world.Entities.Remove(satellite.Id);
            _items[satellite.Handle.Index] = null;
            _free.Push(satellite.Handle.Index);
            satellite.Id = default;
            satellite.Handle = default;
        }

        // Stage 9. As RepairBots.Resolve: Frontier DestructibleSatellite takes the total damage without resistance, and
        // the push of a projectile or a gravity wave.
        public void Resolve(in ContactCandidate hit, Satellite satellite)
        {
            if (satellite.Destroyed) return;
            var bulletHit = _world.Bullets.TryGet(hit.AttackerHandle, out var index);
            var owner = _world.EntityOwner(hit.AttackerHandle);
            var deviceIndex = bulletHit ? _world.Bullets[index].DeviceIndex : hit.PointDefense?.Index ?? -1;
            var perTick = !bulletHit || (_world.Bullets[index].IsBeam
                ? _world.Bullets[index].BeamDefinition.Impact == BulletImpact.OverTime
                : _world.Bullets[index].Definition.Impact == BulletImpact.OverTime);
            var impulse = hit.Impulse;
            if (bulletHit && !_world.Bullets[index].IsBeam)
            {
                ref readonly var projectile = ref _world.Bullets[index];
                impulse = _battle.PushImpulse(projectile, _world.Physics.GetPosition(satellite.Body),
                    projectile.Definition.HasFalloff ? hit.Falloff : 1f);
            }
            if (_world.Setup.IsLocalOwner(satellite.Owner))
            {
                if (!_world.Setup.IsLocalOwner(owner) && !_battle.RecordBulletContact(hit.Attacker, satellite.Id, perTick ? _world.Tick : -1)) return;
                satellite.Damage += hit.Damage;
                if (bulletHit && !_world.Bullets[index].IsBeam)
                {
                    ref readonly var bullet = ref _world.Bullets[index];
                    _world.Physics.ApplyImpulseToCenter(satellite.Body, impulse);
                    if (!hit.AttackerPassThrough && !_world.Setup.IsLocalOwner(owner))
                        _battle.ClaimBulletContact(satellite.Id, satellite.Owner, -1, bullet, HitImpactKind.Stop, 0f);
                }
                else if (hit.GravityWave != null) _world.Physics.ApplyImpulseToCenter(satellite.Body, impulse);
            }
            else if (_world.Setup.IsLocalOwner(owner))
                _battle.SendBulletContact(new ResolvedHit(_world.BattleId, _world.Tick, hit.Attacker, owner, deviceIndex,
                    satellite.Id, satellite.Owner, 0, HitContactState.Start, perTick ? HitImpactKind.DamageOverTime : HitImpactKind.Energy,
                    hit.Damage, targetIsBullet: true, impulse: impulse));
        }

        public void ApplyHit(in ResolvedHit hit, Satellite satellite)
        {
            if (satellite == null || satellite.Destroyed || !_world.Setup.IsLocalOwner(satellite.Owner) ||
                hit.TargetOwner != satellite.Owner || hit.Effect != HitEffect.Damage ||
                hit.ImpactKind == HitImpactKind.Disarm || hit.ImpactKind == HitImpactKind.Stop) return;
            if (_battle.RecordBulletContact(hit.AttackerId, satellite.Id, hit.ImpactKind == HitImpactKind.DamageOverTime ? hit.Tick : -1))
            {
                satellite.Damage += hit.Amount;
                _world.Physics.ApplyImpulseToCenter(satellite.Body, hit.Impulse);
                _world.Events.Add(BattleEvent.RemoteHit(_world.BattleId, _world.Tick, _world.Events.Count, hit, hit.Amount));
            }
        }

        // Stage 10 on the owner. Frontier DestructibleSatellite dies strictly below zero.
        public void ApplyDamage()
        {
            foreach (var satellite in _items)
            {
                if (satellite == null || satellite.Destroyed || !_world.Setup.IsLocalOwner(satellite.Owner)) continue;
                satellite.HitPoints -= satellite.Damage;
                satellite.Damage = 0f;
                if (satellite.HitPoints < 0f) satellite.PendingDeath = true;
            }
        }

        // Stage 12 on the owner, after the ship deaths: a satellite of a removed ship is already gone.
        public void DestroyDead()
        {
            foreach (var satellite in _items)
            {
                if (satellite == null || !satellite.PendingDeath) continue;
                Destroy(satellite, _world.Tick);
                _battle.CaptureRemoval(satellite.Id, satellite.Owner, RemovalReason.Death);
            }
        }

        // A received death record destroys the remote copy at the owner's tick.
        public void Destroy(Satellite satellite, long tick)
        {
            Satellites.Destroy(_world, satellite, tick);
            _battle.ForgetContacts(satellite.Id);
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly List<Satellite> _items = new List<Satellite>();
        private readonly Stack<int> _free = new Stack<int>();
        private int _generation;
    }
}

using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using GameDiagnostics;
using Utilites;

namespace CombatSimulation.Core
{
    // The state that every battle system shares: the tick, physics, the roster, the ship, drone, bullet and segment
    // stores, the entity registry, the shape table and the event batch of the tick. Its changes touch only this state.
    // A change that other systems react to (a ship or bullet removal, an operation record) stays with Battle, which
    // calls them in stage order (phase 5, step 8b, H13).
    internal sealed class BattleWorld
    {
        public BattleWorld(BattleSetup setup)
        {
            Setup = setup;
            Physics = new PhysicsWorld(new WorldSettings
            {
                Width = setup.Width,
                Height = setup.Height,
                Substeps = Battle.PhysicsSubsteps,
            });
            Roster = new Roster(setup);
        }

        public BattleSetup Setup { get; }
        public BattleId BattleId => Setup.BattleId;

        // The next tick to execute.
        public long Tick { get; private set; }

        public PhysicsWorld Physics { get; }
        public Roster Roster { get; }
        public ShipStore Ships { get; } = new ShipStore();

        // Live drones and decoys (phase 5, step 10c) in entity ID order.
        public List<ShipEntity> Drones { get; } = new List<ShipEntity>();

        public BulletStore Bullets { get; } = new BulletStore();
        public TailSegmentStore Segments { get; } = new TailSegmentStore();
        public RepairBots RepairBots { get; set; }
        public Decoys Decoys { get; set; }
        public EntityRegistry Entities { get; } = new EntityRegistry();
        public ShapeTable Shapes { get; } = new ShapeTable();
        public BattleEventBuffer Events { get; } = new BattleEventBuffer();

        // The order of the last deployment. A received deployment raises it to its owner's value.
        public long DeploymentOrder { get; set; }

        // A defect (such as a live duplicate EntityId) throws when set; otherwise it is logged and the operation fails.
        public bool ThrowOnDefect { get; set; }

        public void AdvanceTick() => ++Tick;

        public SpawnSample ReadSpawnSample(ShipEntity ship) => new SpawnSample(Tick, Physics.GetPosition(ship.Body),
            Physics.GetRotation(ship.Body), Physics.GetAngle(ship.Body), Physics.GetLinearVelocity(ship.Body));

        public SpawnPose ReadSpawnPose(ShipEntity ship) => new SpawnPose(Physics.GetPosition(ship.Body),
            Physics.GetAngle(ship.Body), Physics.GetLinearVelocity(ship.Body), Physics.GetAngularVelocity(ship.Body));

        public int EntityOwner(EntityHandle handle) => handle.Kind == EntityKind.Bullet
            ? (Bullets.TryGet(handle, out var index) ? Bullets[index].Owner : -1)
            : handle.Kind == EntityKind.RepairBot ? RepairBots.Get(handle)?.Owner ?? -1
            : handle.Kind == EntityKind.Segment ? Segments.Get(handle)?.Owner ?? -1
            : (Ships.Get(handle)?.Owner ?? -1);

        public BattleEventAuthority Authority(int owner) => Setup.IsLocalOwner(owner)
            ? BattleEventAuthority.Committed : BattleEventAuthority.Predicted;

        public ShipEntity FindShip(EntityId id) =>
            Entities.TryGet(id, out var handle) ? Ships.Get(handle) : null;

        // Roster entries can be empty; live drones and decoys follow them in stable entity ID order.
        public int ShipEntryCount => Roster.Slots.Count + Drones.Count;
        public ShipEntity GetShipEntry(int index) => index < Roster.Slots.Count
            ? Roster.Slots[index].ActiveShip : Drones[index - Roster.Slots.Count];

        public void ReportDefect(string message)
        {
            if (ThrowOnDefect) throw new InvalidOperationException(message);
            Trace.LogError(message);
        }

        public bool TryRegister(EntityId id, EntityHandle handle)
        {
            if (Entities.TryAdd(id, handle)) return true;

            ReportDefect($"Battle {BattleId}: entity ID {id} is already alive.");
            return false;
        }

        public void AddDrone(ShipEntity ship)
        {
            var index = 0;
            while (index < Drones.Count && Drones[index].Id.Value < ship.Id.Value) ++index;
            Drones.Insert(index, ship);
        }

        public void AddShipShapes(ShipEntity ship, Filter filter)
        {
            var shapeDef = ShapeDef.Default;
            shapeDef.Filter = filter;
            var parts = ship.Definition.HullParts;
            for (var i = 0; i < parts.Count; ++i)
            {
                var shape = AddHullPart(ship.Body, parts[i], shapeDef);
                ship.AddShape(shape);
                Shapes.Register(shape, new ShapeEntry(ship.Handle, ship.Id, ShapeHitPriority.Hull, i));
            }
            ShieldRules.AddShapes(this, ship);
        }

        public ShapeId AddHullPart(BodyId body, in HullPart part, in ShapeDef shapeDef)
        {
            switch (part.Type)
            {
                case HullPartType.Circle:
                    return Physics.AddCircle(body, part.Circle, shapeDef);
                case HullPartType.Capsule:
                    return Physics.AddCapsule(body, part.Capsule, shapeDef);
                default:
                    return Physics.AddPolygon(body, part.Polygon, shapeDef);
            }
        }

        public void SetHullFilters(ShipEntity ship)
        {
            var filter = ShipStatusRules.HullFilter(ship);
            for (var i = 0; i < ship.ShapeCount; ++i)
                Physics.SetShapeFilter(ship.GetShape(i), filter);
            Satellites.SetFilters(this, ship);
        }

        // The body that carries a barrel: the hull, or the barrel's satellite (phase 5, step 12c). barrelIndex may be
        // SpawnRequest.ShipCentre or ShipHeading.
        public BodyId CarrierBody(ShipEntity ship, int barrelIndex)
        {
            var satellite = barrelIndex < 0 ? BarrelDefinition.Hull : ship.Definition.Barrels[barrelIndex].Satellite;
            return satellite < 0 ? ship.Body : ship.GetSatellite(satellite).Body;
        }

        // The ship sample for the hull. A satellite keeps no history, so its barrels fire from its present state.
        public SpawnSample CarrierSample(ShipEntity ship, int barrelIndex, in SpawnSample sample)
        {
            var body = CarrierBody(ship, barrelIndex);
            return body.Equals(ship.Body) ? sample : new SpawnSample(sample.Tick, Physics.GetPosition(body),
                Physics.GetRotation(body), Physics.GetAngle(body), Physics.GetLinearVelocity(body));
        }

        // D13: the ship, its hull filters, its attached tail segments and its live bullets and beams take the new
        // allegiance. Legacy bullets read their owner's side, so they change with it. Phase 5 friendly fire must keep
        // its own allegiance here.
        public void SetAllegiance(ShipEntity ship, Allegiance allegiance)
        {
            ship.Allegiance = allegiance;
            if (ship.IsDrone && allegiance == Allegiance.Undefined) ship.MothershipOverride = default(EntityId);
            SetHullFilters(ship);

            for (var i = 0; i < Segments.Capacity; ++i)
            {
                var segment = Segments[i];
                if (!segment.IsAlive || segment.Detached || segment.ShipId != ship.Id) continue;
                segment.Allegiance = ship.Allegiance;
                Physics.SetShapeFilter(segment.Shape, CollisionFilters.Missile(ship.Allegiance));
            }

            for (var i = 0; i < Bullets.Capacity; ++i)
            {
                if (!Bullets.IsAlive(i)) continue;
                ref var bullet = ref Bullets[i];
                if ((bullet.AllegianceOwner ?? bullet.ShipId) != ship.Id) continue;
                bullet.Allegiance = allegiance;
                if (!bullet.IsBeam) Physics.SetBulletFilter(bullet.Body, CollisionFilters.Bullet(bullet.Definition, allegiance));
            }
        }

        // Legacy BulletFactory.ConfigureBody at the muzzle of the sample. A spawn tick before the present advances the
        // bullet on a line, step by step as physics moves it, without hit tests. Returns -1 on an ID collision.
        // With an owner launch pose (P1), the muzzle and angle come from it; the inherited velocity stays the carrier's.
        // barrelIndex may be SpawnRequest.ShipCentre or ShipHeading.
        public int CreateBullet(ShipEntity ship, int deviceIndex, int barrelIndex, uint sequence,
            BulletDefinition definition, float spread, float randomFactor, long spawnTick, in SpawnSample sample,
            out Vec2 muzzle, out Vec2 direction, out float angle, in OwnerOperation launch = default)
        {
            var id = SpawnIdentity.CreateChild(ship.Id, spawnTick, (uint)deviceIndex, SpawnRequest.SpawnSlot(barrelIndex),
                sequence);
            var index = Bullets.Allocate();
            if (!TryRegister(id, Bullets.GetHandle(index)))
            {
                Bullets.Release(index);
                muzzle = direction = default;
                angle = 0f;
                return -1;
            }

            Launch(ship, barrelIndex, id, definition, spread, randomFactor, spawnTick, sample, launch, out muzzle,
                out angle, out direction, out var velocity, out var position);
            DrawShot(id, spawnTick, definition, randomFactor, out var speedScale, out var lifetimeTicks, out var capTicks,
                out var flightLifetime);
            var auraTicks = definition.IsAura ? Auras.StartTicks(definition, spawnTick, Tick) : 0;

            var bodyDef = new BulletDef
            {
                Position = position,
                Angle = angle,
                LinearVelocity = velocity,
                Owner = ship.Body,
                MaxOwnerDistance = definition.IsHarpoon ? definition.Range : 0f,

                // The core selects the first valid hit, so physics reports every target (contact ordering, rule 1).
                PassThrough = true,
                Filter = CollisionFilters.Bullet(definition, ship.Allegiance),
            };
            var body = definition.Wave != null ? Physics.CreateBullet(bodyDef, definition.Wave.Shape)
                : Physics.CreateBullet(bodyDef, definition.IsAura ? Auras.Circle(definition, auraTicks)
                    : new Circle(definition.CircleCenter, definition.Radius));

            // Legacy BulletFactory.ConfigureBody: an attached bullet rides on the barrel, at its pose (phase 5, step 3).
            // Each device attaches it to its own copy of the ship.
            if (definition.AttachedToParent)
            {
                var barrel = ship.Definition.Barrels[barrelIndex];
                Physics.AttachBullet(body, CarrierBody(ship, barrelIndex), new Pose(barrel.PositionAt(ship.GetBarrel(barrelIndex).Angle), Rot.FromAngle(ship.GetBarrel(barrelIndex).Angle)));
            }

            ref var bullet = ref Bullets[index];
            bullet.Id = id;
            bullet.ParentId = ship.Id;
            bullet.ShipId = ship.Id;
            bullet.Side = ship.Side;
            bullet.Allegiance = ship.Allegiance;
            bullet.Owner = ship.Owner;
            bullet.DeviceIndex = deviceIndex;
            bullet.Definition = definition;
            bullet.SpeedScale = speedScale;
            bullet.ExpiryTick = spawnTick + lifetimeTicks - 1;
            bullet.LifetimeCapTicks = capTicks;
            bullet.FlightLifetime = flightLifetime;
            bullet.RenewTick = spawnTick;
            bullet.AuraTicks = auraTicks;
            bullet.HitPoints = definition.HitPoints;
            bullet.SpawnTick = spawnTick;
            bullet.Confirmed = Setup.IsLocalOwner(ship.Owner);

            // Legacy runs no search at creation (its timer check is strict); the first one is in the next tick.
            bullet.NextSearchTick = spawnTick + 1;
            Bullets.SetBody(index, body);
            Waves.Resize(Physics, ref bullet, Tick);

            // A manageable or continuous cannon holds its bullet (phase 5, steps 4b and 4g), as a beam device holds its
            // beam; a charging cannon shows it (step 4f).
            if (barrelIndex >= 0 && ship.GetDevice(deviceIndex) is CannonDevice cannon)
                cannon.OnBulletCreated(Bullets.GetHandle(index));
            return index;
        }

        // The launch of a projectile and its position and velocity at the present tick, before this tick's physics.
        public void Launch(ShipEntity ship, int barrelIndex, EntityId id, BulletDefinition definition, float spread,
            float randomFactor, long spawnTick, in SpawnSample sample, in OwnerOperation launch, out Vec2 muzzle,
            out float angle, out Vec2 direction, out Vec2 velocity, out Vec2 position)
        {
            var carrier = CarrierSample(ship, barrelIndex, sample);
            if (launch.HasLaunchPose && !definition.AttachedToParent)
            {
                muzzle = launch.Pose.Position;
                angle = launch.Pose.Angle;
            }
            else if (barrelIndex < 0)
            {
                muzzle = sample.Position;
                angle = barrelIndex == SpawnRequest.ShipHeading ? sample.Angle : 0f;
            }
            else
            {
                // An attached bullet has no spread (legacy ConfigureBody); the barrel decides its pose.
                var barrel = ship.Definition.Barrels[barrelIndex];
                muzzle = carrier.Position + carrier.Rotation.Rotate(barrel.PositionAt(ship.GetBarrel(barrelIndex).Angle));
                angle = carrier.Angle + ship.GetBarrel(barrelIndex).Angle;
                if (!definition.AttachedToParent)
                {
                    var random = CounterRandom.Create(Setup.MatchSeed, id, spawnTick, RandomPurpose.WeaponSpread);
                    angle += (random.NextFloat() - 0.5f) * spread;
                }
            }

            DrawShot(id, spawnTick, definition, randomFactor, out var speedScale, out _);
            direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            velocity = definition.AttachedToParent
                ? carrier.LinearVelocity
                : BeamBolts.LaunchSpeed(definition, speedScale) * direction +
                  definition.ParentVelocityEffect * carrier.LinearVelocity;

            position = muzzle;
            if (spawnTick < Tick && !definition.AttachedToParent)
            {
                position = Physics.Torus.Wrap(position);
                RocketSteering.Advance(Physics.Torus, definition, speedScale, angle, spawnTick, Tick, ref position,
                    ref velocity);
            }
        }

        // Legacy BulletStats.GetBulletSpeed and GetBulletLifetime with the weapon's RandomFactor (phase 5, step 4a): each
        // varies by (r - 0.5) x factor. The draws come from the shot's identity, so both devices get the same values.
        public void DrawShot(EntityId id, long spawnTick, BulletDefinition definition, float randomFactor,
            out float speedScale, out int lifetimeTicks) =>
            DrawShot(id, spawnTick, definition, randomFactor, out speedScale, out lifetimeTicks, out _);

        // capTicks: the full lifetime, without the loss of a restoring bullet (phase 5, step 5d).
        public void DrawShot(EntityId id, long spawnTick, BulletDefinition definition, float randomFactor,
            out float speedScale, out int lifetimeTicks, out int capTicks)
            => DrawShot(id, spawnTick, definition, randomFactor, out speedScale, out lifetimeTicks, out capTicks, out _);

        public void DrawShot(EntityId id, long spawnTick, BulletDefinition definition, float randomFactor,
            out float speedScale, out int lifetimeTicks, out int capTicks, out float flightLifetime)
        {
            var lifetime = definition.Lifetime;
            speedScale = 1f;
            if (randomFactor > 0f)
            {
                var random = CounterRandom.Create(Setup.MatchSeed, id, spawnTick, RandomPurpose.ShotVariation);
                var speed = random.NextFloat();
                var variation = random.NextFloat();
                speedScale = 1f + (speed - 0.5f) * randomFactor;
                lifetime *= 1f + (variation - 0.5f) * randomFactor;
            }
            lifetimeTicks = definition.SpawnLifetimeTicks(lifetime, speedScale);
            capTicks = TickTime.After(lifetime);
            flightLifetime = lifetime;
        }

        // Returns -1 on an ID collision. The definition is the device's, or its charged copy (phase 5, step 4f).
        public int CreateBeam(ShipEntity ship, BeamDevice device, BeamDefinition definition, uint sequence, long spawnTick)
        {
            var id = SpawnIdentity.CreateChild(ship.Id, spawnTick, (uint)device.Index,
                (uint)device.Definition.BarrelIndex, sequence);
            var index = Bullets.Allocate();
            var handle = Bullets.GetHandle(index);
            if (!TryRegister(id, handle))
            {
                Bullets.Release(index);
                return -1;
            }

            ref var beam = ref Bullets[index];
            beam.Id = id;
            beam.ParentId = ship.Id;
            beam.ShipId = ship.Id;
            beam.Side = ship.Side;
            beam.Allegiance = ship.Allegiance;
            beam.Owner = ship.Owner;
            beam.DeviceIndex = device.Index;
            beam.BeamDefinition = definition;
            beam.SpawnTick = spawnTick;
            beam.Confirmed = Setup.IsLocalOwner(ship.Owner);
            if (definition.IsShot)
            {
                // Phase 5, step 4d: a shot beam expires after its lifetime, as a projectile does.
                beam.ExpiryTick = spawnTick + definition.LifetimeTicks - 1;
                device.OnShotCreated(handle);
                return index;
            }

            beam.ExpiryTick = long.MaxValue; // Device-owned lifetime, without projectile expiry.
            device.Attach(handle);
            return index;
        }
    }
}

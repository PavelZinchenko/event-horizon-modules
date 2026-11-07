using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using Utilites;

namespace CombatSimulation.Core
{
    // Spawns (phase 5, step 8b, item 7). Stage 5 creates the drones, clones, bullets and beams that devices request at
    // stage 2. Battle runs the network records of stage 5 around it. Roster deployment uses the spawn placement.
    internal sealed class SpawnSystem
    {
        // Legacy Scene.FindFreePlace: up to 100 random candidates, at least 40 units from every enemy ship.
        public const int Attempts = 100;
        public const float MinEnemyDistance = 40f;

        // How far TryFindFreePosition may move a spawn to clear an overlap.
        public const float SearchDistance = 40f;

        public SpawnSystem(BattleWorld world, Battle battle, BulletTriggers triggers, BattleNetwork network)
        {
            _world = world;
            _battle = battle;
            _triggers = triggers;
            _network = network;
        }

        private BattleSetup Setup => _world.Setup;
        private BattleId BattleId => _world.BattleId;
        private long Tick => _world.Tick;
        private PhysicsWorld Physics => _world.Physics;

        // Stage 2: a device asks for a ship, a bullet or a beam; stage 5 creates it.
        public void Request(in SpawnRequest request) => _requests.Add(request);

        // Stage 5. Roster ships first, then drones by ID; devices in index order. One spawn slot stays adjacent.
        public void ApplyRequests()
        {
            for (var i = 0; i < _requests.Count; ++i)
            {
                var request = _requests[i];
                var sequence = 0u;
                for (var j = i - 1; j >= 0 && _requests[j].HasSameSlot(request); --j) ++sequence;
                if (request.LaunchesShip) SpawnDrone(request, sequence);
                else
                {
                    BarrelAiming.BeforeShot(_world, request.Ship, request.BarrelIndex, request.Bullet, request.BeamDefinition, _network);
                    if (request.Beam != null) SpawnBeam(request, sequence);
                    else SpawnBullet(request, sequence);
                }
            }

            _requests.Clear();
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

            var body = Physics.CreateBody(new BodyDef { Mass = bay.Drone.Mass });
            ship.InitializeDrone(id, bay, body, Tick, Setup.DamageTypes.Count);
            _world.AddShipShapes(ship, CollisionFilters.Drone(ship.Allegiance));
            if (parent.Definition.Devices[request.DeviceIndex] is ClonningCenterDefinition) PlaceClone(parent, ship);
            else
            {
                var origin = Physics.GetPosition(parent.Body);
                var angle = Physics.GetAngle(parent.Body);
                Physics.SetPose(body, origin, angle);
                Physics.TryFindFreePosition(body, origin, SearchDistance, out var position);
                Physics.SetPose(body, position, angle);
            }

            _world.AddDrone(ship);
            bay.RecordLaunch();
            _world.Events.Add(BattleEvent.Spawn(BattleId, Tick, _world.Events.Count, ship, _world.Authority(ship.Owner)));
            _network?.CaptureDroneSpawn(ship, parent.Id);
            parent.GetDevice(request.DeviceIndex).OnShipLaunched(_battle.DeviceContext, parent, id, Tick);
        }

        // Legacy ClonningDevice.SpawnShip: the mothership position plus its scale in a random direction of whole
        // degrees, that direction as the rotation, and the mothership velocity. Legacy seeds System.Random from a hash
        // code; the core draws the direction counter-based, so it is new, not a copy (approved).
        private void PlaceClone(ShipEntity mothership, ShipEntity clone)
        {
            var random = CounterRandom.Create(Setup.MatchSeed, clone.Id, Tick, RandomPurpose.CloneSpawn);
            var angle = random.NextUint() % 360 * (float)(Math.PI / 180.0);
            var direction = Rot.FromAngle(angle);
            var position = Physics.GetPosition(mothership.Body) + mothership.Definition.Scale * new Vec2(direction.C, direction.S);
            Physics.SetPose(clone.Body, Physics.Torus.Wrap(position), angle);
            Physics.SetLinearVelocity(clone.Body, Physics.GetLinearVelocity(mothership.Body));
        }

        // Legacy BulletFactory.ConfigureBody at the muzzle pose, plus the Q1 recoil (owner, 2026-10-20; phase 4 E1):
        // Recoil x the bullet velocity relative to the ship (legacy BulletFactory.ApplyRecoil), at the muzzle.
        private void SpawnBullet(in SpawnRequest request, uint sequence)
        {
            var ship = request.Ship;
            var sample = _world.ReadSpawnSample(ship);
            var index = _world.CreateBullet(ship, request.DeviceIndex, request.BarrelIndex, sequence, request.Bullet,
                request.Spread, request.RandomFactor, Tick, sample, out var muzzle, out var direction, out var angle);
            if (index < 0) return;

            var definition = request.Bullet;
            if (definition.Recoil > 0f && !definition.AttachedToParent)
            {
                var relative = _world.Bullets[index].Speed * definition.StartingVelocityMultiplier * direction +
                               (definition.ParentVelocityEffect - 1f) * sample.LinearVelocity;
                Physics.ApplyImpulse(ship.Body, -definition.Recoil * relative, muzzle);
            }

            ref readonly var bullet = ref _world.Bullets[index];
            _world.Events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _world.Events.Count, bullet, _world.Authority(bullet.Owner)));
            _network?.CaptureBulletSpawn(bullet, bullet.DeviceIndex, (int)SpawnRequest.SpawnSlot(request.BarrelIndex),
                sequence, Tick, !definition.IsHoming, muzzle, angle, request.ChargeTicks);
            _triggers.RunCreatedTriggers(index);
        }

        private void SpawnBeam(in SpawnRequest request, uint sequence)
        {
            var index = _world.CreateBeam(request.Ship, request.Beam, request.BeamDefinition, sequence, Tick);
            if (index < 0) return;
            ref readonly var beam = ref _world.Bullets[index];
            _world.Events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _world.Events.Count, beam, _world.Authority(beam.Owner)));
            _network?.CaptureBulletSpawn(beam, beam.DeviceIndex, request.BarrelIndex, sequence, Tick,
                chargeTicks: request.ChargeTicks);
        }

        // Legacy Scene.FindFreePlace and ShipInfo.Create, with counter-based random.
        public void PlaceRosterShip(BodyId body, EntityId id, int side)
        {
            var random = CounterRandom.Create(Setup.MatchSeed, id, Tick, RandomPurpose.SpawnPlacement);
            var angle = random.NextUint() % 360 * (float)(Math.PI / 180.0);

            var best = Vec2.Zero;
            var bestDistance = float.MinValue;
            for (var i = 0; i < Attempts; ++i)
            {
                var candidate = Physics.Torus.Wrap(new Vec2(random.NextFloat() * Setup.Width, random.NextFloat() * Setup.Height));
                var distance = DistanceToNearestShip(candidate, 1 - side);
                if (distance > bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
                if (distance >= MinEnemyDistance) break;
            }

            Physics.SetPose(body, best, angle);
            Physics.TryFindFreePosition(body, best, SearchDistance, out var position);
            Physics.SetPose(body, position, angle);
        }

        private float DistanceToNearestShip(Vec2 position, int side)
        {
            var nearest = float.MaxValue;
            var slots = _world.Roster.Slots;
            for (var i = 0; i < slots.Count; ++i)
            {
                var ship = slots[i].ActiveShip;
                if (ship == null || ship.Side != side) continue;
                var distance = Physics.Torus.Delta(position, Physics.GetPosition(ship.Body)).Length;
                if (distance < nearest) nearest = distance;
            }
            return nearest;
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly BulletTriggers _triggers;
        // Null without remote owners (decision 3b).
        private readonly BattleNetwork _network;
        private readonly List<SpawnRequest> _requests = new List<SpawnRequest>();
    }
}

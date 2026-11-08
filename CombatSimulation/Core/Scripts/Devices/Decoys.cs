using System;
using CombatSimulation.Physics;
using Utilites;

namespace CombatSimulation.Core
{
    // Decoys (legacy Decoy unit; phase 5, step 10c). A decoy is a ship entity in the drone list, so the searches, the
    // contacts, the snapshot and the removal see it as a ship. It has no slot, bay or pilot. On the network it follows
    // the repair bot's conventions (H8): the owner sends a launch record with its position and launch angle, contacts
    // that involve a remote owner travel as auxiliary contacts (TargetIsBullet), and poses travel in the missile
    // correction format. Its hit points belong to its owner.
    internal sealed class Decoys
    {
        // Legacy Decoy.OnUpdatePhysics: the velocity falls by itself per second, the spin by 10% of itself.
        public const float SpinDamping = 0.1f;

        public Decoys(BattleWorld world, Battle battle)
        {
            _world = world;
            _battle = battle;
        }

        private BattleSetup Setup => _world.Setup;
        private PhysicsWorld Physics => _world.Physics;
        private long Tick => _world.Tick;

        // Stage 5. Legacy DecoyDevice.CreateDecoys: one unit from the ship centre in the launch direction, with the
        // ship velocity plus the direction x Range, at rotation 0.
        public void Spawn(ShipEntity parent, int deviceIndex, int number)
        {
            if (!Setup.IsLocalOwner(parent.Owner)) return;
            var definition = ((DecoyDevice)parent.GetDevice(deviceIndex)).Definition;
            var id = SpawnIdentity.CreateChild(parent.Id, Tick, (uint)deviceIndex, (uint)number, 0);
            var angle = definition.LaunchAngle(number, Physics.GetAngle(parent.Body));
            var direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            var position = Physics.Torus.Wrap(Physics.GetPosition(parent.Body) + direction);
            var velocity = Physics.GetLinearVelocity(parent.Body) + definition.Range * direction;
            if (Create(parent, definition, id, Tick, position, velocity) != null)
                _battle.CaptureDecoySpawn(parent, deviceIndex, number, id, position, angle);
        }

        // The copy builds the decoy from the owner's launch record: its position and launch angle, and the parent's
        // velocity at the launch tick. A late record replays the drift up to this tick; corrections remove the rest.
        public bool ApplySpawn(ShipEntity parent, DecoyDevice device, in OwnerOperation operation)
        {
            var definition = device.Definition;
            if (operation.BarrelIndex >= definition.Count || operation.SpawnSequence != 0 || !operation.HasLaunchPose)
                return false;
            device.OnRemoteLaunch(operation.Tick);
            if (_battle.HasDueRemoval(operation.EntityId)) return true;
            if (!parent.TryGetSpawnSample(operation.Tick, out var sample)) sample = _world.ReadSpawnSample(parent);
            var angle = operation.Pose.Angle;
            var velocity = sample.LinearVelocity + definition.Range * new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            var decoy = Create(parent, definition, operation.EntityId, operation.Tick, operation.Pose.Position, velocity);
            if (decoy == null) return false;
            Replay(decoy, operation.Tick);
            return true;
        }

        private ShipEntity Create(ShipEntity parent, DecoyDefinition definition, EntityId id, long tick, Vec2 position,
            Vec2 velocity)
        {
            var ship = _world.Ships.Allocate();
            if (!_world.TryRegister(id, ship.Handle))
            {
                _world.Ships.Release(ship);
                return null;
            }

            // Legacy draws both from the unseeded UnityEngine.Random: Random.Range(-90, 90) in whole degrees per second,
            // and Lifetime x (value x 0.5 + 0.75). Owner question 4: counter-based draws.
            var spinRandom = CounterRandom.Create(Setup.MatchSeed, id, tick, RandomPurpose.DecoySpin);
            var spin = (int)(spinRandom.NextUint() % 180) - 90;
            var lifetimeRandom = CounterRandom.Create(Setup.MatchSeed, id, tick, RandomPurpose.DecoyLifetime);
            var share = lifetimeRandom.NextFloat();
            var lifetime = TickTime.AtLeast(definition.Lifetime * (share * 0.5f + 0.75f));

            var body = Physics.CreateBody(new BodyDef { Mass = definition.Ship.Mass });
            ship.InitializeDecoy(id, parent, definition, body, tick, tick + lifetime, Setup.DamageTypes.Count);
            _world.AddShipShapes(ship, CollisionFilters.Drone(ship.Allegiance));
            Physics.SetPose(body, position, 0f);
            Physics.SetLinearVelocity(body, velocity);
            Physics.SetAngularVelocity(body, spin * (float)(Math.PI / 180.0));
            _world.AddDrone(ship);
            _world.Events.Add(BattleEvent.Spawn(_world.BattleId, _world.Tick, _world.Events.Count, ship,
                _world.Authority(ship.Owner)));
            return ship;
        }

        // Stage 4, from the tick after the spawn.
        public void Update(ShipEntity decoy)
        {
            if (decoy.Decoy.SpawnTick >= Tick) return;
            var linear = Physics.GetLinearVelocity(decoy.Body);
            var angular = Physics.GetAngularVelocity(decoy.Body);
            Damp(ref linear, ref angular);
            Physics.SetLinearVelocity(decoy.Body, linear);
            Physics.SetAngularVelocity(decoy.Body, angular);
        }

        private static void Damp(ref Vec2 linear, ref float angular)
        {
            linear -= Battle.TickDuration * linear;
            angular -= SpinDamping * Battle.TickDuration * angular;
        }

        // The ticks from the given one up to this one, without contacts: the step after it and the drift after that.
        private void Replay(ShipEntity decoy, long from)
        {
            var position = Physics.GetPosition(decoy.Body);
            var angle = Physics.GetAngle(decoy.Body);
            var linear = Physics.GetLinearVelocity(decoy.Body);
            var angular = Physics.GetAngularVelocity(decoy.Body);
            for (var tick = from; tick < Tick; ++tick)
            {
                if (tick > decoy.Decoy.SpawnTick) Damp(ref linear, ref angular);
                position += Battle.TickDuration * linear;
                angle += Battle.TickDuration * angular;
            }
            Physics.SetPose(decoy.Body, Physics.Torus.Wrap(position), angle);
            Physics.SetLinearVelocity(decoy.Body, linear);
            Physics.SetAngularVelocity(decoy.Body, angular);
        }

        // Stage 10. Legacy Decoy.Affect: the hit points fall by the total damage without resistances; the decoy dies
        // strictly below zero. Repair, recharge and drains do nothing (legacy Stats is null). A copy dies only by its
        // owner's record.
        public void ApplyDamage(ShipEntity decoy)
        {
            var total = 0f;
            var damage = decoy.PendingDamage;
            var bypass = decoy.PendingDamageIgnoringShield;
            for (var type = 0; type < damage.Length; ++type)
                total = (float)Math.Min(float.MaxValue, (double)total + damage[type] + bypass[type]);
            if (!(total > 0f)) return;
            if (Setup.IsLocalOwner(decoy.Owner) && total > decoy.Armor.Value) decoy.PendingDeath = true;
            decoy.Armor.Consume(total);
        }

        // A contact on a decoy with a remote attacker or a remote decoy: neither copy applies the full ship rule.
        public bool IsRemoteContact(in ContactCandidate hit, out ShipEntity decoy)
        {
            decoy = _world.Ships.Get(hit.TargetHandle);
            if (decoy == null || !decoy.IsDecoy) return false;
            return !Setup.IsLocalOwner(decoy.Owner) || !Setup.IsLocalOwner(_world.EntityOwner(hit.AttackerHandle));
        }

        // As RepairBots.Resolve: the decoy's owner applies the damage and push of a remote attacker's contact it sees
        // first; a local attacker sends its contact on a remote decoy to the decoy's owner. The slow-down and roll of
        // these contacts are left to the pose corrections.
        public void Resolve(in ContactCandidate hit, ShipEntity decoy)
        {
            var bulletHit = _world.Bullets.TryGet(hit.AttackerHandle, out var index);
            var owner = _world.EntityOwner(hit.AttackerHandle);
            var deviceIndex = bulletHit ? _world.Bullets[index].DeviceIndex : hit.AttackerDeviceIndex;
            var perTick = !bulletHit || (_world.Bullets[index].IsBeam
                ? _world.Bullets[index].BeamDefinition.Impact == BulletImpact.OverTime
                : _world.Bullets[index].Definition.Impact == BulletImpact.OverTime);
            var impulse = Vec2.Zero;
            if (bulletHit && !_world.Bullets[index].IsBeam)
            {
                ref readonly var projectile = ref _world.Bullets[index];
                impulse = _battle.PushImpulse(projectile, Physics.GetPosition(decoy.Body),
                    projectile.Definition.HasFalloff ? hit.Falloff : 1f);
            }

            if (Setup.IsLocalOwner(decoy.Owner))
            {
                if (!_battle.RecordBulletContact(hit.Attacker, decoy.Id, perTick ? Tick : -1)) return;
                decoy.AddPendingDamage(hit.DamageType, hit.Damage);
                if (impulse.X != 0f || impulse.Y != 0f) _battle.ApplyPush(decoy, impulse);
                if (bulletHit && !_world.Bullets[index].IsBeam && !hit.AttackerPassThrough)
                    _battle.ClaimBulletContact(decoy.Id, decoy.Owner, -1, _world.Bullets[index], HitImpactKind.Stop, 0f);
            }
            else if (Setup.IsLocalOwner(owner))
                _battle.SendBulletContact(new ResolvedHit(_world.BattleId, Tick, hit.Attacker, owner, deviceIndex,
                    decoy.Id, decoy.Owner, 0, HitContactState.Start, perTick ? HitImpactKind.DamageOverTime : HitImpactKind.Energy,
                    hit.Damage, targetIsBullet: true, damageType: (byte)hit.DamageType, impulse: impulse));
        }

        // A remote attacker's contact record on a local decoy, once per contact.
        public void ApplyHit(in ResolvedHit hit, ShipEntity decoy)
        {
            if (!Setup.IsLocalOwner(decoy.Owner) || hit.TargetOwner != decoy.Owner || hit.Effect != HitEffect.Damage ||
                hit.ImpactKind == HitImpactKind.Disarm || hit.ImpactKind == HitImpactKind.Stop) return;
            if (!_battle.RecordBulletContact(hit.AttackerId, decoy.Id,
                    hit.ImpactKind == HitImpactKind.DamageOverTime ? hit.Tick : -1)) return;
            decoy.AddPendingDamage(hit.DamageType, hit.Amount);
            if (hit.Impulse.X != 0f || hit.Impulse.Y != 0f) _battle.ApplyPush(decoy, hit.Impulse);
            _world.Events.Add(BattleEvent.RemoteHit(_world.BattleId, Tick, _world.Events.Count, hit, hit.Amount));
        }

        // The owner's decoy poses, in the missile correction format with velocity. A decoy never sent comes first.
        public void WriteCorrections(int owner, CorrectionBatch batch)
        {
            foreach (var decoy in _world.Drones)
            {
                if (!decoy.IsDecoy || decoy.Owner != owner || decoy.PendingDeath) continue;
                var life = decoy.Decoy;
                batch.AddMissile(new MissileCorrection(decoy.Id, Physics.GetPosition(decoy.Body), Physics.GetAngle(decoy.Body),
                    true, Physics.GetLinearVelocity(decoy.Body), life.LastSentTick < 0 ? 1e6f : Tick - life.LastSentTick));
            }
        }

        public bool CorrectionSent(EntityId id, long tick)
        {
            var decoy = _world.FindShip(id);
            if (decoy == null || !decoy.IsDecoy) return false;
            decoy.Decoy.LastSentTick = tick;
            return true;
        }

        // False when the entity is not a decoy; held is then whether the correction was kept.
        public bool TryHoldCorrection(int owner, long tick, in MissileCorrection correction, out bool held)
        {
            held = false;
            var decoy = _world.FindShip(correction.EntityId);
            if (decoy == null || !decoy.IsDecoy) return false;
            var life = decoy.Decoy;
            if (decoy.Owner != owner || tick < life.SpawnTick || tick <= life.LastCorrectionTick ||
                tick <= life.CorrectionTick || !correction.HasVelocity || !correction.Position.IsFinite ||
                !correction.Velocity.IsFinite || float.IsNaN(correction.Angle) || float.IsInfinity(correction.Angle)) return true;
            life.CorrectionTick = tick;
            life.Correction = correction;
            held = true;
            return true;
        }

        // Stage 1: the owner's pose at the end of its tick, then the drift of the ticks after it. The spin stays the
        // copy's: it comes from the same counter-based draw.
        public void ApplyCorrections()
        {
            foreach (var decoy in _world.Drones)
            {
                if (!decoy.IsDecoy) continue;
                var life = decoy.Decoy;
                if (life.CorrectionTick < 0 || life.CorrectionTick >= Tick) continue;
                Physics.SetPose(decoy.Body, life.Correction.Position, life.Correction.Angle);
                Physics.SetLinearVelocity(decoy.Body, life.Correction.Velocity);
                Replay(decoy, life.CorrectionTick + 1);
                life.LastCorrectionTick = life.CorrectionTick;
                life.CorrectionTick = -1;
            }
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
    }
}

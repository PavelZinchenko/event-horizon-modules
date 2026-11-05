using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Contacts (phase 5, step 8b, item 7). Stage 6: the beam, beam bolt (step 4e) and point-defense queries. Stage 8: every source adds its
    // contact candidates. Stage 9: the resolver accepts them in key order, and each accepted contact reaches its
    // target: pending damage and a push on a ship, damage or removal on a bullet, a queued hit on a tail segment.
    // Stage 10 applies the damage (DamageSystem).
    internal sealed class ContactSystem
    {
        public ContactSystem(BattleWorld world, Battle battle, WormTails tails, BulletTriggers triggers,
            BattleNetwork network, BulletContactLedger ledger)
        {
            _world = world;
            _battle = battle;
            _tails = tails;
            _triggers = triggers;
            _network = network;
            _ledger = ledger;
        }

        private BattleSetup Setup => _world.Setup;
        private BattleId BattleId => _world.BattleId;
        private long Tick => _world.Tick;
        private PhysicsWorld Physics => _world.Physics;

        public ContactResolver Resolver => _resolver;
        public RammingTracker Ramming => _ramming;
        public BeamQueries Beams => _beams;
        public BulletContactLedger Ledger => _ledger;

        // Stage 6.
        public void RunQueries()
        {
            _beams.Run(_battle);
            _bolts.Run(_battle);
            _pointDefense.Run(_battle);
        }

        // Stage 8. Every source adds its candidates; the resolver sorts them by the contact key.
        public void Collect()
        {
            _resolver.BeginStep();
            _beams.Collect(_resolver);
            _bolts.Collect(_resolver);
            _pointDefense.Collect(_resolver);
            _world.Bullets.CollectHits(Physics, _world.Shapes, _world.Ships, _world.Segments, _resolver);
            _world.Bullets.CollectStuckHits(Physics, _world.Entities, _world.Shapes, _world.Ships, _world.Segments, _resolver);
            _ramming.Collect(Physics, _world.Shapes, _world.Ships, _world.Segments, _resolver, Setup.DamageTypes.RammingDamageType);

            var duplicates = _resolver.Sort();
            if (duplicates > 0)
                _world.ReportDefect($"Battle {BattleId}, tick {Tick}: {duplicates} contact candidates share a key.");
        }

        // Stage 9. Ship contacts belong to the attacker; bullet damage belongs to the target bullet.
        public void Resolve()
        {
            var previousRoll = _previousRoll;
            _previousRoll = _currentRoll;
            _currentRoll = previousRoll;
            _currentRoll.Clear();
            _resolver.Resolve();
            _beams.Complete(_battle, _resolver);
            _bolts.Complete(_battle, _resolver);
            _network?.OutgoingHits.BeginContacts();

            var accepted = _resolver.Accepted;
            for (var i = 0; i < accepted.Length; ++i)
            {
                ref readonly var hit = ref accepted[i];
                _battle.ObserveContact(hit);
                if (hit.Kind == ContactKind.Disarm)
                {
                    if (!_world.Bullets.TryGet(hit.TargetHandle, out var disarmed)) continue;
                    // Legacy: point defense is a contact, so Hit triggers run before Disarmed ones. A bullet that cannot
                    // be disarmed counts each zap once, whether this device or a remote one saw it.
                    if (hit.TargetKeepsFlying)
                    {
                        if (_ledger.Record(hit.Attacker, hit.Target, Tick)) _triggers.AddPendingHit(disarmed);
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
                        _network?.ClaimBulletContact(hit.Attacker, defender.Owner, hit.AttackerDeviceIndex,
                            _world.Bullets[disarmed], HitImpactKind.Disarm, 0f);
                    continue;
                }
                if (hit.Kind != ContactKind.Hit) continue;

                // Keep the first body as the ray endpoint even when this shot already hit it.
                if (hit.Source == ContactSource.Ray && _world.Bullets.TryGet(hit.AttackerHandle, out var beamIndex))
                {
                    ref var beam = ref _world.Bullets[beamIndex];
                    if (beam.IsBeam)
                    {
                        if (beam.BeamDefinition.Impact == BulletImpact.AllTargets)
                        {
                            if (beam.BeamHitTargets == null) beam.BeamHitTargets = new HashSet<EntityId>();
                            if (!beam.BeamHitTargets.Add(hit.Target)) continue;
                        }
                        _triggers.AddPendingHit(beamIndex);
                    }
                }

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
                    var perTick = (ray && (!attackerBullet || !_world.Bullets[attackerIndex].IsBeam ||
                        _world.Bullets[attackerIndex].BeamDefinition.Impact == BulletImpact.OverTime)) || (attackerBullet && !_world.Bullets[attackerIndex].IsBeam &&
                                          _world.Bullets[attackerIndex].Definition.Impact == BulletImpact.OverTime);
                    if (authority == BattleEventAuthority.Committed)
                    {
                        // A remote attacker's contact counts once, whether this device or the attacker's owner saw it.
                        if (Setup.IsLocalOwner(attackerOwner) || _ledger.Record(hit.Attacker, hit.Target, perTick ? Tick : -1))
                        {
                            _world.Bullets.AddDamage(hit.TargetHandle, hit.Damage);
                            _triggers.AddPendingHit(index);
                        }
                        // A remote projectile that stops at a local bullet stops on its owner too.
                        if (attackerBullet && !hit.AttackerPassThrough && !ray)
                            _network?.ClaimBulletContact(hit.Target, targetBullet.Owner, -1, _world.Bullets[attackerIndex],
                                HitImpactKind.Stop, 0f);
                    }
                    else
                    {
                        // Resolver walk damage decides predicted removal without changing remote hit points.
                        if (_resolver.GetStopTime(hit.TargetHandle) < float.PositiveInfinity)
                            _world.Bullets.MarkRemoval(hit.TargetHandle, RemovalReason.Death);
                        if (attackerOwner >= 0)
                            _network?.ClaimBulletContact(hit.Attacker, attackerOwner, hit.AttackerDeviceIndex, targetBullet,
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
                    // Drains and siphon (phase 5, step 5a): a hit applies them once; over time, per tick.
                    var effects = default(ImpactEffects);
                    var effectScale = 1f;
                    var attackerShip = default(EntityId);
                    var hostile = true;
                    if (_world.Bullets.TryGet(hit.AttackerHandle, out var index))
                    {
                        ref readonly var attacker = ref _world.Bullets[index];
                        owner = attacker.Owner;
                        deviceIndex = attacker.DeviceIndex;
                        attackerShip = attacker.ShipId;
                        hostile = Allegiances.IsHostile(attacker.Allegiance, target.Allegiance);
                        kind = attacker.IsBeam ? HitImpactKind.DamageOverTime :
                            attacker.Definition.IsMissile ? HitImpactKind.Impact : HitImpactKind.Energy;
                        if (attacker.IsBeam)
                        {
                            amount = attacker.BeamDefinition.DamagePerSecond;
                            effects = attacker.BeamDefinition.Effects;
                            effectScale = attacker.BeamDefinition.Impact == BulletImpact.OverTime ? Battle.TickDuration : 1f;
                            if (attacker.BeamDefinition.Impact != BulletImpact.OverTime) kind = HitImpactKind.Energy;
                            // The ray candidate includes repair's enemy damage for missile and segment targets.
                            if (hostile && effects.RepairDamage > 0f) damage -= effects.RepairDamage * effectScale;
                        }
                        else if (attacker.Definition.Impact == BulletImpact.OverTime)
                        {
                            // One damage type at a fixed rate (BulletDefinition). The record names shape 0, so the
                            // interval stays one contact while the bullet moves over the hull parts.
                            kind = HitImpactKind.DamageOverTime;
                            amount = attacker.Definition.Damage;
                            damage = amount * Battle.TickDuration;
                            damageType = attacker.Definition.DamageType;
                            shape = 0;
                            effects = attacker.Definition.Effects;
                            effectScale = Battle.TickDuration;
                        }
                        else
                        {
                            // The candidate holds the total; a ship takes each damage effect with its own type.
                            projectile = attacker.Definition;
                            if (projectile.HasFalloff) falloff = hit.Falloff;
                            amount = damage = BulletStore.EffectDamage(projectile, projectile.Damage,
                                projectile.DamageFalloff, falloff);
                            damageType = projectile.DamageType;
                            impulse = PushImpulse(attacker, Physics.GetPosition(target.Body), falloff);
                            effects = projectile.Effects;
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
                        var repairDamage = hostile ? effects.RepairDamage : 0f;
                        if (effects.RepairDamageType == damageType)
                        {
                            amount += repairDamage;
                            damage += repairDamage * effectScale;
                        }
                        else if (repairDamage > 0f)
                        {
                            target.AddPendingDamage(effects.RepairDamageType, repairDamage * effectScale);
                            if (remote)
                                _network.OutgoingHits.Add(new ResolvedHit(BattleId, Tick, hit.Attacker, owner, deviceIndex,
                                    hit.Target, target.Owner, shape, HitContactState.Start, kind, repairDamage,
                                    damageType: (byte)effects.RepairDamageType));
                        }
                        // Motion changes precede push, both here and in the outgoing record order (step 5b).
                        if (effects.SlowDown > 0f)
                        {
                            ApplySlowDown(Physics, target, effects.SlowDown * effectScale);
                            if (remote)
                                AddEffectRecord(hit, owner, deviceIndex, target, shape, kind, HitEffect.SlowDown,
                                    effects.SlowDown);
                        }
                        if (effects.Roll > 0f)
                        {
                            var pair = new EntityPair(hit.Attacker, hit.Target);
                            if (_currentRoll.Add(pair) && !_previousRoll.Contains(pair))
                            {
                                var degrees = effects.Roll / (float)Math.Sqrt(target.Definition.Mass) * 180f;
                                ApplyRoll(Physics, target, degrees);
                                if (remote)
                                    AddEffectRecord(hit, owner, deviceIndex, target, shape, HitImpactKind.Energy,
                                        HitEffect.Roll, degrees);
                            }
                        }
                        target.AddPendingDamage(damageType, damage);
                        if (remote)
                        {
                            _network.OutgoingHits.Add(new ResolvedHit(BattleId, Tick, hit.Attacker, owner, deviceIndex,
                                hit.Target, target.Owner, shape, HitContactState.Start, kind, amount,
                                damageType: (byte)damageType, impulse: impulse));
                            // The ramp (step 5c) is in this tick's damage; its interval on shape 0 lasts while the
                            // ray stays on the target, so the target owner counts the ramp from its first tick.
                            if (effects.Ramp > 0f)
                                _network.OutgoingHits.Add(new ResolvedHit(BattleId, Tick, hit.Attacker, owner,
                                    deviceIndex, hit.Target, target.Owner, 0, HitContactState.Start, kind, effects.Ramp,
                                    damageType: (byte)damageType, effect: HitEffect.ProgressiveDamage,
                                    rampTime: effects.RampTime));
                        }
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
                        var energyDrain = effects.EnergyDrain + (hostile ? effects.EnemyEnergyDrain : 0f);
                        var shieldDrain = effects.ShieldDrain + (hostile ? effects.EnemyShieldDrain : 0f);
                        if (energyDrain > 0f || shieldDrain > 0f)
                        {
                            target.AddPendingDrain(energyDrain * effectScale, shieldDrain * effectScale);
                            if (remote)
                            {
                                AddEffectRecord(hit, owner, deviceIndex, target, shape, kind, HitEffect.EnergyDrain,
                                    energyDrain);
                                AddEffectRecord(hit, owner, deviceIndex, target, shape, kind, HitEffect.ShieldDrain,
                                    shieldDrain);
                            }
                        }
                        if (!hostile)
                        {
                            target.AddPendingRepair(effects.Repair * effectScale);
                            target.AddPendingRecharge(effects.EnergyRecharge * effectScale, effects.ShieldRecharge * effectScale);
                            if (remote)
                            {
                                AddEffectRecord(hit, owner, deviceIndex, target, shape, kind, HitEffect.Repair, effects.Repair);
                                AddEffectRecord(hit, owner, deviceIndex, target, shape, kind, HitEffect.EnergyRecharge,
                                    effects.EnergyRecharge);
                                AddEffectRecord(hit, owner, deviceIndex, target, shape, kind, HitEffect.ShieldRecharge,
                                    effects.ShieldRecharge);
                            }
                        }
                        if (effects.Siphon > 0f) Siphon(attackerShip, target, damageType, damage * effects.Siphon);
                        // A hit restores Restore seconds; over time, Restore seconds per second: Restore ticks per tick.
                        if (effects.Restore > 0f)
                            RestoreLifetime(ref _world.Bullets[index], kind == HitImpactKind.DamageOverTime
                                ? effects.Restore : TickTime.Ticks(effects.Restore));
                    }
                }

                _world.Events.Add(BattleEvent.HitEvent(BattleId, Tick, _world.Events.Count, hit, authority));
            }
            _network?.OutgoingHits.CompleteContacts(Tick);
        }

        // A ship effect on a remote target: its own record, or its own interval with the rate as amount.
        private void AddEffectRecord(in ContactCandidate hit, int owner, int deviceIndex, ShipEntity target, int shape,
            HitImpactKind kind, HitEffect effect, float amount)
        {
            if (amount > 0f)
                _network.OutgoingHits.Add(new ResolvedHit(BattleId, Tick, hit.Attacker, owner, deviceIndex, hit.Target,
                    target.Owner, shape, HitContactState.Start, kind, amount, effect: effect));
        }

        // Legacy SiphonHitPointsAction: the attacker's ship regains the damage less the target's armor resistance of
        // its type, whatever the target's shield absorbs (finding 7a). The attacker's owner heals its own ship; a copy
        // follows corrections, so no record travels.
        private void Siphon(EntityId attackerShip, ShipEntity target, int damageType, float damage)
        {
            var ship = _world.FindShip(attackerShip);
            if (ship == null || !Setup.IsLocalOwner(ship.Owner)) return;
            var resistance = DamageSystem.ArmorResistance(Setup.DamageTypes, target.Definition, damageType,
                target.ResistanceShare);
            ship.AddPendingRepair(damage * (1f - resistance));
        }

        // Legacy RestoreLifeAction (phase 5, step 5d) on the attacker's owner: the expiry moves back by the restored
        // ticks, at most to a full lifetime from this tick (Lifetime.Restore stops the elapsed time at 0). A fraction of a
        // tick waits for the next contact. A copy follows the owner's removal record, so no record travels.
        private void RestoreLifetime(ref BulletEntity bullet, double ticks)
        {
            var total = bullet.RestoreCarry + ticks;
            var whole = Math.Floor(total);
            var cap = Tick + bullet.LifetimeCapTicks - 1;
            if (whole >= cap - bullet.ExpiryTick)
            {
                bullet.ExpiryTick = Math.Max(bullet.ExpiryTick, cap);
                bullet.RestoreCarry = 0f;
                return;
            }
            bullet.ExpiryTick += (long)whole;
            bullet.RestoreCarry = (float)(total - whole);
        }

        // Legacy PushAction: the impulse acts at the target's origin, so it never turns the target.
        public void ApplyPush(ShipEntity target, Vec2 impulse) =>
            Physics.ApplyImpulse(target.Body, impulse, Physics.GetPosition(target.Body));

        // Missed over-time ticks compound; adding their powers would stop a target too soon. Engine inputs between
        // those ticks are not replayed, as with the push-on-arrival rule. One tick uses the same float on both peers.
        internal static void ApplySlowDown(PhysicsWorld physics, ShipEntity target, float power, long ticks = 1)
        {
            var factor = 1f - Math.Min(1f, power / (float)Math.Sqrt(target.Definition.Mass));
            if (ticks != 1) factor = (float)Math.Pow(factor, ticks);
            physics.SetLinearVelocity(target.Body, physics.GetLinearVelocity(target.Body) * factor);
            physics.SetAngularVelocity(target.Body, physics.GetAngularVelocity(target.Body) * factor);
        }

        internal static void ApplyRoll(PhysicsWorld physics, ShipEntity target, float degrees)
        {
            var spin = physics.GetAngularVelocity(target.Body);
            var change = degrees * (float)(Math.PI / 180.0);
            physics.SetAngularVelocity(target.Body, spin + (spin < 0f ? -change : change));
        }

        // Legacy PushAction (Push x the bullet velocity) plus RadialPushAction (phase 5, step 3): away from the bullet
        // centre toward the target's origin. Legacy applies the radial impulse at the bullet centre; that point lies on
        // the same line through the origin, so the torque is the same.
        public Vec2 PushImpulse(in BulletEntity bullet, Vec2 target, float falloff)
        {
            var definition = bullet.Definition;
            var impulse = Vec2.Zero;
            if (definition.Push != 0f) impulse = definition.Push * Physics.GetBulletVelocity(bullet.Body);
            var radial = definition.RadialPush + definition.RadialPushWithFalloff * falloff;
            if (radial != 0f)
                impulse += radial * Physics.Torus.Delta(Physics.GetBulletPosition(bullet.Body), target).Normalized;
            return impulse;
        }

        // A removed segment leaves the ramming tracker and the bullet contact ledger.
        public void Forget(EntityId id)
        {
            _ramming.Forget(id);
            _ledger.Forget(id);
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly WormTails _tails;
        private readonly BulletTriggers _triggers;
        // Null without remote owners (decision 3b).
        private readonly BattleNetwork _network;
        // Shared with the network part, which records the contacts of received hits.
        private readonly BulletContactLedger _ledger;
        private readonly ContactResolver _resolver = new ContactResolver();
        private readonly RammingTracker _ramming = new RammingTracker();
        private readonly BeamQueries _beams = new BeamQueries();
        private readonly BeamBolts _bolts = new BeamBolts();
        private readonly PointDefenseQueries _pointDefense = new PointDefenseQueries();
        private HashSet<EntityPair> _previousRoll = new HashSet<EntityPair>();
        private HashSet<EntityPair> _currentRoll = new HashSet<EntityPair>();
    }
}

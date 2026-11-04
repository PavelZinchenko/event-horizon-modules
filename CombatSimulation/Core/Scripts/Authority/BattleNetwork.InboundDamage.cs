using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    internal sealed partial class BattleNetwork
    {
        // Stage 10. Read all available boundaries before applying intervals, so a start and end received
        // together catch up only the owner's actual firing time. No attacker object is needed here.
        internal void AccumulateRemoteDamage()
        {
            var count = 0;
            while (count < _remoteHits.Count && _remoteHits[count].Hit.Tick <= Tick)
            {
                var record = _remoteHits[count++];
                var hit = record.Hit;
                _battle.Observer?.HitProcessed(new HitObservation(hit, Tick, LocalHitTarget(hit) != null));
                RecordAppliedHit(record.SenderOwner, hit.Sequence);
                if (hit.TargetIsBullet)
                {
                    ApplyBulletContact(hit);
                    continue;
                }
                if (hit.ImpactKind != HitImpactKind.DamageOverTime)
                {
                    if (hit.ContactState != HitContactState.End) AddRemoteDamage(hit, hit.Amount);
                    // The owner applies the push when the record arrives, late by the link delay (phase 4 E4).
                    if (hit.Impulse.X != 0f || hit.Impulse.Y != 0f)
                    {
                        var target = LocalHitTarget(hit);
                        if (target != null) _battle.ApplyPush(target, hit.Impulse);
                    }
                    continue;
                }

                if (hit.ContactState == HitContactState.Start)
                {
                    _remoteIntervals.Add(new RemoteInterval(hit, hit.Tick + Setup.OwnerDelay(hit.AttackerOwner)));
                }
                else if (hit.ContactState == HitContactState.End)
                {
                    // A contact can stop and restart several times during a blackout.
                    for (var i = _remoteIntervals.Count - 1; i >= 0; --i)
                    {
                        var interval = _remoteIntervals[i];
                        if (interval.EndTick != long.MaxValue || !interval.Hit.SameContact(hit) || interval.Hit.Tick > hit.Tick)
                            continue;
                        interval.EndTick = hit.Tick + Setup.OwnerDelay(hit.AttackerOwner);
                        _remoteIntervals[i] = interval;
                        break;
                    }
                }
                // Stay records do not open or extend an interval. Its start already supplies the rate.
            }
            if (count > 0) _remoteHits.RemoveRange(0, count);

            for (var i = 0; i < _remoteIntervals.Count;)
            {
                var interval = _remoteIntervals[i];
                if (LocalHitTarget(interval.Hit) == null)
                {
                    _remoteIntervals.RemoveAt(i);
                    continue;
                }
                var through = Math.Min(Tick + 1, interval.EndTick);
                if (through > interval.NextTick)
                {
                    var amount = IntervalDamage(interval.Hit, interval.NextTick - interval.StartTick,
                        through - interval.StartTick);
                    if (interval.Hit.Effect == HitEffect.SlowDown)
                        ContactSystem.ApplySlowDown(Physics, LocalHitTarget(interval.Hit),
                            interval.Hit.Amount * Battle.TickDuration, through - interval.NextTick);
                    else
                    {
                        AddRemoteDamage(interval.Hit, (float)Math.Min(float.MaxValue, amount));
                        _battle.Observer?.BeamDamage(new BeamDamageObservation(interval.Hit, Tick, interval.NextTick,
                            through, (float)Math.Min(float.MaxValue, amount)));
                    }
                    interval.NextTick = through;
                }
                if (interval.EndTick <= Tick + 1) _remoteIntervals.RemoveAt(i);
                else { _remoteIntervals[i] = interval; ++i; }
            }
        }

        // The damage of an interval's ticks [from, to), counted from its first tick (0): the rate per tick, or the
        // progressive ramp (phase 5, step 5c), which grows from the first tick.
        internal static double IntervalDamage(in ResolvedHit hit, long from, long to) =>
            hit.Effect == HitEffect.ProgressiveDamage
                ? ImpactEffects.RampDamage(hit.Amount, hit.RampTime, from, to)
                : (double)(to - from) * hit.Amount * Battle.TickDuration;

        private ShipEntity LocalHitTarget(in ResolvedHit hit)
        {
            var ship = _world.FindShip(hit.TargetId);
            return ship != null && ship.Owner == hit.TargetOwner && Setup.IsLocalOwner(ship.Owner) &&
                hit.TargetShape < ship.ShapeCount ? ship : null;
        }

        // A drain (phase 5, step 5a) joins the target's pending effects; only damage makes a hit event.
        private void AddRemoteDamage(in ResolvedHit hit, float amount)
        {
            var ship = LocalHitTarget(hit);
            if (ship == null || !(amount > 0f)) return;
            switch (hit.Effect)
            {
                case HitEffect.SlowDown:
                    ContactSystem.ApplySlowDown(Physics, ship, amount);
                    break;
                case HitEffect.Roll:
                    ContactSystem.ApplyRoll(Physics, ship, amount);
                    break;
                case HitEffect.EnergyDrain:
                    ship.AddPendingDrain(amount, 0f);
                    break;
                case HitEffect.ShieldDrain:
                    ship.AddPendingDrain(0f, amount);
                    break;
                default:
                    ship.AddPendingDamage(hit.DamageType, amount);
                    _world.Events.Add(BattleEvent.RemoteHit(BattleId, Tick, _world.Events.Count, hit, amount));
                    break;
            }
        }

        // Network plan 9d (owner, 2026-10-26): a contact between bullets, or between point defense and a bullet, counts if
        // either owner's device sees it. The device that saw it sends the contact on a remote bullet to that bullet's owner.
        internal void ClaimBulletContact(EntityId attacker, int attackerOwner, int deviceIndex, in BulletEntity target,
            HitImpactKind kind, float amount)
        {
            if (!Setup.HasRemoteOwners || !Setup.IsLocalOwner(attackerOwner) || Setup.IsLocalOwner(target.Owner)) return;
            _outgoingHits.AddBulletContact(new ResolvedHit(BattleId, Tick, attacker, attackerOwner, deviceIndex, target.Id,
                target.Owner, 0, HitContactState.Start, kind, amount, targetIsBullet: true));
        }

        // The owner applies a received contact to its bullet if the bullet is still alive. A projectile or ray deals its
        // damage once: this device's own contact with the same attacker (and tick, for a ray) already dealt it.
        private void ApplyBulletContact(in ResolvedHit hit)
        {
            if (!_world.Entities.TryGet(hit.TargetId, out var handle)) return;
            if (handle.Kind == EntityKind.Segment)
            {
                _tails.ApplySegmentContact(hit, _world.Segments.Get(handle));
                return;
            }
            if (!_world.Bullets.TryGet(handle, out var index)) return;
            ref readonly var bullet = ref _world.Bullets[index];
            if (bullet.Owner != hit.TargetOwner || !Setup.IsLocalOwner(bullet.Owner)) return;
            // A contact that stops the bullet counts once for its Hit triggers, even if this device saw it too.
            var stops = bullet.PendingRemoval == RemovalReason.None;
            switch (hit.ImpactKind)
            {
                case HitImpactKind.Disarm:
                    if (!bullet.Definition.CanBeDisarmed)
                    {
                        // The zap still counts as a contact; the bullet flies on.
                        if (_contacts.Record(hit.AttackerId, hit.TargetId, hit.Tick)) _triggers.AddPendingHit(index);
                        _world.Events.Add(BattleEvent.RemoteDisarm(BattleId, Tick, _world.Events.Count, hit));
                        break;
                    }
                    if (stops) _triggers.AddPendingHit(index);
                    _world.Bullets.MarkRemoval(handle, RemovalReason.Disposal);
                    _world.Events.Add(BattleEvent.RemoteDisarm(BattleId, Tick, _world.Events.Count, hit));
                    break;
                case HitImpactKind.Stop:
                    if (stops) _triggers.AddPendingHit(index);
                    _world.Bullets.MarkRemoval(handle, RemovalReason.Death);
                    break;
                default:
                    if (!_contacts.Record(hit.AttackerId, hit.TargetId,
                        hit.ImpactKind == HitImpactKind.DamageOverTime ? hit.Tick : -1)) break;
                    _world.Bullets.AddDamage(handle, hit.Amount);
                    _triggers.AddPendingHit(index);
                    _world.Events.Add(BattleEvent.RemoteHit(BattleId, Tick, _world.Events.Count, hit, hit.Amount));
                    break;
            }
        }

        // A local attacker's contact on a remote bullet or segment goes to the target's owner.
        internal void SendBulletContact(in ResolvedHit hit) => _outgoingHits.AddBulletContact(hit);

        // The last hit sequence of the sender that this stage has processed; corrections acknowledge it (step 5).
        // Records refused at Submit leave gaps; the next processed record covers them.
        internal ulong AppliedHitSequence(int sender) =>
            _appliedHitSequences.TryGetValue(sender, out var sequence) ? sequence : 0;

        private void RecordAppliedHit(int sender, ulong sequence)
        {
            if (sequence > AppliedHitSequence(sender)) _appliedHitSequences[sender] = sequence;
        }

        private struct RemoteInterval
        {
            public readonly ResolvedHit Hit;
            // The local tick of the owner's first contact tick.
            public readonly long StartTick;
            public long NextTick;
            public long EndTick;

            public RemoteInterval(ResolvedHit hit, long nextTick)
            {
                Hit = hit;
                StartTick = nextTick;
                NextTick = nextTick;
                EndTick = long.MaxValue;
            }
        }

        private readonly List<RemoteInterval> _remoteIntervals = new List<RemoteInterval>();
        private readonly Dictionary<int, ulong> _appliedHitSequences = new Dictionary<int, ulong>();
    }
}

using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    public sealed partial class Battle
    {
        // Stage 10. Read all available boundaries before applying intervals, so a start and end received
        // together catch up only the owner's actual firing time. No attacker object is needed here.
        private void AccumulateRemoteDamage()
        {
            var count = 0;
            while (count < _remoteHits.Count && _remoteHits[count].Hit.Tick <= Tick)
            {
                var record = _remoteHits[count++];
                var hit = record.Hit;
                Observer?.HitProcessed(new HitObservation(hit, Tick, LocalHitTarget(hit) != null));
                RecordAppliedHit(record.SenderOwner, hit.Sequence);
                if (hit.TargetIsBullet)
                {
                    ApplyBulletContact(hit);
                    continue;
                }
                if (hit.ImpactKind != HitImpactKind.DamageOverTime)
                {
                    if (hit.ContactState != HitContactState.End) AddRemoteDamage(hit, hit.Amount);
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
                    var amount = (double)(through - interval.NextTick) * interval.Hit.Amount * TickDuration;
                    AddRemoteDamage(interval.Hit, (float)Math.Min(float.MaxValue, amount));
                    Observer?.BeamDamage(new BeamDamageObservation(interval.Hit, Tick, interval.NextTick,
                        through, (float)Math.Min(float.MaxValue, amount)));
                    interval.NextTick = through;
                }
                if (interval.EndTick <= Tick + 1) _remoteIntervals.RemoveAt(i);
                else { _remoteIntervals[i] = interval; ++i; }
            }
        }

        private ShipEntity LocalHitTarget(in ResolvedHit hit)
        {
            var ship = FindShip(hit.TargetId);
            return ship != null && ship.Owner == hit.TargetOwner && Setup.IsLocalOwner(ship.Owner) &&
                hit.TargetShape < ship.ShapeCount ? ship : null;
        }

        private void AddRemoteDamage(in ResolvedHit hit, float amount)
        {
            var ship = LocalHitTarget(hit);
            if (ship == null || !(amount > 0f)) return;
            ship.AddPendingDamage(hit.DamageType, amount);
            _events.Add(BattleEvent.RemoteHit(BattleId, Tick, _events.Count, hit, amount));
        }

        // Network plan 9d (owner, 2026-10-26): a contact between bullets, or between point defense and a bullet, counts if
        // either owner's device sees it. The device that saw it sends the contact on a remote bullet to that bullet's owner.
        private void ClaimBulletContact(EntityId attacker, int attackerOwner, int deviceIndex, in BulletEntity target,
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
            if (!_entities.TryGet(hit.TargetId, out var handle) || !_bullets.TryGet(handle, out var index)) return;
            ref readonly var bullet = ref _bullets[index];
            if (bullet.Owner != hit.TargetOwner || !Setup.IsLocalOwner(bullet.Owner)) return;
            switch (hit.ImpactKind)
            {
                case HitImpactKind.Disarm:
                    _bullets.MarkRemoval(handle, RemovalReason.Disposal);
                    _events.Add(BattleEvent.RemoteDisarm(BattleId, Tick, _events.Count, hit));
                    break;
                case HitImpactKind.Stop:
                    _bullets.MarkRemoval(handle, RemovalReason.Death);
                    break;
                default:
                    if (!RecordBulletContact(hit.AttackerId, hit.TargetId,
                        hit.ImpactKind == HitImpactKind.DamageOverTime ? hit.Tick : -1)) break;
                    _bullets.AddDamage(handle, hit.Amount);
                    _events.Add(BattleEvent.RemoteHit(BattleId, Tick, _events.Count, hit, hit.Amount));
                    break;
            }
        }

        // False when the contact of this attacker on this local bullet (at this tick, for a ray; tick -1 otherwise) already
        // dealt its damage. Entries leave with their bullet.
        private bool RecordBulletContact(EntityId attacker, EntityId target, long tick)
        {
            foreach (var contact in _bulletContacts)
                if (contact.Attacker == attacker && contact.Target == target && contact.Tick == tick) return false;
            _bulletContacts.Add(new BulletContact(attacker, target, tick));
            return true;
        }

        private void ForgetBulletContacts(EntityId target)
        {
            for (var i = _bulletContacts.Count - 1; i >= 0; --i)
                if (_bulletContacts[i].Target == target) _bulletContacts.RemoveAt(i);
        }

        private readonly struct BulletContact
        {
            public readonly EntityId Attacker, Target;
            public readonly long Tick;
            public BulletContact(EntityId attacker, EntityId target, long tick) { Attacker = attacker; Target = target; Tick = tick; }
        }

        private readonly List<BulletContact> _bulletContacts = new List<BulletContact>();

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
            public long NextTick;
            public long EndTick;

            public RemoteInterval(ResolvedHit hit, long nextTick)
            {
                Hit = hit;
                NextTick = nextTick;
                EndTick = long.MaxValue;
            }
        }

        private readonly List<RemoteInterval> _remoteIntervals = new List<RemoteInterval>();
        private readonly Dictionary<int, ulong> _appliedHitSequences = new Dictionary<int, ulong>();
    }
}

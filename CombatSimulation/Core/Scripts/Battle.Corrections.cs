using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Owner corrections and predicted damage (network plan, step 5). The owner writes the end-of-tick state of its
    // ships and drones; the receiver adds the movement error to the present and sets the gameplay state exactly.
    // Damage this device sends to a remote ship is applied to the copy at once and replayed after each correction
    // until the target owner acknowledges it.
    public sealed partial class Battle
    {
        // Call after Step: the batch describes the last completed tick. AppliedHitSequence acknowledges the hits
        // of peerOwner that this device has applied by then.
        public void WriteCorrections(int owner, int peerOwner, CorrectionBatch batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (!Setup.IsLocalOwner(owner)) throw new ArgumentException("The owner is not local.", nameof(owner));
            if (!IsRemoteOwner(peerOwner)) throw new ArgumentException("The peer is not a remote owner.", nameof(peerOwner));
            if (Tick == 0) throw new InvalidOperationException("The battle has no completed tick.");

            batch.Begin(BattleId, owner, Tick - 1, AppliedHitSequence(peerOwner));
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship == null || ship.Owner != owner) continue;

                for (var d = 0; d < ship.DeviceCount; ++d) batch.AddDevice(ship.GetDevice(d).Export());
                for (var b = 0; b < ship.Definition.Barrels.Count; ++b) batch.AddBarrel(ship.GetBarrelShotTick(b));
                batch.CommitBody(ship.Id, ReadSpawnPose(ship), ship.Armor.Value,
                    new RechargeState(ship.Shield.Value, ship.Shield.DelayRemaining),
                    new RechargeState(ship.Energy.Value, ship.Energy.DelayRemaining), ship.RetreatCompletionTick);
            }
        }

        // Holds each valid correction on its remote entity until stage 1 after its tick. A correction that is not
        // newer than the held or last applied one for its entity is dropped. Returns the number held.
        public int Submit(int senderOwner, CorrectionBatch batch)
        {
            RequireRunning();
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (batch.BattleId != BattleId || batch.Owner != senderOwner || !IsRemoteOwner(senderOwner) ||
                batch.Tick < 0 || batch.Tick == long.MaxValue) return 0;

            var held = 0;
            foreach (var body in batch.Bodies)
            {
                var ship = FindShip(body.EntityId);
                if (ship == null || ship.Owner != senderOwner || !ValidCorrection(ship, body)) continue;
                if (batch.Tick <= ship.LastCorrectionTick || (ship.Correction.IsSet && batch.Tick <= ship.Correction.Tick))
                    continue;
                ship.Correction.Store(batch.Tick, batch.AppliedHitSequence, body, batch.GetDevices(body),
                    batch.GetBarrelShotTicks(body));
                ++held;
            }
            return held;
        }

        private static bool ValidCorrection(ShipEntity ship, in BodyCorrection body) =>
            body.DeviceCount == ship.DeviceCount && body.BarrelCount == ship.Definition.Barrels.Count &&
            ValidPose(body.Motion) && Finite(body.Armor) && body.Armor >= 0f && body.Armor <= ship.Armor.Max &&
            ValidResource(body.Shield, ship.Shield) && ValidResource(body.Energy, ship.Energy) &&
            (body.RetreatCompletionTick == ShipEntity.NoRetreat || body.RetreatCompletionTick >= 0);

        private static bool ValidResource(in RechargeState state, in ResourcePool pool) =>
            Finite(state.Value) && state.Value >= 0f && state.Value <= pool.Max &&
            Finite(state.DelayRemaining) && state.DelayRemaining >= 0f && state.DelayRemaining <= pool.RechargeDelay;

        // Stage 1, after received operations and before commands.
        private void ApplyCorrections()
        {
            if (!Setup.HasRemoteOwners) return;
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship == null || !ship.Correction.IsSet || ship.Correction.Tick >= Tick) continue;
                ApplyCorrection(ship, ship.Correction);
                ship.Correction.IsSet = false;
            }
        }

        private void ApplyCorrection(ShipEntity ship, PendingCorrection correction)
        {
            var tick = correction.Tick;
            ref readonly var body = ref correction.Body;

            // Movement: the error at the owner's tick, added to the present. Outside the history, the next
            // correction inside it repairs the movement.
            if (ship.TryGetBodySample(tick, out var sample))
            {
                var position = World.Torus.Delta(sample.Position, body.Motion.Position);
                var angle = WrapAngle(body.Motion.Angle - sample.Angle);
                var linearVelocity = body.Motion.LinearVelocity - sample.LinearVelocity;
                var angularVelocity = body.Motion.AngularVelocity - sample.AngularVelocity;
                World.SetPose(ship.Body, World.GetPosition(ship.Body) + position, World.GetAngle(ship.Body) + angle);
                World.SetLinearVelocity(ship.Body, World.GetLinearVelocity(ship.Body) + linearVelocity);
                World.SetAngularVelocity(ship.Body, World.GetAngularVelocity(ship.Body) + angularVelocity);
                ship.ShiftHistory(tick, World.Torus, position, angle, linearVelocity, angularVelocity);
            }

            // Gameplay: set exactly at the owner's tick, then recharge through the last completed tick.
            ship.Armor.Value = body.Armor;
            ship.Shield.Value = body.Shield.Value;
            ship.Shield.DelayRemaining = body.Shield.DelayRemaining;
            ship.Energy.Value = body.Energy.Value;
            ship.Energy.DelayRemaining = body.Energy.DelayRemaining;
            for (var t = tick + 1; t < Tick && !(IsSettled(ship.Shield) && IsSettled(ship.Energy)); ++t)
            {
                ship.Shield.Update(TickDuration);
                ship.Energy.Update(TickDuration);
            }

            ship.RetreatCompletionTick = body.RetreatCompletionTick;
            for (var b = 0; b < body.BarrelCount; ++b) ship.RecordBarrelShot(b, correction.BarrelShotTicks[b]);
            for (var d = 0; d < body.DeviceCount; ++d) ship.GetDevice(d).Import(correction.Devices[d]);

            // Damage this device sent that the owner had not applied by its tick.
            var damage = ReplayPredictedDamage(ship.Id, tick, correction.AppliedHitSequence);
            if (damage > 0f) ApplyShieldFirst(ship, damage);
            ship.LastCorrectionTick = tick;
        }

        // A full resource without a delay no longer changes.
        private static bool IsSettled(in ResourcePool pool) => pool.DelayRemaining <= 0f && pool.Value >= pool.Max;

        private static float WrapAngle(float angle)
        {
            const float twoPi = (float)(2.0 * Math.PI);
            angle %= twoPi;
            if (angle > Math.PI) angle -= twoPi;
            else if (angle < -Math.PI) angle += twoPi;
            return angle;
        }

        // Legacy Impact.ApplyShield and ShipStats.ApplyDamage: shield first, then armor.
        private static void ApplyShieldFirst(ShipEntity ship, float damage)
        {
            var absorbed = ship.Shield.Max > 0f && ship.Shield.Value > 0f ? Math.Min(damage, ship.Shield.Value) : 0f;
            if (absorbed > 0f) ship.Shield.Consume(absorbed);
            ship.Armor.Consume(damage - absorbed);
        }

        // Stage 14, before the snapshot: the end-of-tick state a correction for this tick describes.
        private void RecordBodySamples()
        {
            if (!Setup.HasRemoteOwners) return;
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship != null && !Setup.IsLocalOwner(ship.Owner)) ship.RecordBodySample(Tick, ReadSpawnPose(ship));
            }
        }

        // Stage 14: this tick's outgoing records join the ledger. A beam end closes its open interval.
        private void RecordPredictedHits()
        {
            var records = _outgoingHits.Records;
            for (var i = 0; i < records.Count; ++i)
            {
                var hit = records[i];
                if (hit.ImpactKind != HitImpactKind.DamageOverTime)
                {
                    if (hit.ContactState != HitContactState.End) _predictedHits.Add(new PredictedHit(hit));
                    continue;
                }

                if (hit.ContactState == HitContactState.Start)
                {
                    _predictedHits.Add(new PredictedHit(hit));
                    continue;
                }
                if (hit.ContactState != HitContactState.End) continue;

                for (var j = _predictedHits.Count - 1; j >= 0; --j)
                {
                    var entry = _predictedHits[j];
                    if (entry.EndTick != long.MaxValue || entry.Hit.ImpactKind != HitImpactKind.DamageOverTime ||
                        !entry.Hit.SameContact(hit)) continue;
                    entry.EndTick = hit.Tick;
                    entry.EndSequence = hit.Sequence;
                    _predictedHits[j] = entry;
                    break;
                }
            }
        }

        // The damage to replay on the target after a correction for the owner's tick with acknowledgement ack.
        // A discrete hit replays until acknowledged. A beam replays the ticks shown here, [start, end), minus
        // the ticks the owner had applied by its tick, [start + D, end + D), where D is the shooter's delay.
        private float ReplayPredictedDamage(EntityId target, long tick, ulong ack)
        {
            var total = 0.0;
            var kept = 0;
            for (var i = 0; i < _predictedHits.Count; ++i)
            {
                var entry = _predictedHits[i];
                var keep = true;
                if (entry.Hit.TargetId == target)
                {
                    if (entry.Hit.ImpactKind != HitImpactKind.DamageOverTime)
                    {
                        if (entry.Hit.Sequence > ack) total += entry.Hit.Amount;
                        else keep = false;
                    }
                    else
                    {
                        var start = entry.Hit.Tick;
                        var delay = Setup.OwnerDelay(entry.Hit.AttackerOwner);
                        var endKnown = entry.EndSequence <= ack;
                        var shown = Math.Max(0L, Math.Min(entry.EndTick, Tick) - start);
                        var applied = 0L;
                        if (entry.Hit.Sequence <= ack)
                        {
                            var ownerEnd = endKnown ? entry.EndTick + delay : long.MaxValue;
                            applied = Math.Max(0L, Math.Min(tick + 1, ownerEnd) - (start + delay));
                        }
                        if (shown > applied) total += (double)(shown - applied) * entry.Hit.Amount * TickDuration;
                        keep = !(endKnown && tick + 1 >= entry.EndTick + delay);
                    }
                }
                if (keep) _predictedHits[kept++] = entry;
            }
            _predictedHits.RemoveRange(kept, _predictedHits.Count - kept);
            return (float)Math.Min(float.MaxValue, total);
        }

        // A removed target needs no replay.
        private void ForgetPredictedHits(EntityId target)
        {
            var kept = 0;
            for (var i = 0; i < _predictedHits.Count; ++i)
                if (_predictedHits[i].Hit.TargetId != target) _predictedHits[kept++] = _predictedHits[i];
            _predictedHits.RemoveRange(kept, _predictedHits.Count - kept);
        }

        internal int PredictedHitCount => _predictedHits.Count;

        private struct PredictedHit
        {
            public readonly ResolvedHit Hit;
            // Beams only: the exclusive end tick and its record sequence, unknown until the end record.
            public long EndTick;
            public ulong EndSequence;

            public PredictedHit(ResolvedHit hit)
            {
                Hit = hit;
                EndTick = long.MaxValue;
                EndSequence = ulong.MaxValue;
            }
        }

        private readonly List<PredictedHit> _predictedHits = new List<PredictedHit>();
    }
}

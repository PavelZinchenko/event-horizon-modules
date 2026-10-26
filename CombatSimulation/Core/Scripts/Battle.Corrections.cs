using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Owner corrections and predicted damage (network plan, step 5 and G1). The owner writes the end-of-tick state of
    // its ships and drones; the receiver adds the movement error to the present, and moves resources and
    // point-defense timers to the owner's. Damage this device sends to a remote ship is applied to the copy at once
    // and replayed after each correction until the target owner acknowledges it.
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

                for (var d = 0; d < ship.DeviceCount; ++d)
                    if (ship.GetDevice(d) is PointDefenseDevice pointDefense) batch.AddTimers(pointDefense.Export(Tick - 1));
                batch.CommitBody(ship.Id, ReadSpawnPose(ship), ResourceLevel.Of(ship.Armor, true),
                    ResourceLevel.Of(ship.Shield, false), ResourceLevel.Of(ship.Energy, false));
            }
            WriteMissileCorrections(owner, peerOwner, batch);
        }

        // P3: every live homing projectile of the owner, highest priority first. A missile never corrected comes first
        // (the early correction after spawn), older first. Otherwise the priority is the ticks since its last correction
        // times its weight: NearWeight within one second of flight of a ship of the peer, or inside the peer's
        // point-defense range; 1 elsewhere. Equal weights give round-robin.
        private void WriteMissileCorrections(int owner, int peerOwner, CorrectionBatch batch)
        {
            var tick = Tick - 1;
            for (var i = 0; i < _bullets.Capacity; ++i)
            {
                if (!_bullets.IsAlive(i)) continue;
                ref readonly var bullet = ref _bullets[i];
                if (bullet.Owner != owner || bullet.IsBeam || !bullet.Definition.IsHoming ||
                    bullet.PendingRemoval != RemovalReason.None) continue;
                var position = World.GetBulletPosition(bullet.Body);
                var rotation = World.GetBulletRotation(bullet.Body);
                var velocity = World.GetBulletVelocity(bullet.Body);
                var fullSpeed = (velocity - bullet.Definition.Speed * new Vec2(rotation.C, rotation.S)).Length <= FullSpeedTolerance;
                var priority = !bullet.Corrected ? EarlyPriority + (tick - bullet.SpawnTick)
                    : (tick - bullet.CorrectedTick) * (IsNearPeer(peerOwner, position, bullet.Definition) ? NearWeight : 1f);
                batch.AddMissile(new MissileCorrection(bullet.Id, position, rotation.Angle, !fullSpeed, velocity, priority));
            }
            batch.SortMissiles();
        }

        private bool IsNearPeer(int peerOwner, Vec2 position, BulletDefinition definition)
        {
            var reach = definition.Speed * NearSeconds;
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship == null || ship.Owner != peerOwner) continue;
                var distance = World.Torus.Delta(position, World.GetPosition(ship.Body)).Length - ship.Definition.HullRadius;
                if (distance <= reach) return true;
                for (var d = 0; d < ship.DeviceCount; ++d)
                    if (ship.GetDevice(d) is PointDefenseDevice pointDefense &&
                        distance <= pointDefense.Definition.Radius + definition.Radius) return true;
            }
            return false;
        }

        // Runtime calls this for each missile it sent from a batch for tick.
        internal void MissileCorrectionSent(EntityId id, long tick)
        {
            if (!_entities.TryGet(id, out var handle) || !_bullets.TryGet(handle, out var index)) return;
            ref var bullet = ref _bullets[index];
            bullet.Corrected = true;
            bullet.CorrectedTick = tick;
        }

        // Placeholders until the owner sets them from a playable scene (network plan, P3).
        internal const float NearWeight = 4f, NearSeconds = 1f;
        // An early correction outranks every periodic one that waited under 250,000 ticks at NearWeight (83 minutes).
        // Spawn ages up to 2^24 ticks stay exact in a float above it.
        private const float EarlyPriority = 1e6f;
        // Velocity within this of Speed along the heading is not sent: about 0.3 mm of error per tick.
        internal const float FullSpeedTolerance = 1f / 64f;

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
                if (ship == null || ship.Owner != senderOwner || !ValidCorrection(ship, batch, body)) continue;
                if (batch.Tick <= ship.LastCorrectionTick || (ship.Correction.IsSet && batch.Tick <= ship.Correction.Tick))
                    continue;
                ship.Correction.Store(batch.Tick, batch.AppliedHitSequence, body, batch.GetTimers(body));
                ++held;
            }
            foreach (var missile in batch.Missiles)
            {
                if (!_entities.TryGet(missile.EntityId, out var handle) || !_bullets.TryGet(handle, out var index)) continue;
                ref var bullet = ref _bullets[index];
                if (bullet.Owner != senderOwner || bullet.IsBeam || !bullet.Definition.IsHoming || !ValidMissile(missile) ||
                    batch.Tick < bullet.SpawnTick || (bullet.Corrected && batch.Tick <= bullet.CorrectedTick) ||
                    (bullet.HasCorrection && batch.Tick <= bullet.CorrectionTick)) continue;
                bullet.HasCorrection = true;
                bullet.CorrectionTick = batch.Tick;
                bullet.Correction = missile;
                ++held;
            }
            return held;
        }

        private static bool ValidMissile(in MissileCorrection missile) => Finite(missile.Position.X) &&
            Finite(missile.Position.Y) && Finite(missile.Angle) && Finite(missile.Velocity.X) && Finite(missile.Velocity.Y);

        private static bool ValidCorrection(ShipEntity ship, CorrectionBatch batch, in BodyCorrection body)
        {
            if (body.TimerCount != ship.Definition.PointDefenseCount || !ValidPose(body.Motion) ||
                !body.Armor.IsValidFor(ship.Armor) || !body.Shield.IsValidFor(ship.Shield) ||
                !body.Energy.IsValidFor(ship.Energy)) return false;
            var timers = batch.GetTimers(body);
            var next = 0;
            for (var d = 0; d < ship.DeviceCount; ++d)
                if (ship.GetDevice(d) is PointDefenseDevice pointDefense && !pointDefense.IsValid(timers[next++])) return false;
            return true;
        }

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
            for (var i = 0; i < _bullets.Capacity; ++i)
            {
                if (!_bullets.IsAlive(i)) continue;
                ref var bullet = ref _bullets[i];
                if (!bullet.HasCorrection || bullet.CorrectionTick >= Tick) continue;
                ApplyMissileCorrection(ref bullet);
                bullet.HasCorrection = false;
            }
        }

        // P3, by replacement: the owner's state at its tick, then the homing steering re-run to the last completed tick
        // toward the target's present position. No history, so nothing to overshoot. No event: only the motion changes.
        private void ApplyMissileCorrection(ref BulletEntity bullet)
        {
            var correction = bullet.Correction;
            var before = World.GetBulletPosition(bullet.Body);
            var angleBefore = World.GetBulletRotation(bullet.Body).Angle;
            var position = World.Torus.Wrap(correction.Position);
            var angle = correction.Angle;
            var velocity = correction.HasVelocity ? correction.Velocity
                : bullet.Definition.Speed * new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            HomingSteering.Advance(this, bullet, (int)Math.Min(int.MaxValue, Tick - 1 - bullet.CorrectionTick),
                ref position, ref angle, ref velocity);
            World.SetBulletPose(bullet.Body, position, angle);
            World.SetBulletVelocity(bullet.Body, velocity);
            bullet.Corrected = true;
            bullet.CorrectedTick = bullet.CorrectionTick;
            Observer?.Correction(new CorrectionObservation(bullet, bullet.CorrectionTick, Tick,
                World.Torus.Delta(before, position), WrapAngle(angle - angleBefore)));
        }

        private void ApplyCorrection(ShipEntity ship, PendingCorrection correction)
        {
            var tick = correction.Tick;
            ref readonly var body = ref correction.Body;
            var armorBefore = ship.Armor.Value;
            var shieldBefore = ship.Shield.Value;
            var hasHistory = ship.TryGetBodySample(tick, out var sample);
            var positionError = hasHistory ? World.Torus.Delta(sample.Position, body.Motion.Position) : Vec2.Zero;
            var angleError = hasHistory ? WrapAngle(body.Motion.Angle - sample.Angle) : 0f;

            // Movement: the error at the owner's tick, added to the present. Outside the history, the next
            // correction inside it repairs the movement.
            if (hasHistory)
            {
                var position = positionError;
                var angle = angleError;
                var linearVelocity = body.Motion.LinearVelocity - sample.LinearVelocity;
                var angularVelocity = body.Motion.AngularVelocity - sample.AngularVelocity;
                World.SetPose(ship.Body, World.GetPosition(ship.Body) + position, World.GetAngle(ship.Body) + angle);
                World.SetLinearVelocity(ship.Body, World.GetLinearVelocity(ship.Body) + linearVelocity);
                World.SetAngularVelocity(ship.Body, World.GetAngularVelocity(ship.Body) + angularVelocity);
                ship.ShiftHistory(tick, World.Torus, position, angle, linearVelocity, angularVelocity);
            }

            // Gameplay (G1): each resource gets a candidate, the owner's level at its tick recharged through the last
            // completed tick, minus the damage this device sent that the owner had not applied by its tick. The
            // candidate replaces the value only when they differ by more than one level. The delay always follows.
            var elapsed = (Tick - 1 - tick) * TickDuration;
            var armor = Candidate(ship.Armor, body.Armor, elapsed);
            var shield = Candidate(ship.Shield, body.Shield, elapsed);
            var energy = Candidate(ship.Energy, body.Energy, elapsed);
            for (var t = tick + 1; t < Tick && !(IsSettled(armor) && IsSettled(shield) && IsSettled(energy)); ++t)
            {
                armor.Update(TickDuration);
                shield.Update(TickDuration);
                energy.Update(TickDuration);
            }
            var damage = ReplayPredictedDamage(ship.Id, tick, correction.AppliedHitSequence);
            if (damage > 0f) ApplyShieldFirst(ref shield, ref armor, damage);
            Settle(ref ship.Armor, armor);
            Settle(ref ship.Shield, shield);
            Settle(ref ship.Energy, energy);

            var next = 0;
            for (var d = 0; d < ship.DeviceCount; ++d)
                if (ship.GetDevice(d) is PointDefenseDevice pointDefense) pointDefense.Import(tick, correction.Timers[next++]);
            ship.LastCorrectionTick = tick;
            Observer?.Correction(new CorrectionObservation(ship, tick, Tick, hasHistory, positionError,
                angleError, armorBefore, shieldBefore));
        }

        // A full resource without a delay no longer changes; nor does one that does not recharge.
        private static bool IsSettled(in ResourcePool pool) =>
            pool.DelayRemaining <= 0f && (pool.Value >= pool.Max || pool.RechargeRate == 0f);

        // The owner's level at its tick. Delay flag 0 clears the delay. Flag 1 keeps a running local delay, moved
        // back to the owner's tick, or starts the full delay there; the recharge to the present then advances it.
        private static ResourcePool Candidate(in ResourcePool local, in ResourceLevel level, float elapsed)
        {
            var candidate = local;
            candidate.Value = level.Value(local.Max);
            candidate.DelayRemaining = !level.DelayRunning ? 0f : local.DelayRemaining > 0f
                ? Math.Min(local.RechargeDelay, local.DelayRemaining + elapsed) : local.RechargeDelay;
            return candidate;
        }

        private static void Settle(ref ResourcePool local, in ResourcePool candidate)
        {
            if (Math.Abs(candidate.Value - local.Value) > local.Max / ResourceLevel.Steps) local.Value = candidate.Value;
            local.DelayRemaining = candidate.DelayRemaining;
        }

        private static float WrapAngle(float angle)
        {
            const float twoPi = (float)(2.0 * Math.PI);
            angle %= twoPi;
            if (angle > Math.PI) angle -= twoPi;
            else if (angle < -Math.PI) angle += twoPi;
            return angle;
        }

        private static void ApplyShieldFirst(ShipEntity ship, float damage) => ApplyShieldFirst(ref ship.Shield, ref ship.Armor, damage);

        // Legacy Impact.ApplyShield and ShipStats.ApplyDamage: shield first, then armor.
        private static void ApplyShieldFirst(ref ResourcePool shield, ref ResourcePool armor, float damage)
        {
            var absorbed = shield.Max > 0f && shield.Value > 0f ? Math.Min(damage, shield.Value) : 0f;
            if (absorbed > 0f) shield.Consume(absorbed);
            armor.Consume(damage - absorbed);
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

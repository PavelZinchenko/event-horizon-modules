using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // The network part of a battle (phase 5, step 8b, H13): owner records in and out, fire confirmations, owner
    // corrections, predicted damage, received hits and bullet contacts. Battle keeps the public API and calls it in
    // stage order. It calls back into Battle for the changes that other systems react to (ship removal, bullet
    // release, roster transitions) and for devices, which take the battle until DeviceContext (decision 1).
    // Battle creates it only with remote owners (decision 3b); a local battle has no network part.
    internal sealed partial class BattleNetwork
    {
        public BattleNetwork(BattleWorld world, Battle battle, WormTails tails, BulletTriggers triggers,
            BulletContactLedger contacts)
        {
            _world = world;
            _battle = battle;
            _tails = tails;
            _triggers = triggers;
            _contacts = contacts;
            _replayDamage = new float[world.Setup.DamageTypes.Count];
        }

        private BattleSetup Setup => _world.Setup;
        private BattleId BattleId => _world.BattleId;
        private long Tick => _world.Tick;
        private PhysicsWorld Physics => _world.Physics;
        private Roster Roster => _world.Roster;

        // True while received records apply: their changes are commits and send no records.
        internal bool ApplyingRemoteOperation => _applyingRemoteOperation;

        internal OutgoingHitBuffer OutgoingHits => _outgoingHits;

        // Stage 13: the last tick that every remote owner's records have settled. Received operations may still wait
        // for a roster revision or their original stage.
        internal long ResultFrontier()
        {
            var frontier = Tick;
            for (var i = 0; i < Setup.Roster.Count; ++i)
            {
                var slot = Setup.Roster[i];
                if (Setup.IsLocalOwner(slot.Owner)) continue;
                if (!_watermarks.TryGetValue(slot.Owner, out var watermark)) { frontier = -1; break; }
                watermark.Promote(OperationsThrough(slot.Owner));
                frontier = Math.Min(frontier, watermark.Through);
            }
            foreach (var operation in _remoteOperations)
                frontier = Math.Min(frontier, operation.Tick - 1);
            return frontier;
        }

        internal void CaptureRetreat(ShipEntity ship)
        {
            if (ShouldCapture(ship.Owner))
                _outgoingOperations.Add(OwnerOperation.RetreatStarted(BattleId, ship.Owner, NextOperationSequence(ship.Owner), Tick, ship.Id));
        }

        internal void CaptureAllegiance(ShipEntity ship, Allegiance allegiance)
        {
            if (ShouldCapture(ship.Owner))
                _outgoingOperations.Add(OwnerOperation.AllegianceChanged(BattleId, ship.Owner,
                    NextOperationSequence(ship.Owner), Tick, ship.Id, allegiance));
        }

        // Stage 14, after the snapshot has taken this tick's records.
        internal void ClearTickRecords()
        {
            _outgoingHits.Records.Clear();
            _outgoingOperations.Clear();
            _appliedOperations.Clear();
        }

        private readonly BattleWorld _world;
        private readonly Battle _battle;
        private readonly WormTails _tails;
        private readonly BulletTriggers _triggers;
        private readonly BulletContactLedger _contacts;
        private readonly OutgoingHitBuffer _outgoingHits = new OutgoingHitBuffer();
    }
}

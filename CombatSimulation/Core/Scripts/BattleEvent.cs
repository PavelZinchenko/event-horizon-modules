using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public enum BattleEventKind : byte
    {
        Spawn = 1,
        Removal = 2,
        RosterTransition = 3,
        Result = 4,

        // For presentation: one accepted hit. The damage is applied at stage 10, shield first.
        Hit = 5,
    }

    public enum RemovalReason : byte
    {
        None,
        Death,
        Retreat,
        Expiry,
        Disposal,
    }

    // The same set as the application's roster change reasons (phase 3a).
    public enum RosterTransitionReason : byte
    {
        None,
        Deployed,
        Retreated,
        Killed,

        // Surrender or kill-all destroyed the slot; a ready reserve has no entity.
        ForcedDestruction,
    }

    // One committed event of a tick. A flat tagged form, so the batch never allocates.
    // Identity: battle ID, tick and sequence. An entity event comes before the roster transition of the same operation.
    public readonly struct BattleEvent
    {
        public readonly BattleId BattleId;
        public readonly long Tick;

        // Position in the tick's batch.
        public readonly int Sequence;

        public readonly BattleEventKind Kind;

        // Spawn, removal, and a roster transition that involves an entity. Hit: the target.
        public readonly EntityId EntityId;

        // Spawn and removal of a roster ship, and roster transitions. Side and owner also for a bullet.
        public readonly RosterId RosterId;
        public readonly int Side;
        public readonly int Owner;

        // Spawn and removal of a ship: the view factory builds the view from it. Null for a bullet.
        public readonly ShipDefinition Definition;

        public readonly RemovalReason RemovalReason;

        public readonly RosterStatus PreviousStatus;
        public readonly RosterStatus Status;
        public readonly RosterTransitionReason TransitionReason;
        public readonly int Revision;

        // Roster transition: the deficits the slot retains after it (B4).
        public readonly float ArmorDeficit;
        public readonly float ShieldDeficit;

        public readonly BattleResult Result;

        // Hit: the attacker, the damage before the shield, and the contact point (zero for a ship touch).
        public readonly EntityId AttackerId;
        public readonly float Damage;
        public readonly Vec2 Point;

        // Spawn and removal of a bullet: the definition and the entity that fired it. Null for a ship.
        public readonly BulletDefinition BulletDefinition;
        public readonly BeamDefinition BeamDefinition;
        public readonly EntityId ParentId;

        public bool IsBullet => BulletDefinition != null || IsBeam;
        public bool IsBeam => BeamDefinition != null;

        private BattleEvent(BattleId battleId, long tick, int sequence, BattleEventKind kind, EntityId entityId,
            RosterId rosterId, int side, int owner, ShipDefinition definition, RemovalReason removalReason,
            RosterStatus previousStatus, RosterStatus status, RosterTransitionReason transitionReason, int revision,
            float armorDeficit, float shieldDeficit, BattleResult result, EntityId attackerId = default,
            float damage = 0f, Vec2 point = default, BulletDefinition bulletDefinition = null, EntityId parentId = default,
            BeamDefinition beamDefinition = null)
        {
            BattleId = battleId;
            Tick = tick;
            Sequence = sequence;
            Kind = kind;
            EntityId = entityId;
            RosterId = rosterId;
            Side = side;
            Owner = owner;
            Definition = definition;
            RemovalReason = removalReason;
            PreviousStatus = previousStatus;
            Status = status;
            TransitionReason = transitionReason;
            Revision = revision;
            ArmorDeficit = armorDeficit;
            ShieldDeficit = shieldDeficit;
            Result = result;
            AttackerId = attackerId;
            Damage = damage;
            Point = point;
            BulletDefinition = bulletDefinition;
            BeamDefinition = beamDefinition;
            ParentId = parentId;
        }

        internal static BattleEvent Spawn(BattleId battleId, long tick, int sequence, ShipEntity ship) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Spawn, ship.Id, ship.Slot.RosterId, ship.Side,
                ship.Owner, ship.Definition, RemovalReason.None, default, default, RosterTransitionReason.None, 0, 0f, 0f,
                default);

        internal static BattleEvent Removal(BattleId battleId, long tick, int sequence, ShipEntity ship, RemovalReason reason) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Removal, ship.Id, ship.Slot.RosterId, ship.Side,
                ship.Owner, ship.Definition, reason, default, default, RosterTransitionReason.None, 0, 0f, 0f, default);

        internal static BattleEvent BulletSpawn(BattleId battleId, long tick, int sequence, in BulletEntity bullet) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Spawn, bullet.Id, default, bullet.Side, bullet.Owner,
                null, RemovalReason.None, default, default, RosterTransitionReason.None, 0, 0f, 0f, default,
                bulletDefinition: bullet.Definition, parentId: bullet.ParentId, beamDefinition: bullet.BeamDefinition);

        internal static BattleEvent BulletRemoval(BattleId battleId, long tick, int sequence, in BulletEntity bullet) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Removal, bullet.Id, default, bullet.Side, bullet.Owner,
                null, bullet.PendingRemoval, default, default, RosterTransitionReason.None, 0, 0f, 0f, default,
                bulletDefinition: bullet.Definition, parentId: bullet.ParentId, beamDefinition: bullet.BeamDefinition);

        internal static BattleEvent Transition(BattleId battleId, long tick, int sequence, RosterSlot slot,
            RosterStatus previousStatus, RosterTransitionReason reason, EntityId entityId) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.RosterTransition, entityId, slot.RosterId, slot.Side,
                slot.Owner, null, RemovalReason.None, previousStatus, slot.Status, reason, slot.Revision,
                slot.ArmorDeficit, slot.ShieldDeficit, default);

        internal static BattleEvent ResultEvent(BattleId battleId, long tick, int sequence, BattleResult result) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Result, default, default, 0, 0,
                null, RemovalReason.None, default, default, RosterTransitionReason.None, 0, 0f, 0f, result);

        internal static BattleEvent HitEvent(BattleId battleId, long tick, int sequence, in ContactCandidate hit) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Hit, hit.Target, default, 0, 0, null,
                RemovalReason.None, default, default, RosterTransitionReason.None, 0, 0f, 0f, default, hit.Attacker,
                hit.Damage, hit.Point);

        public override string ToString()
        {
            switch (Kind)
            {
                case BattleEventKind.Spawn:
                    return IsBullet
                        ? $"{Tick}.{Sequence} Spawn bullet {EntityId} (parent {ParentId}, side {Side})"
                        : $"{Tick}.{Sequence} Spawn {EntityId} (roster {RosterId}, side {Side})";
                case BattleEventKind.Removal:
                    return $"{Tick}.{Sequence} Removal {(IsBullet ? "bullet " : "")}{EntityId} ({RemovalReason})";
                case BattleEventKind.RosterTransition:
                    return $"{Tick}.{Sequence} Roster {RosterId}: {PreviousStatus} -> {Status} ({TransitionReason}, revision {Revision}, entity {EntityId})";
                case BattleEventKind.Result:
                    return $"{Tick}.{Sequence} {Result}";
                case BattleEventKind.Hit:
                    return $"{Tick}.{Sequence} Hit {AttackerId} -> {EntityId} ({Damage})";
                default:
                    return $"{Tick}.{Sequence} {Kind}";
            }
        }
    }
}

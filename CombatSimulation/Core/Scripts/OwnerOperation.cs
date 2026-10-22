using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Pose at the owner's spawn stage, not the end-of-tick snapshot pose.
    public readonly struct SpawnPose
    {
        public readonly Vec2 Position;
        public readonly float Angle;
        public readonly Vec2 LinearVelocity;
        public readonly float AngularVelocity;

        public SpawnPose(Vec2 position, float angle, Vec2 linearVelocity, float angularVelocity = 0f)
        {
            Position = position;
            Angle = angle;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
        }
    }

    public readonly struct RosterRecord
    {
        public readonly RosterId RosterId;
        public readonly int Revision;
        public readonly RosterStatus PreviousStatus;
        public readonly RosterStatus Status;
        public readonly RosterTransitionReason Reason;
        public readonly uint DeploymentCount;
        public readonly long DeploymentOrder;
        public readonly float ArmorDeficit;
        public readonly float ShieldDeficit;
        public readonly float DamageFraction;

        public RosterRecord(RosterId rosterId, int revision, RosterStatus previousStatus, RosterStatus status,
            RosterTransitionReason reason, uint deploymentCount, long deploymentOrder,
            float armorDeficit, float shieldDeficit, float damageFraction)
        {
            RosterId = rosterId;
            Revision = revision;
            PreviousStatus = previousStatus;
            Status = status;
            Reason = reason;
            DeploymentCount = deploymentCount;
            DeploymentOrder = deploymentOrder;
            ArmorDeficit = armorDeficit;
            ShieldDeficit = shieldDeficit;
            DamageFraction = damageFraction;
        }
    }

    public enum OwnerOperationKind : byte { Roster, DroneSpawn, Removal, BulletSpawn }

    // One logical commit. A roster operation includes its entity spawn/removal, never a second message.
    // Sequence starts at 1 per owner and covers this batch only (hits have their own sequence).
    // Tick is the owner's commit/spawn tick. The receiving presentation event uses its local apply tick.
    public readonly struct OwnerOperation
    {
        public readonly BattleId BattleId;
        public readonly int Owner;
        public readonly ulong Sequence;
        public readonly long Tick;
        public readonly OwnerOperationKind Kind;
        public readonly EntityId EntityId;
        public readonly RosterRecord Roster;
        public readonly DroneBayKey? BayKey;
        public readonly EntityId ParentId;
        public readonly SpawnPose Pose;
        public readonly RemovalReason RemovalReason;

        // Bullet spawns only: the identity inputs of SpawnIdentity.CreateChild besides parent and tick.
        public readonly int DeviceIndex;
        public readonly int BarrelIndex;
        public readonly uint SpawnSequence;

        private OwnerOperation(BattleId battleId, int owner, ulong sequence, long tick, OwnerOperationKind kind,
            EntityId entityId, RosterRecord roster = default, DroneBayKey? bayKey = null, EntityId parentId = default,
            SpawnPose pose = default, RemovalReason removalReason = RemovalReason.None,
            int deviceIndex = 0, int barrelIndex = 0, uint spawnSequence = 0)
        {
            DeviceIndex = deviceIndex;
            BarrelIndex = barrelIndex;
            SpawnSequence = spawnSequence;
            BattleId = battleId;
            Owner = owner;
            Sequence = sequence;
            Tick = tick;
            Kind = kind;
            EntityId = entityId;
            Roster = roster;
            BayKey = bayKey;
            ParentId = parentId;
            Pose = pose;
            RemovalReason = removalReason;
        }

        public static OwnerOperation RosterChange(BattleId battleId, int owner, ulong sequence, long tick,
            EntityId entityId, RosterRecord roster, SpawnPose pose = default) =>
            new OwnerOperation(battleId, owner, sequence, tick, OwnerOperationKind.Roster, entityId, roster, pose: pose);

        public static OwnerOperation DroneLaunch(BattleId battleId, int owner, ulong sequence, long tick,
            EntityId entityId, DroneBayKey bayKey, EntityId parentId, SpawnPose pose) =>
            new OwnerOperation(battleId, owner, sequence, tick, OwnerOperationKind.DroneSpawn, entityId,
                bayKey: bayKey, parentId: parentId, pose: pose);

        // A drone includes its bay; a projectile or beam has no bay. Roster ships use RosterChange.
        public static OwnerOperation Remove(BattleId battleId, int owner, ulong sequence, long tick,
            EntityId entityId, RemovalReason reason, DroneBayKey? bayKey = null) =>
            new OwnerOperation(battleId, owner, sequence, tick, OwnerOperationKind.Removal, entityId,
                bayKey: bayKey, removalReason: reason);

        // A fire confirmation: one projectile or beam the owner created. It carries no pose; the receiver places a
        // missing bullet from its own spawn history of the parent.
        public static OwnerOperation BulletFired(BattleId battleId, int owner, ulong sequence, long tick,
            EntityId entityId, EntityId parentId, int deviceIndex, int barrelIndex, uint spawnSequence) =>
            new OwnerOperation(battleId, owner, sequence, tick, OwnerOperationKind.BulletSpawn, entityId,
                parentId: parentId, deviceIndex: deviceIndex, barrelIndex: barrelIndex, spawnSequence: spawnSequence);
    }

    // "All my operations through tick Tick are sent; the last one has sequence OperationSequence" (0: none yet).
    // Runtime sends it on the reliable ordered stream after the operations of that tick.
    public readonly struct OwnerWatermark
    {
        public readonly BattleId BattleId;
        public readonly int Owner;
        public readonly long Tick;
        public readonly ulong OperationSequence;

        public OwnerWatermark(BattleId battleId, int owner, long tick, ulong operationSequence)
        {
            BattleId = battleId;
            Owner = owner;
            Tick = tick;
            OperationSequence = operationSequence;
        }
    }

    // SenderOwner comes from the peer binding, not from the decoded hit's AttackerOwner field.
    public readonly struct InboundHit
    {
        public readonly int SenderOwner;
        public readonly ulong Sequence;
        public readonly ResolvedHit Hit;

        public InboundHit(int senderOwner, ulong sequence, ResolvedHit hit)
        {
            SenderOwner = senderOwner;
            Sequence = sequence;
            Hit = hit;
        }
    }
}

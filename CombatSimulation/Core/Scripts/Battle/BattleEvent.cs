using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public enum BattleEventAuthority : byte { Committed = 0, Predicted = 1 }

    public enum BattleEventKind : byte
    {
        Spawn = 1,
        Removal = 2,
        UnitTransition = 3,
        Result = 4,

        // For presentation: one accepted hit. The damage is applied at stage 10, shield first.
        Hit = 5,
        Disarm = 6,

        // Network only (P2): a predicted copy of a remote bullet takes its owner's ID. EntityId is the new ID and
        // PreviousId the copy's; the body and its view stay.
        Rename = 7,

        // For presentation (phase 5, step 11b): a teleporter jump. EntityId is the ship, Point the start and Destination
        // the end, DeviceIndex the teleporter.
        Teleport = 8,

        // For presentation (phase 5, step 11c): a gravity generator use. EntityId is the ship, Point the centre of the
        // wave, DeviceIndex the generator.
        GravityWave = 9,

        // For presentation (phase 5, step 14): an asteroid left. EntityId is the asteroid, Point its last position, Tag
        // its AsteroidSetup.Tag (exploration objects). Side is -1: an asteroid belongs to no side. RemovalReason is Death,
        // or Vanish when a mode removed it (ModeContext.VanishAsteroid): no explosion and no loot.
        AsteroidRemoval = 10,

        // For presentation (phase 5b, step 4): what one ship lost at stage 10 of the tick, for the damage text.
        // EntityId is the ship, Damage the amount and DeviceIndex the damage type, or -1 for the shield. Armor
        // damage is after the shield and the armor resistance, by type; one event per type with damage. They come last
        // in the batch, after the result.
        Damage = 11,

        // For presentation (phase 5b, step 7b): a repair bot or a tail segment left the battle. EntityId is the object,
        // ParentId its ship, Point its last position and RemovalReason the reason. The views of these objects live by
        // the snapshot; this tells a death from an expiry or a vanish.
        AuxiliaryRemoval = 12,

        // For presentation (satellite plan, phase 6): a destructible satellite died or respawned. EntityId is its ship,
        // DeviceIndex the satellite index, Point its position, and RemovalReason Death for a death or None for a respawn.
        SatelliteChange = 13,

        // For the application (battle modes): an event that the mode published (ModeContext.Publish). ModeEvent has the
        // mode's code and values.
        Mode = 14,

        // A pickup left (loot plan, L7). EntityId is the pickup, Tag its reward ID, Point its last position, and
        // RemovalReason Collected (ParentId is the collecting ship), Expiry or Vanish. Side is -1. Views read the pickups
        // from the snapshot, so a pickup has no spawn event.
        PickupRemoval = 15,
    }

    public enum RemovalReason : byte
    {
        None,
        Death,
        Retreat,
        Expiry,
        Disposal,

        // A remote bullet that its owner's fire confirmations do not list. Never sent by an owner.
        Unconfirmed,

        // A ship left without a death (vanish command, D12). The roster records a retreat.
        Vanish,

        // A ship took a pickup (loot plan, L7). Pickups only; never sent.
        Collected,
    }

    // The same set as the application's roster change reasons (phase 3a).
    public enum UnitTransitionReason : byte
    {
        None,
        Deployed,
        Retreated,
        Killed,

        // Surrender, kill-all or a mode destroyed the slot; a ready reserve has no entity.
        ForcedDestruction,

        // A mode returned a destroyed unit to Ready (ModeContext.Respawn).
        Respawned,

        // A mode removed the unit from the table (ModeContext.RemoveUnit). The status after it is Destroyed, and no
        // later snapshot lists the unit. Local battles only.
        Removed,
    }

    // One presentation event of a tick, with explicit authority. A flat tagged form, so the batch never allocates.
    // Identity: battle ID, tick and sequence. An entity event comes before the roster transition of the same operation.
    public readonly struct BattleEvent
    {
        public readonly BattleId BattleId;
        public readonly long Tick;

        // Position in the tick's batch.
        public readonly int Sequence;

        public readonly BattleEventKind Kind;
        public readonly BattleEventAuthority Authority;

        // Spawn, removal, and a roster transition that involves an entity. Hit: the target.
        public readonly EntityId EntityId;

        // Spawn and removal of a roster ship, and roster transitions. Side and owner also for a bullet.
        public readonly UnitId UnitId;
        public readonly DroneBayKey? BayKey;
        public bool IsDrone => BayKey.HasValue;

        // Spawn and removal of a decoy (phase 5, step 10c): Definition is its DecoyDefinition.Ship.
        public readonly bool IsDecoy;
        public readonly int Side;
        public readonly int Owner;

        // Spawn and removal of a ship: the view factory builds the view from it. Teleport and gravity wave: the ship's.
        // Null for a bullet.
        public readonly ShipDefinition Definition;

        public readonly RemovalReason RemovalReason;

        // Bullet removal (phase 5b, step 6, owner question 7 (a)): the trigger conditions of the removal, for the sound
        // and effect triggers of the view. Presentation only. The owner gives the conditions that fired; another device
        // derives them from the reason and the definition (BulletTriggers.RemovalConditions).
        public readonly BulletTriggerConditions RemovalConditions;

        public readonly UnitStatus PreviousStatus;
        public readonly UnitStatus Status;
        public readonly UnitTransitionReason TransitionReason;
        public readonly int Revision;

        // Roster transition: the deficits the slot retains after it (B4).
        public readonly float ArmorDeficit;
        public readonly float ShieldDeficit;

        public readonly BattleResult Result;

        // Hit: the attacker, the damage before the shield, and the contact point (zero for a ship touch). Ship removal:
        // the ship's last position, and Velocity its linear velocity (loot plan, L2: the mode places the drop).
        public readonly EntityId AttackerId;
        public readonly float Damage;
        public readonly Vec2 Point;
        public readonly Vec2 Velocity;
        // Point-defense or gravity-wave hit, disarm: index on the attacking ship. Teleport and gravity wave: the device.
        // Other events use -1.
        public readonly int DeviceIndex;

        // Teleport: the landing point.
        public readonly Vec2 Destination;

        // Bullet definitions are null for ships. ParentId is the firing entity or a drone's current mothership.
        public readonly BulletDefinition BulletDefinition;
        public readonly BeamDefinition BeamDefinition;
        public readonly EntityId ParentId;

        // Rename: the copy's ID before it.
        public readonly EntityId PreviousId;

        // Mode: the published code and values.
        public readonly ModeEvent ModeEvent;

        // Asteroid removal: the asteroid's opaque tag.
        public readonly long Tag;

        public bool IsBullet => BulletDefinition != null || IsBeam;
        public bool IsBeam => BeamDefinition != null;

        private BattleEvent(BattleId battleId, long tick, int sequence, BattleEventKind kind, EntityId entityId,
            UnitId rosterId, int side, int owner, ShipDefinition definition, RemovalReason removalReason,
            UnitStatus previousStatus, UnitStatus status, UnitTransitionReason transitionReason, int revision,
            float armorDeficit, float shieldDeficit, BattleResult result, EntityId attackerId = default,
            float damage = 0f, Vec2 point = default, BulletDefinition bulletDefinition = null, EntityId parentId = default,
            BeamDefinition beamDefinition = null, int deviceIndex = -1, DroneBayKey? bayKey = null,
            BattleEventAuthority authority = BattleEventAuthority.Committed, EntityId previousId = default,
            bool isDecoy = false, Vec2 destination = default,
            BulletTriggerConditions removalConditions = BulletTriggerConditions.None, ModeEvent modeEvent = default,
            long tag = 0, Vec2 velocity = default)
        {
            BattleId = battleId;
            Tick = tick;
            Sequence = sequence;
            Kind = kind;
            Authority = authority;
            EntityId = entityId;
            UnitId = rosterId;
            BayKey = bayKey;
            IsDecoy = isDecoy;
            Side = side;
            Owner = owner;
            Definition = definition;
            RemovalReason = removalReason;
            RemovalConditions = removalConditions;
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
            Velocity = velocity;
            DeviceIndex = deviceIndex;
            Destination = destination;
            BulletDefinition = bulletDefinition;
            BeamDefinition = beamDefinition;
            ParentId = parentId;
            PreviousId = previousId;
            ModeEvent = modeEvent;
            Tag = tag;
        }

        internal static BattleEvent Spawn(BattleId battleId, long tick, int sequence, ShipEntity ship,
            BattleEventAuthority authority = BattleEventAuthority.Committed) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Spawn, ship.Id, ship.Slot?.UnitId ?? default, ship.Side,
                ship.Owner, ship.Definition, RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f,
                default, parentId: ship.MothershipId, bayKey: ship.Bay?.Key, authority: authority, isDecoy: ship.IsDecoy);

        internal static BattleEvent Removal(BattleId battleId, long tick, int sequence, ShipEntity ship, RemovalReason reason,
            Vec2 position, Vec2 velocity) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Removal, ship.Id, ship.Slot?.UnitId ?? default, ship.Side,
                ship.Owner, ship.Definition, reason, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                point: position, parentId: ship.MothershipId, bayKey: ship.Bay?.Key, isDecoy: ship.IsDecoy,
                velocity: velocity);

        internal static BattleEvent BulletSpawn(BattleId battleId, long tick, int sequence, in BulletEntity bullet,
            BattleEventAuthority authority = BattleEventAuthority.Committed) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Spawn, bullet.Id, default, bullet.Side, bullet.Owner,
                null, RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                bulletDefinition: bullet.Definition, parentId: bullet.ParentId, beamDefinition: bullet.BeamDefinition, authority: authority);

        internal static BattleEvent BulletRemoval(BattleId battleId, long tick, int sequence, in BulletEntity bullet,
            BulletTriggerConditions conditions, BattleEventAuthority authority = BattleEventAuthority.Committed) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Removal, bullet.Id, default, bullet.Side, bullet.Owner,
                null, bullet.PendingRemoval, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                bulletDefinition: bullet.Definition, parentId: bullet.ParentId, beamDefinition: bullet.BeamDefinition, authority: authority,
                removalConditions: conditions);

        internal static BattleEvent BulletRename(BattleId battleId, long tick, int sequence, in BulletEntity bullet,
            EntityId previousId) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Rename, bullet.Id, default, bullet.Side, bullet.Owner,
                null, RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                bulletDefinition: bullet.Definition, parentId: bullet.ParentId, beamDefinition: bullet.BeamDefinition,
                previousId: previousId);

        internal static BattleEvent Transition(BattleId battleId, long tick, int sequence, Unit slot,
            UnitStatus previousStatus, UnitTransitionReason reason, EntityId entityId,
            BattleEventAuthority authority = BattleEventAuthority.Committed) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.UnitTransition, entityId, slot.UnitId, slot.Side,
                slot.Owner, null, RemovalReason.None, previousStatus, slot.Status, reason, slot.Revision,
                slot.ArmorDeficit, slot.ShieldDeficit, default, authority: authority);

        internal static BattleEvent ResultEvent(BattleId battleId, long tick, int sequence, BattleResult result) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Result, default, default, 0, 0,
                null, RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f, result);

        internal static BattleEvent HitEvent(BattleId battleId, long tick, int sequence, in ContactCandidate hit,
            BattleEventAuthority authority = BattleEventAuthority.Committed) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Hit, hit.Target, default, 0, 0, null,
                RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f, default, hit.Attacker,
                hit.Damage, hit.Point, deviceIndex: hit.AttackerDeviceIndex, authority: authority);

        internal static BattleEvent DisarmEvent(BattleId battleId, long tick, int sequence, in ContactCandidate hit,
            BattleEventAuthority authority = BattleEventAuthority.Committed) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Disarm, hit.Target, default, 0, 0, null,
                RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                attackerId: hit.Attacker, point: hit.Point, deviceIndex: hit.AttackerDeviceIndex, authority: authority);

        internal static BattleEvent RemoteDisarm(BattleId battleId, long tick, int sequence, in ResolvedHit hit) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Disarm, hit.TargetId, default, 0, hit.TargetOwner, null,
                RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                attackerId: hit.AttackerId, deviceIndex: hit.DeviceIndex);

        internal static BattleEvent RemoteHit(BattleId battleId, long tick, int sequence, in ResolvedHit hit, float damage) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Hit, hit.TargetId, default, 0,
                hit.TargetOwner, null, RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f,
                default, hit.AttackerId, damage, deviceIndex: hit.DeviceIndex);

        internal static BattleEvent Teleport(BattleId battleId, long tick, int sequence, ShipEntity ship, int deviceIndex,
            Vec2 from, Vec2 to, BattleEventAuthority authority) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Teleport, ship.Id, ship.Slot?.UnitId ?? default,
                ship.Side, ship.Owner, ship.Definition, RemovalReason.None, default, default, UnitTransitionReason.None, 0,
                0f, 0f, default, point: from, deviceIndex: deviceIndex, authority: authority, destination: to);

        internal static BattleEvent GravityWave(BattleId battleId, long tick, int sequence, ShipEntity ship, int deviceIndex,
            Vec2 origin, BattleEventAuthority authority) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.GravityWave, ship.Id, ship.Slot?.UnitId ?? default,
                ship.Side, ship.Owner, ship.Definition, RemovalReason.None, default, default, UnitTransitionReason.None, 0,
                0f, 0f, default, point: origin, deviceIndex: deviceIndex, authority: authority);

        internal static BattleEvent AsteroidRemoval(BattleId battleId, long tick, int sequence, Asteroid asteroid,
            Vec2 position, RemovalReason reason) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.AsteroidRemoval, asteroid.Id, default, -1,
                asteroid.Owner, null, reason, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                point: position, tag: asteroid.Setup.Tag);

        internal static BattleEvent PickupRemoval(BattleId battleId, long tick, int sequence, Pickup pickup) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.PickupRemoval, pickup.Id, default, -1, 0, null,
                pickup.PendingRemoval, default, default, UnitTransitionReason.None, 0, 0f, 0f, default,
                point: pickup.Position, parentId: pickup.Collector, tag: pickup.RewardId);

        internal static BattleEvent AuxiliaryRemoval(BattleId battleId, long tick, int sequence, EntityId id, int owner,
            EntityId shipId, Vec2 position, RemovalReason reason) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.AuxiliaryRemoval, id, default, -1, owner, null, reason,
                default, default, UnitTransitionReason.None, 0, 0f, 0f, default, point: position, parentId: shipId);

        internal static BattleEvent SatelliteChange(BattleId battleId, long tick, int sequence, ShipEntity ship, int satellite,
            Vec2 position, bool destroyed, BattleEventAuthority authority) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.SatelliteChange, ship.Id, ship.Slot?.UnitId ?? default,
                ship.Side, ship.Owner, ship.Definition, destroyed ? RemovalReason.Death : RemovalReason.None, default, default,
                UnitTransitionReason.None, 0, 0f, 0f, default, point: position, deviceIndex: satellite, authority: authority);

        internal static BattleEvent DamageTaken(BattleId battleId, long tick, int sequence, ShipEntity ship, int damageType,
            float amount, BattleEventAuthority authority) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Damage, ship.Id, ship.Slot?.UnitId ?? default,
                ship.Side, ship.Owner, ship.Definition, RemovalReason.None, default, default, UnitTransitionReason.None, 0,
                0f, 0f, default, damage: amount, deviceIndex: damageType, authority: authority);

        internal static BattleEvent ModeItem(BattleId battleId, long tick, int sequence, in ModeEvent item) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Mode, default, default, -1, 0, null,
                RemovalReason.None, default, default, UnitTransitionReason.None, 0, 0f, 0f, default, modeEvent: item);

        public override string ToString()
        {
            switch (Kind)
            {
                case BattleEventKind.Spawn:
                    return IsBullet
                        ? $"{Tick}.{Sequence} Spawn bullet {EntityId} (parent {ParentId}, side {Side})"
                        : $"{Tick}.{Sequence} Spawn {EntityId} (roster {UnitId}, side {Side})";
                case BattleEventKind.Removal:
                    return $"{Tick}.{Sequence} Removal {(IsBullet ? "bullet " : "")}{EntityId} ({RemovalReason})";
                case BattleEventKind.UnitTransition:
                    return $"{Tick}.{Sequence} Roster {UnitId}: {PreviousStatus} -> {Status} ({TransitionReason}, revision {Revision}, entity {EntityId})";
                case BattleEventKind.Result:
                    return $"{Tick}.{Sequence} {Result}";
                case BattleEventKind.Hit:
                    return $"{Tick}.{Sequence} Hit {AttackerId} -> {EntityId} ({Damage})";
                case BattleEventKind.Disarm:
                    return $"{Tick}.{Sequence} Disarm {AttackerId} device {DeviceIndex} -> {EntityId}";
                case BattleEventKind.Rename:
                    return $"{Tick}.{Sequence} Rename bullet {PreviousId} -> {EntityId}";
                case BattleEventKind.Teleport:
                    return $"{Tick}.{Sequence} Teleport {EntityId} device {DeviceIndex}: {Point} -> {Destination}";
                case BattleEventKind.GravityWave:
                    return $"{Tick}.{Sequence} Gravity wave {EntityId} device {DeviceIndex} at {Point}";
                case BattleEventKind.AsteroidRemoval:
                    return $"{Tick}.{Sequence} Asteroid {EntityId} {(RemovalReason == RemovalReason.Vanish ? "vanished" : "destroyed")} at {Point}";
                case BattleEventKind.AuxiliaryRemoval:
                    return $"{Tick}.{Sequence} Removal {EntityId} of {ParentId} ({RemovalReason}) at {Point}";
                case BattleEventKind.SatelliteChange:
                    return $"{Tick}.{Sequence} Satellite {DeviceIndex} of {EntityId} {(RemovalReason == RemovalReason.Death ? "destroyed" : "respawned")} at {Point}";
                case BattleEventKind.Damage:
                    return $"{Tick}.{Sequence} Damage {EntityId} type {DeviceIndex}: {Damage}";
                case BattleEventKind.Mode:
                    return $"{Tick}.{Sequence} Mode {ModeEvent.Code} ({ModeEvent.Value0}, {ModeEvent.Value1})";
                case BattleEventKind.PickupRemoval:
                    return RemovalReason == RemovalReason.Collected
                        ? $"{Tick}.{Sequence} Pickup {EntityId} (reward {Tag}) collected by {ParentId}"
                        : $"{Tick}.{Sequence} Pickup {EntityId} (reward {Tag}) {RemovalReason}";
                default:
                    return $"{Tick}.{Sequence} {Kind}";
            }
        }
    }
}

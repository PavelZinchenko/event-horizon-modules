using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public readonly struct ResourceState
    {
        public readonly float Value;

        // Zero means the ship has no such resource.
        public readonly float Max;

        public ResourceState(float value, float max)
        {
            Value = value;
            Max = max;
        }

        public bool Exists => Max > 0f;
    }

    // One ship at the end of a completed tick. A value copy: it never refers to core state.
    public readonly struct ShipState
    {
        public readonly EntityId Id;
        public readonly RosterId RosterId;
        // Drones have no roster slot. BayKey identifies their persistent owner instead.
        public readonly DroneBayKey? BayKey;
        public readonly EntityId MothershipId;
        public readonly EntityId? MothershipOverride;
        public bool IsDrone => BayKey.HasValue;
        public readonly int Side;
        // Hostility (D13). A side's ships start with Allegiances.OfSide; a drone can change it.
        public readonly Allegiance Allegiance;
        public readonly int Owner;
        public readonly ShipDefinition Definition;
        public readonly TargetPriority TargetPriority;

        // Canonical torus coordinates and radians.
        public readonly Vec2 Position;
        public readonly float Angle;
        public readonly Vec2 LinearVelocity;
        public readonly float AngularVelocity;

        public readonly ResourceState Armor;
        public readonly ResourceState Shield;
        public readonly ResourceState Energy;

        // Controls in force: the last applied frame.
        public readonly float Throttle;
        public readonly bool HasCourse;
        public readonly float Course;
        public readonly ulong Systems;

        // The absolute tick at which the retreat completes, or -1 without a retreat.
        public readonly long RetreatCompletionTick;

        // The status of the tick's running device uses (phase 5, step 7).
        public readonly ShipStatusFlags Status;

        // The share from 0 to 1 by which the view moves the ship colour to its device colour (phase 5, step 7b).
        public readonly float Tint;

        // The resistance share of the tick's device statuses (ShipStatus.ResistanceShare; phase 5, step 7d), for the HUD.
        public readonly float ResistanceShare;

        // The first of this ship's Definition.Devices.Count entries in the snapshot's device buffer.
        internal readonly int DeviceStart;
        internal readonly int BarrelStart;

        internal ShipState(ShipEntity ship, PhysicsWorld world, int deviceStart, int barrelStart = 0)
        {
            Id = ship.Id;
            RosterId = ship.Slot?.RosterId ?? default;
            BayKey = ship.Bay?.Key;
            MothershipId = ship.MothershipId;
            MothershipOverride = ship.MothershipOverride;
            Side = ship.Side;
            Allegiance = ship.Allegiance;
            Owner = ship.Owner;
            Definition = ship.Definition;
            TargetPriority = ship.TargetPriority;
            Position = world.GetPosition(ship.Body);
            Angle = world.GetAngle(ship.Body);
            LinearVelocity = world.GetLinearVelocity(ship.Body);
            AngularVelocity = world.GetAngularVelocity(ship.Body);
            Armor = new ResourceState(ship.Armor.Value, ship.Armor.Max);
            Shield = new ResourceState(ship.Shield.Value, ship.Shield.Max);
            Energy = new ResourceState(ship.Energy.Value, ship.Energy.Max);
            Throttle = ship.Throttle;
            HasCourse = ship.HasCourse;
            Course = ship.Course;
            Systems = ship.Systems;
            RetreatCompletionTick = ship.RetreatCompletionTick;
            Status = ship.Status;
            Tint = ship.Tint;
            ResistanceShare = ship.ResistanceShare;
            DeviceStart = deviceStart;
            BarrelStart = barrelStart;
        }

        public bool IsRetreating => RetreatCompletionTick != ShipEntity.NoRetreat;
    }

    // One projectile or beam at the end of a completed tick. A value copy.
    public readonly struct BulletState
    {
        public readonly EntityId Id;
        public readonly EntityId ParentId;
        public readonly int Side;
        public readonly Allegiance Allegiance;
        public readonly int Owner;
        // Exactly one definition is set: Definition for a projectile, BeamDefinition for a beam.
        public readonly BulletDefinition Definition;
        public readonly BeamDefinition BeamDefinition;
        public bool IsBeam => BeamDefinition != null;

        // Beams: the sampled ray endpoint and selected entity, or no entity at full range. A beam bolt (phase 5, step 4e):
        // the end of this tick's ray from the bolt's pose, at its first hit for a ray without pass-through.
        public readonly Vec2 BeamEndPoint;
        public readonly EntityId BeamTargetId;
        // A stuck hook or mine; null while free. AI reads this without accessing physics.
        public readonly EntityId AttachedTargetId;
        public bool HasRay => IsBeam || Definition.IsBeamBolt;

        // Canonical torus coordinates and radians.
        public readonly Vec2 Position;
        public readonly float Angle;
        public readonly Vec2 LinearVelocity;

        // The last tick a projectile flies in. Beams have device-owned lifetime and use long.MaxValue.
        public readonly long ExpiryTick;

        // An aura's share of its full size in this tick (phase 5, step 4g, legacy AuraController power); 1 otherwise.
        public readonly float AuraPower;
        // Absolute arc scale, shared by the collider and view; zero for other bullets.
        public readonly float WaveScale;

        internal BulletState(in BulletEntity bullet, PhysicsWorld world)
        {
            Id = bullet.Id;
            ParentId = bullet.ParentId;
            Side = bullet.Side;
            Allegiance = bullet.Allegiance;
            Owner = bullet.Owner;
            Definition = bullet.Definition;
            BeamDefinition = bullet.BeamDefinition;
            BeamTargetId = bullet.IsBeam ? bullet.TargetId : default;
            AttachedTargetId = bullet.Stuck ? bullet.TargetId : default;
            Position = bullet.IsBeam ? bullet.BeamOrigin : world.GetBulletPosition(bullet.Body);
            Angle = bullet.IsBeam ? bullet.BeamAngle : world.GetBulletRotation(bullet.Body).Angle;
            if (bullet.IsBeam)
                BeamEndPoint = bullet.BeamEndPoint;
            else if (bullet.Definition.IsBeamBolt)
            {
                var heading = world.GetBulletRotation(bullet.Body);
                BeamEndPoint = world.Torus.Wrap(Position + bullet.RayLength * new Vec2(heading.C, heading.S));
            }
            else
                BeamEndPoint = default;
            LinearVelocity = bullet.IsBeam ? Vec2.Zero : world.GetBulletVelocity(bullet.Body);
            ExpiryTick = bullet.ExpiryTick;
            AuraPower = Auras.Power(bullet);
            WaveScale = bullet.WaveScale;
        }
    }

    // One roster slot at the end of a completed tick.
    public readonly struct RosterSlotState
    {
        public readonly RosterId RosterId;
        public readonly int Side;
        public readonly int Owner;
        public readonly RosterStatus Status;
        public readonly uint DeploymentCount;

        // Battle-wide order of the last deployment. Zero: never deployed.
        public readonly long DeploymentOrder;

        public readonly int Revision;

        // Null unless the slot is Active.
        public readonly EntityId ActiveEntity;

        // Retained from the last retreat (B4).
        public readonly float ArmorDeficit;
        public readonly float ShieldDeficit;

        // The share of the slot's armor that is lost (B2 participation facts): 1 when destroyed, the armor deficit
        // over the maximum while active or after a retreat, 0 when never deployed. A consumer reads it from the
        // snapshot of a Retreated or Killed transition and of the result; the tick's roster updates are applied.
        public readonly float DamageFraction;

        internal RosterSlotState(RosterSlot slot)
        {
            RosterId = slot.RosterId;
            Side = slot.Side;
            Owner = slot.Owner;
            Status = slot.Status;
            DeploymentCount = slot.DeploymentCount;
            DeploymentOrder = slot.DeploymentOrder;
            Revision = slot.Revision;
            ActiveEntity = slot.ActiveShip?.Id ?? default;
            ArmorDeficit = slot.ArmorDeficit;
            ShieldDeficit = slot.ShieldDeficit;
            DamageFraction = slot.OwnerDamageFraction ?? GetDamageFraction(slot);
        }

        private static float GetDamageFraction(RosterSlot slot)
        {
            if (slot.Status == RosterStatus.Destroyed) return 1f;

            var ship = slot.ActiveShip;
            if (ship != null) return ship.Armor.Deficit / ship.Armor.Max;

            return slot.ArmorDeficit / slot.Setup.Ship.Armor;
        }
    }
}

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
        public readonly int Side;
        public readonly int Owner;
        public readonly ShipDefinition Definition;

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

        internal ShipState(ShipEntity ship, PhysicsWorld world)
        {
            Id = ship.Id;
            RosterId = ship.Slot.RosterId;
            Side = ship.Side;
            Owner = ship.Owner;
            Definition = ship.Definition;
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
        }

        public bool IsRetreating => RetreatCompletionTick != ShipEntity.NoRetreat;
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
            DamageFraction = GetDamageFraction(slot);
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

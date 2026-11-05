using System;

namespace CombatSimulation.Core
{
    // Legacy WormTailDeviceV2 (DeviceClass.WormTailV2; phase 5, step 7d). A passive device: the owner grows a chain of
    // Size segments behind the ship, one segment after another, and regrows a lost segment after Cooldown. Each segment
    // has Power x the ship's armor as hit points; a hit on a segment goes to the tip of the tail first. The share of the
    // tail that is attached moves each armor resistance of the ship toward its ceiling (ShipStatus.ResistanceShare).
    // Rules and examples: TODO/battle_simulator_phase5.md, "WormTailV2".
    public sealed class WormTailDefinition : DeviceDefinition
    {
        // Legacy WormSegmentFactory and WormTailDeviceV2.
        public const float ScaleFactor = 0.95f;
        public const float HeadExtraOffset = 0.15f;
        public static readonly float HeadLimit = 75f * (float)(Math.PI / 180.0);
        public static readonly float SegmentLimit = 40f * (float)(Math.PI / 180.0);
        public const float FirstGrowthDelay = 0.05f;
        public const float RegrowthDelay = 0.5f;
        public const int UnbreakableSegments = 2;

        // Legacy WormSegmentBase: a detached segment destroys itself after 5 s.
        public const float SelfDestructDelay = 5f;

        // Legacy WormSegmentDestroyable: recent damage falls by 5 x the hit points per second.
        public const float TearRecoveryRate = 5f;

        // Legacy WormSegmentBase and its subclasses: a segment takes a tenth of a hit's push and loses 5 x its sideways
        // speed per second.
        public const float ImpulseShare = 0.1f;
        public const float SideDamping = 5f;

        // P4 closure, decision 1: the core caps the turn of a ship with a tail at the fastest database worm (worm1_2:
        // 941.9°/s, 2,446.4°/s²), where the hinge gate passed. No database worm changes.
        public static readonly float MaxAngularVelocity = 942f * (float)(Math.PI / 180.0);
        public static readonly float MaxTurnRate = 2447f * (float)(Math.PI / 180.0);

        // size: legacy Mathf.RoundToInt(Size). radius: the circle of the segment prefab at body scale 1.
        public WormTailDefinition(float power, int size, float cooldown, float parentOffset, float jointOffset,
            float radius)
        {
            if (!(power >= 0f) || float.IsInfinity(power))
                throw new ArgumentOutOfRangeException(nameof(power), "Power must be finite and not negative.");
            if (size < 1)
                throw new ArgumentOutOfRangeException(nameof(size), "A tail has at least one segment.");
            if (!(cooldown >= 0f) || float.IsInfinity(cooldown))
                throw new ArgumentOutOfRangeException(nameof(cooldown), "Cooldown must be finite and not negative.");
            if (float.IsNaN(parentOffset) || float.IsInfinity(parentOffset))
                throw new ArgumentOutOfRangeException(nameof(parentOffset), "Parent offset must be finite.");
            if (float.IsNaN(jointOffset) || float.IsInfinity(jointOffset))
                throw new ArgumentOutOfRangeException(nameof(jointOffset), "Joint offset must be finite.");
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius), "Segment radius must be finite and positive.");

            Power = power;
            Size = size;
            Cooldown = cooldown;
            ParentOffset = parentOffset;
            JointOffset = jointOffset;
            Radius = radius;
            CooldownTicks = TickTime.AtLeast(cooldown);
        }

        // Segment hit points as a share of the ship's armor.
        public float Power { get; }
        public int Size { get; }

        // Seconds from the loss of a segment, or from a hit its parent absorbed, to its regrowth.
        public float Cooldown { get; }

        // Legacy device Offset: the hinge sits ParentOffset x the parent scale behind the parent's origin (plus
        // HeadExtraOffset at the ship) and JointOffset x the segment scale ahead of the segment's origin.
        public float ParentOffset { get; }
        public float JointOffset { get; }
        public float Radius { get; }

        internal int CooldownTicks { get; }
        internal static readonly int FirstGrowthTicks = TickTime.AtLeast(FirstGrowthDelay);
        internal static readonly int RegrowthTicks = TickTime.AtLeast(RegrowthDelay);
        internal static readonly int SelfDestructTicks = TickTime.AtLeast(SelfDestructDelay);

        // The engine of a ship with a tail, turned no faster than the cap.
        internal static EngineDefinition CapEngine(EngineDefinition engine) =>
            engine == null || (engine.TurnRate <= MaxTurnRate && engine.MaxAngularVelocity <= MaxAngularVelocity)
                ? engine
                : new EngineDefinition(engine.Propulsion, engine.MaxVelocity, Math.Min(engine.TurnRate, MaxTurnRate),
                    Math.Min(engine.MaxAngularVelocity, MaxAngularVelocity));

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new WormTailDevice(this, index);
    }

    // The device keeps the head of the tail. Battle.Tails runs the chain: only the owner grows segments and applies
    // their damage; a remote copy follows the owner's spawn and removal records (H8).
    internal sealed class WormTailDevice : ShipDevice
    {
        public WormTailDevice(WormTailDefinition definition, int index) : base(index) => Definition = definition;

        public WormTailDefinition Definition { get; }

        // The first segment, or a null handle.
        public EntityHandle Head { get; set; }

        // Legacy WormTailDeviceV2: the tail appears at the first update after deployment.
        public long HeadGrowthTick { get; private set; }
        public bool HasGrown { get; set; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            Head = default;
            HeadGrowthTick = tick;
            HasGrown = false;
        }

        // Step 6c makes head loss reachable while the ship lives. Legacy waits 1.5 cooldowns before rebuilding it.
        public void HeadRemoved(long tick)
        {
            Head = default;
            HeadGrowthTick = tick + TickTime.AtLeast(1.5f * Definition.Cooldown);
        }

        public override void Update(DeviceContext context, ShipEntity ship) => context.GrowTail(ship, this);

        // Legacy segments update while their ship retreats.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship) => Update(context, ship);

        // Legacy WormTailDeviceV2.TryApplyModification: the attached share of the tail.
        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status) =>
            status.AddResistanceShare(context.CountAttachedSegments(this) / (float)Definition.Size);

        // Legacy CanBeActivated is false: the device has no use.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(false, false, 0f);
    }
}

using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy GravityGenerator (DeviceClass.GravityGenerator; phase 5, step 11c). A held bit with the cooldown passed and
    // the energy cost in store spends the cost and starts a gravity wave at the ship centre; the cooldown runs from the
    // use. The wave is a device effect, not a bullet (owner decision 5): for WaveTicks ticks a circle of Range at the use
    // point finds the units, each once per use. It destroys bullets and pushes the other units by Power x sqrt(mass) away
    // from the use point. It skips the own ship, its shields and satellites, its tail, its repair bot and the bullets
    // attached to it (owner decision 4).
    public sealed class GravityGeneratorDefinition : DeviceDefinition
    {
        // Legacy CreateGravityBomb: the bomb lives 0.5 s whatever the record says.
        public const int WaveTicks = 25;

        public GravityGeneratorDefinition(float energyCost, float cooldown, float range, float power)
        {
            if (!(range >= 0f) || float.IsInfinity(range)) throw new ArgumentOutOfRangeException(nameof(range));
            if (float.IsNaN(power) || float.IsInfinity(power)) throw new ArgumentOutOfRangeException(nameof(power));

            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtStart, 0f);
            Range = range;
            Power = power;
        }

        public ActivationPolicy Activation { get; }

        // Legacy Range: the radius of the wave.
        public float Range { get; }

        // Legacy Power: the impulse is Power x sqrt(target mass), so the speed change is Power / sqrt(mass).
        public float Power { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new GravityGeneratorDevice(this, index);
    }

    // Only the owner runs the wave. A remote copy takes the owner's use record for its cooldown and the view; the owner's
    // hit records push remote ships, and its stop records destroy remote bullets.
    internal sealed class GravityGeneratorDevice : ShipDevice
    {
        private const long NoWave = long.MinValue / 2;

        public GravityGeneratorDevice(GravityGeneratorDefinition definition, int index) : base(index) =>
            Definition = definition;

        public GravityGeneratorDefinition Definition { get; }

        // The use point of the running wave.
        public Vec2 Origin { get; private set; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
            _waveTick = NoWave;
            _touched.Clear();
        }

        // Legacy Active with CanBeActivated: the held bit, the cooldown and the cost. While held, it fires at each cooldown.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && _use.IsReady(Definition.Activation, ship, context.Tick);
            if (!IsActive || !context.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, context.Tick)) return;

            Origin = context.Physics.GetPosition(ship.Body);
            _waveTick = context.Tick;
            _touched.Clear();
            context.AddGravityWave(ship, Index, Origin);
            context.CaptureDeviceUse(ship, Index, true, context.Tick);
        }

        // The copy runs no wave; its flash uses its own pose.
        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (!started || _use.LastUseTick == tick) return;

            _use.Start(Definition.Activation, tick);
            context.AddGravityWave(ship, Index, context.Physics.GetPosition(ship.Body));
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        // Stage 6: the wave runs from the use tick for WaveTicks ticks.
        internal bool IsWaving(long tick) => tick - _waveTick < GravityGeneratorDefinition.WaveTicks;

        // Each unit counts once per use (legacy OnTriggerEnter2D on a fresh bomb).
        internal bool HasTouched(EntityId id) => _touched.Contains(id);
        internal void Touch(EntityId id) => _touched.Add(id);

        private DeviceActivation _use;
        private long _waveTick = NoWave;
        private readonly HashSet<EntityId> _touched = new HashSet<EntityId>();
    }
}

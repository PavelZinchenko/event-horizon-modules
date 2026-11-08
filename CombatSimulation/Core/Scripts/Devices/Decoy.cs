using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy DecoyDevice (DeviceClass.Decoy; phase 5, step 10c). A held bit with the cooldown passed and the energy cost
    // in store spends the cost and launches Count decoys; the cooldown runs from the use. A decoy is a ship entity of
    // its own kind (owner question 2a): Ship is its empty definition, with the hit points as armor, no engine, no
    // devices, no resistances, no ramming damage and target priority High. It drifts and slows (Decoys), is no
    // teleport target, does not keep its side alive and gives no kill.
    public sealed class DecoyDefinition : DeviceDefinition
    {
        // Legacy SpaceObjectFactory.CreateDecoy: body scale 0.5 and weight 0.1; the prefab circle has radius 0.5 x scale.
        public const float Size = 0.5f;
        public const float Mass = 0.1f;

        public DecoyDefinition(float energyCost, float cooldown, int count, float range, float lifetime, float hitPoints)
        {
            if (!(range >= 0f) || float.IsInfinity(range)) throw new ArgumentOutOfRangeException(nameof(range));
            if (!(lifetime >= 0f) || float.IsInfinity(lifetime)) throw new ArgumentOutOfRangeException(nameof(lifetime));
            if (!(hitPoints > 0f) || float.IsInfinity(hitPoints)) throw new ArgumentOutOfRangeException(nameof(hitPoints));

            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtStart, 0f);
            Count = Math.Max(0, count);
            Range = range;
            Lifetime = lifetime;
            HitPoints = hitPoints;
            Ship = new ShipDefinition(0.5f * Size, Mass, NoEngine, hitPoints, default, default,
                rammingDamageMultiplier: 0f, scale: Size, teleportImmune: true, targetPriority: TargetPriority.High);
        }

        public ActivationPolicy Activation { get; }

        // Legacy RoundToInt(Power).
        public int Count { get; }

        // Legacy Range: the launch speed relative to the ship.
        public float Range { get; }

        // Legacy Lifetime; each decoy lives it x a random 0.75 to 1.25.
        public float Lifetime { get; }

        // Legacy ArmorMultiplier x 10 of the ship.
        public float HitPoints { get; }

        public ShipDefinition Ship { get; }

        // Legacy DecoyDevice.CreateDecoys: (90 + 180 i) / count in integer degrees, plus the heading, plus 90.
        internal float LaunchAngle(int number, float heading) =>
            (90 + 180 * number) / Count * (float)(Math.PI / 180.0) + heading + (float)(Math.PI / 2.0);

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new DecoyDevice(this, index);

        private static readonly EngineDefinition NoEngine = new EngineDefinition(0f, 0f, 0f, 0f);
    }

    // Only the owner launches. A remote copy creates each decoy from the owner's launch record and starts its cooldown
    // there (H8).
    internal sealed class DecoyDevice : ShipDevice
    {
        public DecoyDevice(DecoyDefinition definition, int index) : base(index) => Definition = definition;

        public DecoyDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
        }

        // Legacy Active with CanBeActivated: the held bit, the cooldown and the cost.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && _use.IsReady(Definition.Activation, ship, context.Tick);
            if (!IsActive || !context.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, context.Tick)) return;

            for (var i = 0; i < Definition.Count; ++i)
                context.RequestSpawn(SpawnRequest.Decoy(ship, Index, i));
        }

        // The first launch record of a use starts the copy's cooldown at the owner's tick.
        internal void OnRemoteLaunch(long tick)
        {
            if (_use.LastUseTick != tick) _use.Start(Definition.Activation, tick);
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        private DeviceActivation _use;
    }
}

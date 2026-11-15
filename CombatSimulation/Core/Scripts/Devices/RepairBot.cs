using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public sealed class RepairBotDefinition : DeviceDefinition
    {
        public RepairBotDefinition(float energyPerSecond, float cooldown, float respawnTime, float size,
            float range, float hitPoints, float repairPercent)
        {
            Activation = new ActivationPolicy(0f, cooldown, CooldownStart.AtEnd, 0f);
            if (!(energyPerSecond >= 0f) || float.IsInfinity(energyPerSecond)) throw new ArgumentOutOfRangeException(nameof(energyPerSecond));
            if (!(respawnTime >= 0f) || float.IsInfinity(respawnTime)) throw new ArgumentOutOfRangeException(nameof(respawnTime));
            if (!(size > 0f) || float.IsInfinity(size)) throw new ArgumentOutOfRangeException(nameof(size));
            if (!(range >= 0f) || float.IsInfinity(range)) throw new ArgumentOutOfRangeException(nameof(range));
            if (!(hitPoints >= 0f) || float.IsInfinity(hitPoints)) throw new ArgumentOutOfRangeException(nameof(hitPoints));
            if (!(repairPercent >= 0f) || float.IsInfinity(repairPercent)) throw new ArgumentOutOfRangeException(nameof(repairPercent));
            EnergyPerSecond = energyPerSecond;
            RespawnTicks = TickTime.AtLeast(respawnTime);
            Size = size;
            Range = range;
            HitPoints = hitPoints;
            RepairPercent = repairPercent;
            Motion = new SatelliteDefinition(Vec2.Zero, 0f, size, angularFactor: 5f);
        }

        public float EnergyPerSecond { get; }
        public float Size { get; }
        public float Range { get; }
        public float HitPoints { get; }
        public float RepairPercent { get; }
        public ActivationPolicy Activation { get; }
        internal int RespawnTicks { get; }
        internal SatelliteDefinition Motion { get; }
        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new RepairBotDevice(this, index);
    }

    internal sealed class RepairBotDevice : ShipDevice
    {
        public RepairBotDevice(RepairBotDefinition definition, int index) : base(index) => Definition = definition;
        public RepairBotDefinition Definition { get; }
        internal EntityId Bot;
        internal bool Running => _use.IsRunning;
        internal long RespawnTick = long.MinValue;
        internal override bool KeepsStealth => true;

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
            Bot = default;
            RespawnTick = long.MinValue;
        }

        public override void Update(DeviceContext context, ShipEntity ship) => Update(context, ship, IsControlOn(ship));
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship) => Update(context, ship, false);

        private void Update(DeviceContext context, ShipEntity ship, bool held)
        {
            var owner = context.Setup.IsLocalOwner(ship.Owner);
            var ready = context.Tick >= RespawnTick && _use.IsCooledDown(Definition.Activation, context.Tick);
            var paid = held && ready && ship.Energy.TryConsume(Definition.EnergyPerSecond * Battle.TickDuration);
            IsActive = paid;
            if (!owner) return;
            if (paid != _use.IsRunning)
            {
                if (paid) _use.Start(Definition.Activation, context.Tick);
                else _use.End(Definition.Activation, context.Tick);
                context.CaptureDeviceUse(ship, Index, paid, context.Tick);
            }
            if (_use.IsRunning && Bot.IsNull) context.RepairBots.Launch(ship, this);
        }

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (started) _use.Start(Definition.Activation, tick);
            else _use.End(Definition.Activation, tick);
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(tick + 1 >= RespawnTick && _use.IsCooledDown(Definition.Activation, tick + 1),
                IsActive, tick + 1 < RespawnTick ? 1f : _use.Cooldown(Definition.Activation, tick + 1));

        private DeviceActivation _use;
    }
}

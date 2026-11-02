using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // A weapon mount on the ship (legacy weapon platform). Weapons on one barrel share its cooldown.
    public sealed class BarrelDefinition
    {
        public BarrelDefinition(Vec2 position, float angle = 0f)
        {
            if (!position.IsFinite)
                throw new ArgumentOutOfRangeException(nameof(position), "Barrel position must be finite.");
            if (float.IsNaN(angle) || float.IsInfinity(angle))
                throw new ArgumentOutOfRangeException(nameof(angle), "Barrel angle must be finite.");

            Position = position;
            Angle = angle;
        }

        // The muzzle point in the ship frame, relative to the body origin. It includes the legacy platform offset.
        public Vec2 Position { get; }

        // In radians, relative to the ship heading.
        public float Angle { get; }
    }

    // Immutable parameters of one ship device. Phase 2 has explicit code per device; the data form waits for the
    // data-model gate (A4). The device index in ShipDefinition.Devices is also its control bit.
    public abstract class DeviceDefinition
    {
        private protected DeviceDefinition() { }

        internal abstract void Validate(int barrelCount);
        internal abstract ShipDevice CreateDevice(int index);
    }

    // Legacy CommonCannon (Q1): fires while the control bit is held, every cooldown, while energy allows.
    public sealed class CannonDefinition : DeviceDefinition
    {
        public CannonDefinition(int barrelIndex, float cooldown, float energyCost, float spread, BulletDefinition bullet)
        {
            if (barrelIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(barrelIndex), "Barrel index must not be negative.");
            if (!(cooldown >= 0f) || float.IsInfinity(cooldown))
                throw new ArgumentOutOfRangeException(nameof(cooldown), "Cooldown must be finite and not negative.");
            if (!(energyCost >= 0f) || float.IsInfinity(energyCost))
                throw new ArgumentOutOfRangeException(nameof(energyCost), "Energy cost must be finite and not negative.");
            if (!(spread >= 0f) || float.IsInfinity(spread))
                throw new ArgumentOutOfRangeException(nameof(spread), "Spread must be finite and not negative.");

            BarrelIndex = barrelIndex;
            Cooldown = cooldown;
            EnergyCost = energyCost;
            Spread = spread;
            Bullet = bullet ?? throw new ArgumentNullException(nameof(bullet));
            CooldownTicks = TickTime.AtLeast(cooldown);
        }

        public int BarrelIndex { get; }

        // Seconds between shots: 1 / FireRate.
        public float Cooldown { get; }

        public float EnergyCost { get; }

        // Full spread angle in radians. A shot deviates by up to half of it either way.
        public float Spread { get; }

        public BulletDefinition Bullet { get; }

        // Legacy SystemBase.CanBeActivated: TimeFromLastUse >= MaxCooldown.
        public int CooldownTicks { get; }

        internal override void Validate(int barrelCount)
        {
            if (BarrelIndex >= barrelCount)
                throw new ArgumentOutOfRangeException(nameof(BarrelIndex), $"Cannon barrel {BarrelIndex} does not exist.");
        }

        internal override ShipDevice CreateDevice(int index) => new CannonDevice(this, index);
    }

    // A device the core cannot run yet (phase 4, D1). It keeps its index and control bit and does nothing: it spends
    // no energy and spawns nothing. Its weight and passive energy use are already in the ship stats.
    public sealed class InertDeviceDefinition : DeviceDefinition
    {
        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new InertDevice(index);
    }
}

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

    // Immutable parameters of one ship device. Each device class is explicit code; from phase 5, step 7 (H2) the gate
    // devices compose the shared ActivationPolicy, ShipStatus and lethal damage condition. The device index in
    // ShipDefinition.Devices is also its control bit.
    public abstract class DeviceDefinition
    {
        private protected DeviceDefinition() { }

        // The ship this device launches: a drone, or a clone (phase 5, step 7c); null for other devices. A launched
        // ship launches none.
        public virtual ShipDefinition ChildShip => null;

        internal abstract void Validate(int barrelCount);
        internal abstract ShipDevice CreateDevice(int index);
    }

    // Legacy weapon classes that fire projectiles from a held key (phase 5, step 4a).
    public enum CannonMode : byte
    {
        // Legacy CommonCannon: one bullet per shot.
        Single,

        // Legacy MultishotCannon: Magazine bullets in one shot, each with its own spread, for Magazine x EnergyCost.
        MultiShot,

        // Legacy MachineGun: Magazine shots at the barrel cooldown, then the weapon cooldown. A released key keeps the
        // count of the magazine.
        MachineGun,

        // Legacy ManageableCannon (phase 5, step 4b): one bullet at a time. Releasing the key detonates it; the barrel
        // and the weapon cooldown start in the tick after its loss.
        Manageable,

        // Legacy ChargeableCannon (phase 5, step 4f): the held key charges the weapon; the release fires one bullet with
        // the charge (WeaponCharge). Built with CannonDefinition.Charged.
        Charging,

        // Legacy ContinuousCannon with a bullet that is not a beam (phase 5, step 4g): one bullet at a time. The held key
        // renews its lifetime for EnergyCost per second; the shot costs EnergyCost (legacy ActivationCost of a bullet
        // without a continuous controller).
        Continuous,
    }

    // Legacy CommonCannon (Q1): fires while the control bit is held, every cooldown, while energy allows. Phase 5, step
    // 4a adds the multi-shot and machine gun modes; step 4b the manageable mode; step 4f the charging mode; step 4g the
    // continuous mode.
    public sealed class CannonDefinition : DeviceDefinition
    {
        public CannonDefinition(int barrelIndex, float cooldown, float energyCost, float spread, BulletDefinition bullet,
            CannonMode mode = CannonMode.Single, int magazine = 1)
            : this(barrelIndex, cooldown, energyCost, spread, bullet, mode, magazine, 0f)
        {
            if (mode == CannonMode.Charging)
                throw new ArgumentOutOfRangeException(nameof(mode), "A charging cannon is built with Charged.");
        }

        private CannonDefinition(int barrelIndex, float cooldown, float energyCost, float spread, BulletDefinition bullet,
            CannonMode mode, int magazine, float chargeTime)
        {
            if (barrelIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(barrelIndex), "Barrel index must not be negative.");
            if (!(cooldown >= 0f) || float.IsInfinity(cooldown))
                throw new ArgumentOutOfRangeException(nameof(cooldown), "Cooldown must be finite and not negative.");
            if (!(energyCost >= 0f) || float.IsInfinity(energyCost))
                throw new ArgumentOutOfRangeException(nameof(energyCost), "Energy cost must be finite and not negative.");
            if (!(spread >= 0f) || float.IsInfinity(spread))
                throw new ArgumentOutOfRangeException(nameof(spread), "Spread must be finite and not negative.");
            if (mode < CannonMode.Single || mode > CannonMode.Continuous)
                throw new ArgumentOutOfRangeException(nameof(mode), "Unknown cannon mode.");
            if (HasMagazine(mode) ? magazine < 1 : magazine != 1)
                throw new ArgumentOutOfRangeException(nameof(magazine),
                    "A multi-shot or machine gun needs a magazine of at least 1; other cannons have 1.");

            BarrelIndex = barrelIndex;
            Cooldown = cooldown;
            EnergyCost = energyCost;
            Spread = spread;
            Bullet = bullet ?? throw new ArgumentNullException(nameof(bullet));
            Mode = mode;
            Magazine = magazine;
            CooldownTicks = TickTime.AtLeast(cooldown);
            ChargeTime = chargeTime;
        }

        // Legacy ChargeableCannon: a full charge takes chargeTime seconds (1 / FireRate) and energyCost energy. It has
        // no weapon cooldown.
        public static CannonDefinition Charged(int barrelIndex, float chargeTime, float energyCost, float spread,
            BulletDefinition bullet)
        {
            if (!(chargeTime > 0f) || float.IsInfinity(chargeTime))
                throw new ArgumentOutOfRangeException(nameof(chargeTime), "Charge time must be finite and positive.");
            return new CannonDefinition(barrelIndex, 0f, energyCost, spread, bullet, CannonMode.Charging, 1, chargeTime);
        }

        public int BarrelIndex { get; }

        // Seconds between shots: 1 / FireRate.
        public float Cooldown { get; }

        public float EnergyCost { get; }

        // Full spread angle in radians. A shot deviates by up to half of it either way.
        public float Spread { get; }

        public BulletDefinition Bullet { get; }

        public CannonMode Mode { get; }

        // Bullets per shot (multi-shot) or shots per burst (machine gun); 1 for other cannons.
        public int Magazine { get; }

        // A charging cannon: the seconds of a full charge; 0 otherwise. EnergyCost is the energy of a full charge.
        public float ChargeTime { get; }

        // Legacy SystemBase.CanBeActivated: TimeFromLastUse >= MaxCooldown. A machine gun waits it after each burst.
        public int CooldownTicks { get; }

        // Legacy WeaponFactory sets BulletStats.RandomFactor for these classes: each bullet's speed and lifetime vary by
        // (r - 0.5) x factor.
        public float RandomFactor => Mode == CannonMode.MachineGun ? 0.2f : Mode == CannonMode.MultiShot ? 0.3f : 0f;

        // The energy of one shot: a multi-shot pays for its whole volley at once.
        public float ShotEnergyCost => Mode == CannonMode.MultiShot ? EnergyCost * Magazine : EnergyCost;

        private static bool HasMagazine(CannonMode mode) => mode == CannonMode.MultiShot || mode == CannonMode.MachineGun;

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

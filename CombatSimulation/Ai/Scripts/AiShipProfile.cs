using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    // Legacy Combat.Component.Systems.Weapons.WeaponType: the weapon class the AI reads.
    public enum AiWeaponClass
    {
        Common,
        Manageable,
        Continuous,
        RequiredCharging,
    }

    // Legacy WeaponCapability, from the ammunition's impact effects.
    [Flags]
    public enum AiWeaponCapability
    {
        None = 0,
        DamageEnemy = 1,
        RepairAlly = 2,
        CaptureDrone = 4,
        RechargeAlly = 8,
    }

    // What the AI knows about one working weapon (legacy WeaponInfo). The mapper fills it from the database; the core
    // never reads it (AI data, gap 2).
    public sealed class AiWeapon
    {
        public AiWeapon(int deviceIndex, AiWeaponClass weaponClass, AiBulletBehavior bulletBehavior,
            AiWeaponCapability capability, float range, float recoil, float spread = 0f)
        {
            DeviceIndex = deviceIndex;
            WeaponClass = weaponClass;
            BulletBehavior = bulletBehavior;
            Capability = capability;
            Range = range;
            Recoil = recoil;
            Spread = spread;
        }

        public int DeviceIndex { get; }
        public AiWeaponClass WeaponClass { get; }
        public AiBulletBehavior BulletBehavior { get; }
        public AiWeaponCapability Capability { get; }

        // Legacy BulletStats.BulletHitRange.
        public float Range { get; }

        // Legacy WeaponInfo.Recoil: ammunition recoil ÷ ship weight for common weapons, else 0.
        public float Recoil { get; }

        // Legacy WeaponInfo.Spread in radians: the full spread angle.
        public float Spread { get; }
    }

    // A working device the AI can press (legacy IDevice in ShipSystems): its control bit, class and whether its key
    // binding makes it automatic (legacy ISystem.IsAutomatic, key below 0).
    public sealed class AiDevice
    {
        public AiDevice(int deviceIndex, DeviceClass deviceClass, bool isAutomatic, float range = 0f,
            float activationTime = 0f)
        {
            DeviceIndex = deviceIndex;
            DeviceClass = deviceClass;
            IsAutomatic = isAutomatic;
            Range = range;
            ActivationTime = activationTime;
        }

        public int DeviceIndex { get; }
        public DeviceClass DeviceClass { get; }
        public bool IsAutomatic { get; }

        // Legacy WarpDrive.MaxRange; 0 for other classes.
        public float Range { get; }

        // Legacy DetonatorDevice.ActivationTime (its lock, in seconds); 0 for other classes.
        public float ActivationTime { get; }
    }

    // A drone bay (legacy IDroneBay) or a clonning center (phase 5, step 7c): its control bit and whether its key binding
    // makes it automatic.
    public sealed class AiDroneBay
    {
        public AiDroneBay(int deviceIndex, bool isAutomatic)
        {
            DeviceIndex = deviceIndex;
            IsAutomatic = isAutomatic;
        }

        public int DeviceIndex { get; }
        public bool IsAutomatic { get; }
    }

    // The ship facts of the AI side table (AI data, gaps 2 and 3). Step 6 holds what the requirement check reads; step 7b
    // adds what other ships' nodes read about this ship as a target. Inert devices are absent (D6).
    public sealed class AiShipProfile
    {
        public AiShipProfile(IReadOnlyList<AiWeapon> weapons, IReadOnlyList<AiDevice> devices,
            IReadOnlyList<AiDroneBay> droneBays, bool isDrone, bool isStarbase, SizeClass sizeClass, float enginePower, float turnRate, float propulsion,
            float kineticResistance, float rammingDamageMultiplier, float modelScale,
            float chanceToAvoidDrone)
        {
            Weapons = weapons ?? Array.Empty<AiWeapon>();
            Devices = devices ?? Array.Empty<AiDevice>();
            DroneBays = droneBays ?? Array.Empty<AiDroneBay>();
            IsDrone = isDrone;
            IsStarbase = isStarbase;
            SizeClass = sizeClass;
            EnginePower = enginePower;
            TurnRate = turnRate;
            Propulsion = propulsion;
            KineticResistance = kineticResistance;
            RammingDamageMultiplier = rammingDamageMultiplier;
            ModelScale = modelScale;
            ChanceToAvoidDrone = chanceToAvoidDrone;

            foreach (var weapon in Weapons)
            {
                if (weapon.Range > AttackRangeMax) AttackRangeMax = weapon.Range;
                if (weapon.Capability != AiWeaponCapability.None && weapon.Range > CapableAttackRange)
                    CapableAttackRange = weapon.Range;
            }
        }

        public IReadOnlyList<AiWeapon> Weapons { get; }

        // Legacy Context.AttackRangeMax: the longest range among all weapons.
        public float AttackRangeMax { get; }

        // Legacy CalculateAttackRange: the longest range among weapons with a capability. Other ships read it.
        public float CapableAttackRange { get; }

        // The working devices, in device order.
        public IReadOnlyList<AiDevice> Devices { get; }

        // The drone bays and clonning centers, in device order (legacy HasDrones counts both). Drones have none.
        public IReadOnlyList<AiDroneBay> DroneBays { get; }

        public bool HasDroneBays => DroneBays.Count > 0;
        public bool IsDrone { get; }
        public bool IsStarbase { get; }
        public SizeClass SizeClass { get; }

        // Legacy IShipStats values: engine power and turn rate in legacy units.
        public float EnginePower { get; }
        public float TurnRate { get; }

        // Legacy IEngine.Propulsion at battle start.
        public float Propulsion { get; }

        // Legacy KineticResistancePercentage, a fraction.
        public float KineticResistance { get; }

        public float RammingDamageMultiplier { get; }

        // Legacy Body.Scale: the enemy search measures distance and size with it, not with the hull radius.
        public float ModelScale { get; }

        // Legacy Features.ChanceToAvoidDrone, a fraction: the drone camouflage Power (phase 5, step 9a).
        public float ChanceToAvoidDrone { get; }

        // Legacy RequirementChecker HasEngine and NodeBuilder.CanMove.
        public bool HasEngine => !IsStarbase && EnginePower > MinEnginePower && TurnRate > MinEnginePower;

        // The target facts of every decoy (phase 5, step 10c): legacy Decoy has no stats, engine or weapons, and its
        // Body.Scale is 0.5.
        public static readonly AiShipProfile Decoy = new AiShipProfile(null, null, null, false, false, default, 0f, 0f, 0f,
            0f, 0f, DecoyDefinition.Size, 0f);

        private const float MinEnginePower = 0.1f;
    }

    // Legacy Combat.Ai.BehaviorTree.AiSettings.
    public readonly struct AiSettings
    {
        public readonly float LimitedRange;
        public readonly AiDifficultyLevel AiLevel;

        public static AiSettings ForDrone(float droneRange) => new AiSettings(AiDifficultyLevel.Hard, droneRange);
        public static AiSettings ForMercenary(float range, int aiLevel) => new AiSettings(AiLevelFromValue(aiLevel), range);
        public static AiSettings FromAiLevel(int aiLevel) => new AiSettings(AiLevelFromValue(aiLevel));
        public static AiSettings FromAiLevel(AiDifficultyLevel aiLevel) => new AiSettings(aiLevel);
        public static AiSettings Default => new AiSettings(AiDifficultyLevel.Hard);

        private AiSettings(AiDifficultyLevel aiLevel, float limitedRange = 0f)
        {
            AiLevel = aiLevel;
            LimitedRange = limitedRange;
        }

        private static AiDifficultyLevel AiLevelFromValue(int value)
        {
            if (value < 50) return AiDifficultyLevel.Easy;
            if (value < 80) return AiDifficultyLevel.Medium;
            return AiDifficultyLevel.Hard;
        }
    }
}

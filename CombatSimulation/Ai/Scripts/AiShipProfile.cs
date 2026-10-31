using System;
using System.Collections.Generic;
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
            AiWeaponCapability capability, float range, float recoil)
        {
            DeviceIndex = deviceIndex;
            WeaponClass = weaponClass;
            BulletBehavior = bulletBehavior;
            Capability = capability;
            Range = range;
            Recoil = recoil;
        }

        public int DeviceIndex { get; }
        public AiWeaponClass WeaponClass { get; }
        public AiBulletBehavior BulletBehavior { get; }
        public AiWeaponCapability Capability { get; }

        // Legacy BulletStats.BulletHitRange.
        public float Range { get; }

        // Legacy WeaponInfo.Recoil: ammunition recoil ÷ ship weight for common weapons, else 0.
        public float Recoil { get; }
    }

    // The ship facts of the AI side table (AI data, gaps 2 and 3). Step 6 holds what the requirement check reads; step 7
    // adds what the action nodes read. Inert devices are absent (D6).
    public sealed class AiShipProfile
    {
        public AiShipProfile(IReadOnlyList<AiWeapon> weapons, IReadOnlyList<DeviceClass> devices, bool hasDroneBays,
            bool isDrone, bool isStarbase, SizeClass sizeClass, float enginePower, float turnRate, float propulsion,
            float kineticResistance, float rammingDamageMultiplier)
        {
            Weapons = weapons ?? Array.Empty<AiWeapon>();
            Devices = devices ?? Array.Empty<DeviceClass>();
            HasDroneBays = hasDroneBays;
            IsDrone = isDrone;
            IsStarbase = isStarbase;
            SizeClass = sizeClass;
            EnginePower = enginePower;
            TurnRate = turnRate;
            Propulsion = propulsion;
            KineticResistance = kineticResistance;
            RammingDamageMultiplier = rammingDamageMultiplier;
        }

        public IReadOnlyList<AiWeapon> Weapons { get; }

        // The classes of the working devices, in device order.
        public IReadOnlyList<DeviceClass> Devices { get; }

        public bool HasDroneBays { get; }
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

        // Legacy RequirementChecker HasEngine and NodeBuilder.CanMove.
        public bool HasEngine => !IsStarbase && EnginePower > MinEnginePower && TurnRate > MinEnginePower;

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

using GameDatabase.DataModel;

namespace CombatSimulation.Ai
{
    // Legacy Combat.Ai.BehaviorTree.RequirementChecker on the AI side table. The builder drops a node whose requirement
    // fails, once per ship.
    internal sealed class RequirementChecker : IBehaviorNodeRequirementFactory<bool>
    {
        // Legacy "weapon.Info.Recoil > 0.1f".
        private const float HighRecoil = 0.1f;

        public RequirementChecker(AiShipProfile profile, AiSettings settings)
        {
            _profile = profile;
            _settings = settings;
        }

        public bool Create(BehaviorNodeRequirement_Empty content) => true;

        public bool Create(BehaviorNodeRequirement_Any content)
        {
            foreach (var child in content.Requirements)
                if (child.Create(this)) return true;

            return false;
        }

        public bool Create(BehaviorNodeRequirement_All content)
        {
            foreach (var child in content.Requirements)
                if (!child.Create(this)) return false;

            return true;
        }

        public bool Create(BehaviorNodeRequirement_None content)
        {
            foreach (var child in content.Requirements)
                if (child.Create(this)) return false;

            return true;
        }

        public bool Create(BehaviorNodeRequirement_HasDevice content)
        {
            foreach (var deviceClass in _profile.Devices)
                if (deviceClass == content.DeviceClass) return true;

            return false;
        }

        // Legacy also counts clonning centers; they are inert in phase 4 (D6).
        public bool Create(BehaviorNodeRequirement_HasDrones content) => _profile.HasDroneBays;

        public bool Create(BehaviorNodeRequirement_HasHighRecoilWeapon content)
        {
            foreach (var weapon in _profile.Weapons)
                if (weapon.Recoil > HighRecoil) return true;

            return false;
        }

        public bool Create(BehaviorNodeRequirement_HasLongRangeWeapon content) => _profile.CapableAttackRange >= content.Range;

        public bool Create(BehaviorNodeRequirement_HasEngine content) => _profile.HasEngine;
        public bool Create(BehaviorNodeRequirement_HasAnyWeapon content) => _profile.Weapons.Count > 0;
        public bool Create(BehaviorNodeRequirement_AiLevel content) => content.DifficultyLevel == _settings.AiLevel;
        public bool Create(BehaviorNodeRequirement_MinAiLevel content) => content.DifficultyLevel <= _settings.AiLevel;
        public bool Create(BehaviorNodeRequirement_IsDrone content) => _profile.IsDrone;
        public bool Create(BehaviorNodeRequirement_CanRepairAllies content) => HasCapability(AiWeaponCapability.RepairAlly);
        public bool Create(BehaviorNodeRequirement_CanRechargeAllies content) => HasCapability(AiWeaponCapability.RechargeAlly);
        public bool Create(BehaviorNodeRequirement_HasHarpoon content) => HasBulletBehavior(GameDatabase.Enums.AiBulletBehavior.Harpoon);
        public bool Create(BehaviorNodeRequirement_HasChargeableWeapon content) => HasWeaponClass(AiWeaponClass.RequiredCharging);
        public bool Create(BehaviorNodeRequirement_HasRemotelyControlledWeapon content) => HasWeaponClass(AiWeaponClass.Manageable);
        public bool Create(BehaviorNodeRequirement_SizeClass content) => _profile.SizeClass == content.SizeClass;
        public bool Create(BehaviorNodeRequirement_HasKineticResistance content) => _profile.KineticResistance >= content.Value;
        public bool Create(BehaviorNodeRequirement_HasHighRammingDamage content) => _profile.RammingDamageMultiplier >= content.Value;
        public bool Create(BehaviorNodeRequirement_HasHighManeuverability content) => _profile.Propulsion > content.Value;

        private bool HasCapability(AiWeaponCapability capability)
        {
            foreach (var weapon in _profile.Weapons)
                if ((weapon.Capability & capability) == capability) return true;

            return false;
        }

        private bool HasWeaponClass(AiWeaponClass weaponClass)
        {
            foreach (var weapon in _profile.Weapons)
                if (weapon.WeaponClass == weaponClass) return true;

            return false;
        }

        private bool HasBulletBehavior(GameDatabase.Enums.AiBulletBehavior behavior)
        {
            foreach (var weapon in _profile.Weapons)
                if (weapon.BulletBehavior == behavior) return true;

            return false;
        }

        private readonly AiShipProfile _profile;
        private readonly AiSettings _settings;
    }
}

namespace CombatSimulation.Core
{
    // A ship resource: legacy Energy (IResourcePoints.cs), stored as an absolute value.
    // Armor uses rate 0, so it never regenerates and a spend never starts a delay.
    internal struct ResourcePool
    {
        public float Value;
        public float Max;
        public float RechargeRate;
        public float RechargeDelay;

        // Seconds left before the recharge resumes.
        public float DelayRemaining;

        // Starts full, with no delay.
        public static ResourcePool Create(in ResourceDefinition definition) => new ResourcePool
        {
            Value = definition.Max,
            Max = definition.Max,
            RechargeRate = definition.RechargeRate,
            RechargeDelay = definition.RechargeDelay,
        };

        public static ResourcePool CreateArmor(float max) => new ResourcePool { Value = max, Max = max };

        public float Deficit => Max - Value;

        // Only the part of the step after the delay ends recharges.
        public void Update(float dt)
        {
            if (DelayRemaining > dt)
            {
                DelayRemaining -= dt;
                return;
            }

            if (Max > 0f) Value = Clamp(Value + (dt - DelayRemaining) * RechargeRate);
            DelayRemaining = 0f;
        }

        // True when TryConsume would spend the amount.
        public readonly bool CanConsume(float amount) => Value > 0f && Value >= amount && Max > 0f;

        // Spends the amount only if it is available. A failed attempt does not touch the delay.
        public bool TryConsume(float amount)
        {
            if (!CanConsume(amount)) return false;

            Value -= amount;
            RestartDelay(amount);
            return true;
        }

        // Spends up to the amount and returns what was removed. A negative amount adds.
        public float Consume(float amount)
        {
            RestartDelay(amount);
            if (!(Max > 0f)) return 0f;

            var before = Value;
            Value = Clamp(Value - amount);
            return before - Value;
        }

        private void RestartDelay(float amount)
        {
            if (amount > 0f && RechargeRate > 0f) DelayRemaining = RechargeDelay;
        }

        private float Clamp(float value) => value < 0f ? 0f : value > Max ? Max : value;
    }
}

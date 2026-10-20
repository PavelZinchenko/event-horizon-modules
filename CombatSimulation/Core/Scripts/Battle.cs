using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public sealed class Battle
    {
        public const float TickDuration = 0.02f;
        public const int PhysicsSubsteps = 8;

        public Battle(BattleSetup setup)
        {
            Setup = setup ?? throw new ArgumentNullException(nameof(setup));
            World = new PhysicsWorld(new WorldSettings
            {
                Width = setup.Width,
                Height = setup.Height,
                Substeps = PhysicsSubsteps,
            });
        }

        public BattleSetup Setup { get; }
        public BattleId BattleId => Setup.BattleId;

        // The next tick to execute. The first Step processes tick 0, then leaves Tick at 1.
        public long Tick { get; private set; }

        // Physics handles and mutable state never leave Core's trusted assembly boundary.
        internal PhysicsWorld World { get; }

        public void Step()
        {
            if (Tick == long.MaxValue)
                throw new InvalidOperationException("Battle tick counter exhausted.");

            ApplyCommands();
            UpdateSystems();
            EvaluateModifiers();
            UpdateMotion();
            ApplySpawnRequests();
            RunQueries();
            StepPhysics();
            CollectContacts();
            ResolveContacts();
            ApplyDamage();
            MarkDeaths();
            ApplyStructureChanges();
            UpdateBattleFlow();
            Publish();
        }

        // These stage boundaries reserve the order for the remaining phase 2 work.
        private void ApplyCommands() { }
        private void UpdateSystems() { }
        private void EvaluateModifiers() { }
        private void UpdateMotion() { }
        private void ApplySpawnRequests() { }
        private void RunQueries() { }
        private void StepPhysics() => World.Step(TickDuration);
        private void CollectContacts() { }
        private void ResolveContacts() { }
        private void ApplyDamage() { }
        private void MarkDeaths() { }
        private void ApplyStructureChanges() { }
        private void UpdateBattleFlow() { }

        private void Publish()
        {
            // Later, publish the snapshot and events for Tick before advancing it.
            ++Tick;
        }
    }
}

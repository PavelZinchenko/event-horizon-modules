using System;
using System.Collections.Generic;
using CombatSimulation.Core;

namespace CombatSimulation.Modes
{
    // The game's ship content for the exploration spawners.
    public interface IExplorationContent
    {
        // Pure: the same arguments always give the same definition. Called in BeginTick on the battle thread, when a
        // spawner first activates and for each dockable ship at the start. The game caches by build and satellites.
        // ordinal: a guardian's number in its group (0 to n - 1); 0 for the other roles. A respawn keeps the unit.
        ShipDefinition CreateShip(ExplorationShipRole role, long contentKey, int ordinal);
    }

    // The rules of an exploration level (new combat simulator, phase 3, step 5; TODO/exploration_mode.md). The setup has
    // the player's unit (side 0) and the layout's zones and asteroids; the mode adds every other unit.
    public sealed class ExplorationRules : IBattleRules
    {
        public const float DefaultActivationDistance = 75f;
        public const float DefaultVanishDistance = 100f;

        // exploration_objects.md: the legacy merchant docks below a gap of 3 and undocks above 3.6.
        public const float DefaultDockingReleaseMargin = 0.6f;

        // completedObjectives: Main's completion state; the mode skips these IDs and ignores the unknown ones.
        public ExplorationRules(
            ExplorationLayout layout,
            IExplorationContent content,
            UnitId player,
            in LivesSetup playerLives,
            IReadOnlyCollection<int> completedObjectives = null,
            float activationDistance = DefaultActivationDistance,
            float vanishDistance = DefaultVanishDistance,
            float dockingReleaseMargin = DefaultDockingReleaseMargin)
        {
            if (!(activationDistance > 0f)) throw new ArgumentOutOfRangeException(nameof(activationDistance));
            if (!(vanishDistance > activationDistance))
                throw new ArgumentOutOfRangeException(nameof(vanishDistance), "The vanish distance must exceed the activation.");
            if (!(dockingReleaseMargin >= 0f)) throw new ArgumentOutOfRangeException(nameof(dockingReleaseMargin));

            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Player = player;
            PlayerLives = playerLives;
            var completed = new bool[layout.Objectives.Count];
            if (completedObjectives != null)
                foreach (var id in completedObjectives)
                    if (id >= 0 && id < completed.Length) completed[id] = true;
            CompletedObjectives = Array.AsReadOnly(completed);
            ActivationDistance = activationDistance;
            VanishDistance = vanishDistance;
            DockingReleaseMargin = dockingReleaseMargin;
        }

        public ExplorationLayout Layout { get; }
        public IExplorationContent Content { get; }
        public UnitId Player { get; }

        // Frontier: 3 lives (100 with UnlimitedRespawn), 250 ticks, at the last pose, asking unless lives cannot be
        // bought. Main: 0 lives, 250 ticks, no question (player_lives.md).
        public LivesSetup PlayerLives { get; }

        // By objective ID.
        public IReadOnlyList<bool> CompletedObjectives { get; }

        // A spawner's unit deploys when the player is this close to the spawner's position.
        public float ActivationDistance { get; }

        // An active ship vanishes when it is farther than this from the player.
        public float VanishDistance { get; }
        public float DockingReleaseMargin { get; }

        // A local square world of the layout's size, with the player's unit on side 0 and no unit in the spawners' range.
        public void Validate(BattleSetup setup)
        {
            if (setup.HasRemoteOwners) throw new ArgumentException("Exploration is local (D10).", nameof(setup));
            if (setup.Width != Layout.WorldSize || setup.Height != Layout.WorldSize)
                throw new ArgumentException("The world must have the layout's size.", nameof(setup));

            var found = false;
            foreach (var unit in setup.Units)
            {
                if (Layout.TryFindSpawner(unit.UnitId, out _))
                    throw new ArgumentException($"The unit {unit.UnitId} is in the spawners' ID range.", nameof(setup));
                if (unit.UnitId != Player) continue;
                if (unit.Side != 0) throw new ArgumentException("The player's unit must be on side 0.", nameof(setup));
                found = true;
            }
            if (!found) throw new ArgumentException($"The setup has no player unit {Player}.", nameof(setup));
        }

        public IBattleMode CreateMode(BattleSetup setup) => new ExplorationMode(setup, this);
    }
}

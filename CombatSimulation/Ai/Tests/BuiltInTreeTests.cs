using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using CombatSimulation.Core;
using CombatSimulation.Runtime;
using GameDatabase.DataModel;
using GameDatabase.Enums;
using GameDatabase.Storage;
using NUnit.Framework;

namespace CombatSimulation.Ai.Tests
{
    // Every built-in tree builds and runs on a fixture battle without exceptions (phase 4, step 6, check 2) and reports
    // the stubs it reaches (check 3).
    public class BuiltInTreeTests
    {
        private const int BuiltInTreeCount = 74;
        private const int Ticks = 600;

        private static readonly (string Name, AiShipProfile Profile, AiSettings Settings)[] Profiles =
        {
            ("easy", AiFixtures.ArmedShip, AiSettings.FromAiLevel(AiDifficultyLevel.Easy)),
            ("medium", AiFixtures.ArmedShip, AiSettings.FromAiLevel(AiDifficultyLevel.Medium)),
            ("hard", AiFixtures.ArmedShip, AiSettings.FromAiLevel(AiDifficultyLevel.Hard)),
            ("drone", AiFixtures.Drone, AiSettings.ForDrone(30f)),
            ("starbase", AiFixtures.Starbase, AiSettings.Default),
            ("unarmed", AiFixtures.Unarmed, AiSettings.Default),
        };

        [Test]
        public void EveryBuiltInTreeRunsOnAFixtureBattle()
        {
            var database = LoadBuiltInDatabase();
            var trees = database.BehaviorTreeList.OrderBy(tree => tree.Id.Value).ToList();
            Assert.AreEqual(BuiltInTreeCount, trees.Count);

            var report = new StringBuilder();
            foreach (var tree in trees)
            {
                var stubs = new SortedDictionary<string, int>();
                foreach (var (name, profile, settings) in Profiles)
                {
                    var provider = new TreePilots(tree, profile, settings);
                    var runner = AiFixtures.Battle(provider);
                    for (var i = 0; i < Ticks && !runner.IsCompleted; ++i)
                        runner.Step();

                    Assert.AreEqual(2, provider.Pilots.Count, $"tree {tree.Id.Value}, {name}");
                    foreach (var pilot in provider.Pilots)
                        foreach (var stub in pilot.Stubs)
                            stubs[stub.Key.ToString()] = stubs.TryGetValue(stub.Key.ToString(), out var count) ? count + stub.Value : stub.Value;
                }

                report.AppendLine($"tree {tree.Id.Value}: " +
                    (stubs.Count == 0 ? "no stubs" : string.Join(", ", stubs.Keys)));
            }

            TestContext.WriteLine(report.ToString());
        }

        // Selection follows legacy ControllerFactory (T6).
        [Test]
        public void TreeSelectionFollowsControllerFactory()
        {
            var settings = LoadBuiltInDatabase().CombatSettings;
            var custom = settings.CloneAI;

            Assert.IsNotNull(settings.EnemyAI);
            Assert.AreSame(settings.EnemyAI, BehaviorTreeSelection.ForEnemy(settings, null));
            Assert.AreSame(custom, BehaviorTreeSelection.ForEnemy(settings, custom));
            Assert.AreSame(settings.StarbaseAI, BehaviorTreeSelection.ForStarbase(settings, null));
            Assert.AreSame(settings.CloneAI, BehaviorTreeSelection.ForClone(settings, null));
            Assert.AreSame(settings.AutopilotAI, BehaviorTreeSelection.ForAutopilot(settings));

            var bay = new DroneBayStats();
            Assert.AreSame(settings.DefensiveDroneAI, BehaviorTreeSelection.ForDrone(settings, bay, null, defensive: true));
            Assert.AreSame(settings.OffensiveDroneAI, BehaviorTreeSelection.ForDrone(settings, bay, null, defensive: false));
            Assert.AreSame(custom, BehaviorTreeSelection.ForDrone(settings, bay, custom, defensive: true));

            bay.OffensiveDroneAI = settings.EnemyAI;
            Assert.AreSame(settings.EnemyAI, BehaviorTreeSelection.ForDrone(settings, bay, custom, defensive: false));
            Assert.AreSame(custom, BehaviorTreeSelection.ForDrone(settings, bay, custom, defensive: true));

            bay.ImprovedAi = true;
            Assert.IsNull(BehaviorTreeSelection.ForDrone(settings, bay, null, defensive: true));
        }

        private static GameDatabase.Database LoadBuiltInDatabase()
        {
            var database = new GameDatabase.Database(new FolderDatabaseStorage(DatabasePath()), new NewtonJsonSerializer(), string.Empty);
            database.LoadDefault();
            return database;
        }

        private static string DatabasePath([CallerFilePath] string path = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), "..", "..", "..", "..", "Modules", "Database", "Resources", "Database"));

        // Gives every ship a pilot of the same tree.
        private sealed class TreePilots : IPilotProvider
        {
            public TreePilots(BehaviorTreeModel tree, AiShipProfile profile, AiSettings settings)
            {
                _tree = tree;
                _profile = profile;
                _settings = settings;
            }

            public List<BehaviorTreePilot> Pilots { get; } = new List<BehaviorTreePilot>();

            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = _profile.IsDrone ? PilotSchedule.Drone : PilotSchedule.Ship;
                var pilot = _factory.Create(_tree, _profile, _settings, ship.Id, schedule);
                Pilots.Add(pilot);
                return pilot;
            }

            private readonly BehaviorTreeModel _tree;
            private readonly AiShipProfile _profile;
            private readonly AiSettings _settings;
            private readonly BehaviorTreeFactory _factory = new BehaviorTreeFactory(7);
        }
    }
}

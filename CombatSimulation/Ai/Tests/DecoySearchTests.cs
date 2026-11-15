using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Core;
using CombatSimulation.Runtime;
using NUnit.Framework;
using static CombatSimulation.Ai.Tests.AiFixtures;

namespace CombatSimulation.Ai.Tests
{
    // Phase 5, step 10c: legacy ShipListExtensions.GetEnemy decoy rules and TargetListBase.GetTargets.
    public class DecoySearchTests
    {
        private static readonly ShipDefinition Jammer = new ShipDefinition(1f, 1f, Engine, 1000f,
            new ResourceDefinition(0f), new ResourceDefinition(100f),
            devices: new DeviceDefinition[] { new DecoyDefinition(10f, 20f, 4, 4f, 15f, 10f) });

        // Slot 1 searches; slot 101 on side 1 launches four decoys at its first tick.
        private static BattleRunner Start(out AiContext context)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Ship),
                new RosterSlotSetup(new RosterId(101), 1, Jammer),
            };
            var runner = new BattleRunner(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: 1),
                new DecoyPilots());
            foreach (var slot in roster)
                runner.Deploy(slot.RosterId, runner.Tick);
            runner.Step();
            runner.Step();
            runner.Step();
            Assert.AreEqual(4, runner.Snapshot.Ships.ToArray().Count(s => s.IsDecoy));
            context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, Utilites.RandomState.FromSeed(1), BattleData());
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            return runner;
        }

        private static EntityId Jammers(BattleRunner runner) =>
            runner.Snapshot.Ships.ToArray().Single(s => s.Definition == Jammer).Id;

        [Test]
        public void AShipSearchSkipsDecoys()
        {
            var runner = Start(out var context);
            Assert.AreEqual(Jammers(runner), EnemySearch.Find(context, default, EnemyOptions.ForShip(0f, false)));
        }

        [Test]
        public void ADroneSearchTakesADecoy()
        {
            var runner = Start(out var context);
            var found = EnemySearch.Find(context, default, EnemyOptions.ForDrone(0f, false));
            Assert.IsTrue(runner.Snapshot.TryGetShip(found, out var target));
            Assert.IsTrue(target.IsDecoy);
        }

        [Test]
        public void TheAdditionalTargetsSkipDecoysAndADecoyHasItsOwnProfile()
        {
            var runner = Start(out var context);
            var enemies = new List<EntityId>();
            TargetLists.UpdateEnemies(context, enemies, new List<float>());
            Assert.AreEqual(new[] { Jammers(runner) }, enemies.ToArray());
            var decoy = runner.Snapshot.Ships.ToArray().First(s => s.IsDecoy);
            Assert.AreSame(AiShipProfile.Decoy, context.ProfileOf(decoy));
            Assert.AreEqual(DecoyDefinition.Size, context.ProfileOf(decoy).ModelScale);
        }

        // No pilot reaches a decoy; the jammer holds its decoy key.
        private sealed class DecoyPilots : IPilotProvider
        {
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                Assert.IsFalse(ship.IsDecoy);
                return ship.Definition == Jammer ? new HoldPilot() : null;
            }
        }

        private sealed class HoldPilot : IPilot
        {
            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                controls = PilotControls.Create(null, 0f, 1UL);
                return true;
            }
        }
    }
}

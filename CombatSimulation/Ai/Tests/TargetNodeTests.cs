using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using GameDatabase.Enums;
using GameDatabase.Serializable;
using NUnit.Framework;
using static CombatSimulation.Ai.Tests.AiFixtures;

namespace CombatSimulation.Ai.Tests
{
    // Enemy search, target list, threat and target nodes against the legacy rules (phase 4, step 7c).
    public class TargetNodeTests
    {
        private const NodeState S = NodeState.Success;
        private const NodeState F = NodeState.Failure;

        [Test]
        public void AlliedTargetConditionsAreBuiltAndReadLiveArmor()
        {
            var runner = Battle2V2();
            var context = ContextOf(runner, 0);
            var builder = Builder(context);
            var ally = builder.Build(Node(Leaf(BehaviorNodeType.MainTargetIsAlly)));
            var low = builder.Build(Node(new BehaviorTreeNodeSerializable
                { Type = BehaviorNodeType.MainTargetLowHp, MinValue = 1f }));
            context.TargetShip = runner.Snapshot.Ships[2].Id;
            Assert.AreEqual(S, ally.Evaluate(context));
            Assert.AreEqual(F, low.Evaluate(context));
            context.TargetShip = runner.Snapshot.Ships[1].Id;
            Assert.AreEqual(F, ally.Evaluate(context));
            context.TargetShip = default;
            Assert.AreEqual(F, ally.Evaluate(context));
            Assert.AreEqual(F, low.Evaluate(context));
            Assert.IsEmpty(builder.Stubs);
        }

        [Test]
        public void FindDamagedAllyUsesTheApprovedThresholdAndExcludesSelfAndEnemies()
        {
            var roster = new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Ship),
                new RosterSlotSetup(new RosterId(2), 0, Ship),
                new RosterSlotSetup(new RosterId(101), 1, Ship),
            };
            var runner = new BattleRunner(new BattleSetup(new BattleId(55), 7, roster, timeLimitTicks: 1,
                maxActiveShipsPerSide: 2, timerMode: TimerMode.DrainPlayerHp), new NoPilots());
            foreach (var slot in roster) runner.Deploy(slot.RosterId, 0);
            runner.Step();
            var context = ContextOf(runner, 0, WithWeaponRange(1000f));
            var node = new FindDamagedAllyNode(0.02f, 0.02f, 0f);
            Assert.AreEqual(F, node.Evaluate(context), "Full armor is not a repair target.");
            for (var i = 0; i < 110; ++i) Think(runner, context, 0);
            Assert.Greater(runner.Snapshot.Ships[1].Armor.Value / runner.Snapshot.Ships[1].Armor.Max, 0.75f);
            Assert.AreEqual(S, node.Evaluate(context), "The approved threshold is 99%, not the legacy search's 75%.");
            Assert.AreEqual(runner.Snapshot.Ships[1].Id, context.TargetShip);
            Assert.AreEqual(S, new MainTargetLowHpNode(0.99f).Evaluate(context));
            Assert.AreEqual(F, new FindDamagedAllyNode(0.02f, 0.02f, 0f).Evaluate(
                ContextOf(runner, 0, WithWeaponRange(0f))), "The minimum search distance is still bounded.");
        }

        // A second definition, so a profile table can tell slot 102 from the other ships.
        private static readonly ShipDefinition OtherShip = new ShipDefinition(1f, 1f, Ship.Engine, 1000f,
            new ResourceDefinition(500f, 10f, 1f), new ResourceDefinition(300f, 20f, 0.5f));

        [Test]
        public void FindNearestEnemyPicksTheNearerEnemyOnTheTorus()
        {
            var runner = Battle2V2();
            var context = ContextOf(runner, 0);
            var ships = runner.Snapshot.Ships;
            var expected = Gap(ships[0], ships[1]) <= Gap(ships[0], ships[3]) ? ships[1].Id : ships[3].Id;

            Assert.AreEqual(S, new FindNearestEnemyNode(0f, 0f, false).Evaluate(context));
            Assert.AreEqual(expected, context.TargetShip);
            Assert.AreEqual(context.Time, context.LastTargetUpdateTime);
        }

        // Legacy TargetStats: one priority step outweighs any distance; priority None is never a target.
        [Test]
        public void TargetPriorityDecidesBeforeDistance()
        {
            var runner = Battle2V2(OtherShip);
            var ships = runner.Snapshot.Ships;

            runner.Battle.FindShip(ships[3].Id).TargetPriority = TargetPriority.High;
            runner.Step();
            var high = ContextOf(runner, 0);
            Assert.AreEqual(S, new FindNearestEnemyNode(0f, 0f, false).Evaluate(high));
            Assert.AreEqual(ships[3].Id, high.TargetShip);

            runner.Battle.FindShip(ships[3].Id).TargetPriority = TargetPriority.None;
            runner.Step();
            var none = ContextOf(runner, 0);
            Assert.AreEqual(S, new FindNearestEnemyNode(0f, 0f, false).Evaluate(none));
            Assert.AreEqual(ships[1].Id, none.TargetShip);
        }

        // Without an enemy the node searches again only after the find cooldown.
        [Test]
        public void FindCooldownDelaysTheNextSearch()
        {
            var runner = Battle2V2();
            foreach (var ship in runner.Snapshot.Ships) runner.Battle.FindShip(ship.Id).TargetPriority = TargetPriority.None;
            runner.Step();
            var context = ContextOf(runner, 0);
            var node = new FindNearestEnemyNode(1f, 0f, false);

            Assert.AreEqual(F, node.Evaluate(context));
            var searched = context.Time;
            foreach (var ship in runner.Snapshot.Ships) runner.Battle.FindShip(ship.Id).TargetPriority = TargetPriority.Normal;

            while (context.Time - searched < 1f - 1e-4f)
            {
                Assert.AreEqual(F, node.Evaluate(context), $"time {context.Time}");
                Think(runner, context, 0);
            }

            Assert.AreEqual(S, node.Evaluate(context));
            Assert.IsFalse(context.TargetShip.IsNull);
        }

        // During the change cooldown the node restores its enemy and adopts a valid main target set by another node.
        [Test]
        public void ChangeCooldownKeepsTheLastEnemy()
        {
            var runner = Battle2V2();
            var context = ContextOf(runner, 0);
            var ships = runner.Snapshot.Ships;
            var node = new FindNearestEnemyNode(0f, 10f, false);

            Assert.AreEqual(S, node.Evaluate(context));
            var first = context.TargetShip;
            var other = first == ships[1].Id ? ships[3].Id : ships[1].Id;

            context.TargetShip = default;
            Think(runner, context, 0);
            Assert.AreEqual(S, node.Evaluate(context));
            Assert.AreEqual(first, context.TargetShip);

            context.TargetShip = other;
            Assert.AreEqual(S, node.Evaluate(context));
            Assert.AreEqual(other, context.TargetShip);

            context.TargetShip = ships[2].Id;
            Assert.AreEqual(S, node.Evaluate(context), "an ally is not valid; the last enemy returns");
            Assert.AreEqual(other, context.TargetShip);
        }

        [Test]
        public void FindEnemyInAttackRangeNeedsTheEnemyInRange()
        {
            var runner = Battle2V2();
            var shortRange = WithWeaponRange(0.0001f);
            var longRange = WithWeaponRange(1000f);

            Assert.AreEqual(F, new FindEnemyInAttackRangeNode(0f, 0f, false).Evaluate(ContextOf(runner, 0, shortRange)));
            Assert.AreEqual(S, new FindEnemyInAttackRangeNode(0f, 0f, false).Evaluate(ContextOf(runner, 0, longRange)));
        }

        // Legacy NodeFactory: a ranged drone searches near its mothership; any ship with a limited range does too.
        [Test]
        public void FindEnemyVariantFollowsTheShipAndRange()
        {
            INode Build(AiShipProfile profile, AiSettings settings, bool inRange) =>
                Builder(Context(out _, profile: profile, settings: settings)).Build(Node(new BehaviorTreeNodeSerializable
                {
                    Type = BehaviorNodeType.FindEnemy, InRange = inRange, MinValue = 1f, MaxValue = 5f,
                }));

            Assert.IsInstanceOf<FindNearestEnemyNode>(Build(ArmedShip, AiSettings.Default, false));
            Assert.IsInstanceOf<FindEnemyInAttackRangeNode>(Build(ArmedShip, AiSettings.Default, true));
            Assert.IsInstanceOf<FindEnemyNearMothershipNode>(Build(ArmedShip, AiSettings.ForMercenary(20f, 50), false));
            Assert.IsInstanceOf<FindEnemyInAttackRangeNode>(Build(ArmedShip, AiSettings.ForMercenary(20f, 50), true));
            Assert.IsInstanceOf<FindEnemyNearMothershipNode>(Build(Drone, AiSettings.ForDrone(30f), false));
            Assert.IsInstanceOf<FindEnemyNearMothershipNode>(Build(Drone, AiSettings.ForDrone(30f), true));
            Assert.IsInstanceOf<FindEnemyInAttackRangeNode>(Build(Drone, AiSettings.Default, true));
        }

        [Test]
        public void AdditionalTargetsAreEnemiesOtherThanTheMainTarget()
        {
            var runner = Battle2V2();
            var context = ContextOf(runner, 0);
            var ships = runner.Snapshot.Ships;

            Assert.AreEqual(F, new HasAdditionalTargetsNode().Evaluate(context));

            context.TargetShip = ships[1].Id;
            Assert.AreEqual(S, new LookForAdditionalTargetsNode(10f).Evaluate(context));
            CollectionAssert.AreEqual(new[] { ships[3].Id }, context.SecondaryTargets);
            Assert.AreEqual(S, new HasAdditionalTargetsNode().Evaluate(context));

            context.TargetShip = ships[3].Id;
            Assert.AreEqual(S, new LookForAdditionalTargetsNode(10f).Evaluate(context));
            CollectionAssert.AreEqual(new[] { ships[3].Id }, context.SecondaryTargets, "no update within the cooldown");
        }

        // Legacy SelectBestTargets carries a displaced ship on; it is not a sort.
        [Test]
        public void AdditionalTargetsKeepTheLegacyPartialSelection()
        {
            CollectionAssert.AreEqual(new ulong[] { 6, 2, 3, 4, 5 }, Select(5f, 4f, 3f, 2f, 1f, 0.5f, 6f));
            CollectionAssert.AreEqual(new ulong[] { 6, 1, 2, 3, 4 }, Select(1f, 2f, 3f, 4f, 5f, 0f));
            CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, Select(3f, 2f, 1f));
        }

        [Test]
        public void ThreatGeometry()
        {
            Assert.IsTrue(TargetLists.IsThreat(new Vec2(10f, 0f), new Vec2(10f, 0f), 1f, out var time));
            Assert.AreEqual(1f, time, 1e-6f);
            Assert.IsTrue(TargetLists.IsThreat(new Vec2(10f, 0.5f), new Vec2(10f, 0f), 1f, out _), "passes at the contact distance");
            Assert.IsFalse(TargetLists.IsThreat(new Vec2(10f, 0.6f), new Vec2(10f, 0f), 1f, out _), "passes wide");
            Assert.IsFalse(TargetLists.IsThreat(new Vec2(10f, 0f), new Vec2(-10f, 0f), 1f, out _), "moves away");
            Assert.IsFalse(TargetLists.IsThreat(new Vec2(10f, 0f), new Vec2(1f, 0f), 1f, out _), "10 s to hit");
            Assert.IsFalse(TargetLists.IsThreat(new Vec2(0.1f, 0f), Vec2.Zero, 1f, out time), "no relative velocity");
            Assert.IsTrue(float.IsNaN(time));
        }

        // Before the first update the time to collision is unlimited; an update without bullets gives legacy 1000.
        [Test]
        public void ThreatListWithoutBullets()
        {
            var context = Context(out _);
            Assert.AreEqual(float.MaxValue, context.TimeToCollision);
            Assert.AreEqual(F, new HasIncomingThreatNode(5f).Evaluate(context));

            Assert.AreEqual(F, new LookForThreatsNode(0f).Evaluate(context));
            Assert.AreEqual(1000f, context.TimeToCollision);
            Assert.AreEqual(F, new HasIncomingThreatNode(5f).Evaluate(context));
            Assert.AreEqual(S, new HasIncomingThreatNode(1000f).Evaluate(context));
        }

        // Phase 5, step 14b, example 13: legacy ThreatList reads an asteroid as a bullet of its size. The ship (model
        // scale 2) waits at (50, 50) facing +x; asteroids of size 4 approach at 5 units per second.
        [Test]
        public void AsteroidsOnACollisionCourseAreThreats()
        {
            var context = AsteroidContext(
                new AsteroidSetup(new Vec2(65f, 50f), new Vec2(-5f, 0f), 4f, 80f, 1600f, 1f),
                new AsteroidSetup(new Vec2(65f, 55f), new Vec2(-5f, 0f), 4f, 80f, 1600f, 1f),
                new AsteroidSetup(new Vec2(71f, 50f), new Vec2(-5f, 0f), 4f, 80f, 1600f, 1f),
                new AsteroidSetup(new Vec2(35f, 50f), new Vec2(-5f, 0f), 4f, 80f, 1600f, 1f));
            var asteroids = context.Snapshot.Asteroids;

            Assert.AreEqual(S, new LookForThreatsNode(0f).Evaluate(context));
            CollectionAssert.AreEqual(new[] { asteroids[0].Id }, context.Threats,
                "the wide pass (5 > 3 units), the far one (21 units) and the receding one are not threats");
            Assert.AreEqual(3f, context.TimeToCollision, 0.05f);
            Assert.AreEqual(S, new HasIncomingThreatNode(5f).Evaluate(context));

            context.Controls.Reset();
            Assert.AreEqual(NodeState.Running, new AvoidThreatsNode().Evaluate(context));
            Assert.AreEqual(1f, context.Controls.Thrust);

            context.Controls.Reset();
            Assert.AreEqual(NodeState.Running, new DefendWithFrontalShieldNode(1).Evaluate(context));
            Assert.AreEqual(0f, context.Controls.Course.Value, 1e-3f);
            Assert.IsTrue(context.Controls.IsSystemActivated(1), "the shield faces the asteroid");
        }

        private static AiContext AsteroidContext(params AsteroidSetup[] asteroids)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Ship),
                new RosterSlotSetup(new RosterId(101), 1, Ship),
            };
            var runner = new BattleRunner(new BattleSetup(new BattleId(1), 7, roster, asteroids: asteroids), new NoPilots());
            foreach (var slot in roster) runner.Deploy(slot.RosterId, runner.Tick);
            runner.Step();

            var world = runner.Battle.World;
            var ships = runner.Snapshot.Ships;
            world.SetPose(runner.Battle.FindShip(ships[0].Id).Body, new Vec2(50f, 50f), 0f);
            world.SetPose(runner.Battle.FindShip(ships[1].Id).Body, new Vec2(150f, 150f), 0f);
            for (var i = 0; i < asteroids.Length; ++i)
            {
                var body = runner.Battle.Asteroids.Items[i].Body;
                world.SetPose(body, asteroids[i].Position, 0f);
                world.SetLinearVelocity(body, asteroids[i].Velocity);
            }
            runner.Step();

            var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, Utilites.RandomState.FromSeed(1), BattleData());
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            return context;
        }

        // Legacy AiManager: more than 30 pilots read no threats.
        [Test]
        public void ThreatsStopAboveThirtyPilots()
        {
            var runner = Battle16V16();
            var ships = runner.Snapshot.Ships;
            Assert.AreEqual(32, ships.Length);

            var battle = BattleData();
            for (var i = 0; i < AiBattle.MaxPilotsWithThreats; ++i)
                battle.Register(ships[i].Id);

            var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, default, battle);
            context.Update(runner.Snapshot, ships[0]);
            new LookForThreatsNode(0f).Evaluate(context);
            Assert.AreEqual(1000f, context.TimeToCollision, "30 pilots read threats");

            battle.Register(ships[AiBattle.MaxPilotsWithThreats].Id);
            runner.Step();
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            Assert.AreEqual(F, new LookForThreatsNode(0f).Evaluate(context));
            Assert.AreEqual(float.MaxValue, context.TimeToCollision, "31 pilots do not");
        }

        [Test]
        public void ShipConditions()
        {
            var runner = Battle2V2();
            var context = ContextOf(runner, 0);
            var ships = runner.Snapshot.Ships;

            Assert.AreEqual(F, new IsLowOnHpNode(0.5f).Evaluate(context));
            Assert.AreEqual(S, new IsLowOnHpNode(1.1f).Evaluate(context));

            Assert.AreEqual(1f, context.EnergyLevelPercentage);
            Assert.AreEqual(S, new HasEnoughEnergyNode(0.5f).Evaluate(context));
            context.LockedEnergy = 0.6f * ships[0].Energy.Max;
            Assert.AreEqual(F, new HasEnoughEnergyNode(0.5f).Evaluate(context));
            Think(runner, context, 0);
            Assert.AreEqual(S, new HasEnoughEnergyNode(0.5f).Evaluate(context), "a think unlocks the energy");

            Assert.AreEqual(F, new MainTargetIsEnemyNode().Evaluate(context));
            context.TargetShip = ships[2].Id;
            Assert.AreEqual(F, new MainTargetIsEnemyNode().Evaluate(context));
            context.TargetShip = ships[1].Id;
            Assert.AreEqual(S, new MainTargetIsEnemyNode().Evaluate(context));
        }

        [Test]
        public void TargetComparisons()
        {
            var runner = Battle2V2();
            var context = ContextOf(runner, 0, WithWeaponRange(40f));

            Assert.AreEqual(F, new IsFasterThanTargetNode(0.5f).Evaluate(context), "no target");
            Assert.AreEqual(F, new HasLongerAttackRangeNode(0.5f).Evaluate(context), "no target");

            context.TargetShip = runner.Snapshot.Ships[1].Id;
            Assert.AreEqual(F, new IsFasterThanTargetNode(1f).Evaluate(context), "same engine");
            Assert.AreEqual(S, new IsFasterThanTargetNode(0.5f).Evaluate(context));

            Assert.AreEqual(S, new HasLongerAttackRangeNode(1f).Evaluate(context), "40 against 30");
            Assert.AreEqual(F, new HasLongerAttackRangeNode(2f).Evaluate(context));
        }

        [Test]
        public void MothershipNodesOfADrone()
        {
            var runner = CarrierBattle(out var carrier, out var drone, FragileCarrier);
            var context = DroneContext(runner, drone);

            Assert.AreEqual(carrier, context.Mothership);
            Assert.AreEqual(S, new TargetMothershipNode().Evaluate(context));
            Assert.AreEqual(carrier, context.TargetShip);
            Assert.AreEqual(S, new MothershipDistanceExceededNode(0.0001f).Evaluate(context));
            Assert.AreEqual(F, new MothershipDistanceExceededNode(1000f).Evaluate(context));
            Assert.AreEqual(F, new MothershipRetreatedNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipDestroyedNode().Evaluate(context));

            // The fragile carrier rams the enemy and dies.
            StepUntilGone(runner, carrier);
            Assert.AreEqual(RosterStatus.Destroyed, runner.Snapshot.Roster[0].Status);
            Think(runner, context, drone);

            Assert.AreEqual(S, new MothershipDestroyedNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipRetreatedNode().Evaluate(context));
            Assert.AreEqual(F, new TargetMothershipNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipDistanceExceededNode(0.0001f).Evaluate(context));
        }

        [Test]
        public void DroneContextFollowsEmpCaptureAndTheNewMothershipDeath()
        {
            var crazy = new BulletDefinition(150f, 0f, 0f, 0f, lifetime: 0.02f,
                impact: BulletImpact.AllTargets, friendlyFire: true,
                effects: new ImpactEffects(driveDronesCrazy: true));
            var capture = new BulletDefinition(150f, 0f, 0f, 0f, lifetime: 0.02f,
                impact: BulletImpact.AllTargets, effects: new ImpactEffects(captureDrones: true));
            var controller = new ShipDefinition(1f, 1f, Ship.Engine, 1000f,
                new ResourceDefinition(0f), new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 10f, 0f, 0f, crazy),
                    new CannonDefinition(0, 10f, 0f, 0f, capture) });
            var pilots = new DroneControlPilots(controller);
            var runner = Start(new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Carrier),
                new RosterSlotSetup(new RosterId(101), 1, controller),
            }, 1, pilots);
            for (var i = 0; i < 30; ++i) runner.Step();
            var drone = default(EntityId);
            foreach (var ship in runner.Snapshot.Ships) if (ship.IsDrone) drone = ship.Id;
            Assert.IsFalse(drone.IsNull);
            var context = DroneContext(runner, drone);
            var oldMother = context.Mothership;
            var newMother = runner.Snapshot.Ships[1].Id;
            pilots.Systems = 1;
            for (var i = 0; i < 3; ++i) runner.Step();
            Think(runner, context, drone);
            Assert.IsFalse(context.HasOwner);
            Assert.IsTrue(context.Mothership.IsNull);
            Assert.AreEqual(F, new MothershipRetreatedNode().Evaluate(context));
            pilots.Systems = 2;
            for (var i = 0; i < 3; ++i) runner.Step();
            Think(runner, context, drone);
            Assert.IsTrue(context.HasOwner);
            Assert.AreEqual(newMother, context.Mothership);
            Assert.AreNotEqual(oldMother, context.Mothership);
            Assert.AreEqual(S, new TargetMothershipNode().Evaluate(context));
            var lateContext = DroneContext(runner, drone);
            Assert.AreEqual(newMother, lateContext.Mothership);
            runner.KillAll(0, runner.Tick);
            runner.Step();
            Assert.IsTrue(runner.Snapshot.TryGetShip(drone, out var survivor));
            context.Update(runner.Snapshot, survivor);
            lateContext.Update(runner.Snapshot, survivor);
            Assert.AreEqual(S, new MothershipDestroyedNode().Evaluate(context));
            Assert.AreEqual(S, new MothershipDestroyedNode().Evaluate(lateContext));
            Assert.AreEqual(F, new MothershipRetreatedNode().Evaluate(context));
        }

        private sealed class DroneControlPilots : IPilotProvider, IPilot
        {
            public DroneControlPilots(ShipDefinition controller) => _controller = controller;
            public ulong Systems;
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                return ship.Definition == Carrier ? new LaunchPilot(false) : ship.Definition == _controller ? this : null;
            }
            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                controls = PilotControls.Create(null, 0f, Systems);
                return true;
            }
            private readonly ShipDefinition _controller;
        }

        // A retreat is legacy Inactive. A redeployed slot is not the drone's mothership.
        [Test]
        public void RetreatedMothershipStaysRetreatedAfterARedeployment()
        {
            var runner = CarrierBattle(out var carrier, out var drone);
            var context = DroneContext(runner, drone);

            Assert.IsTrue(runner.Retreat(carrier, runner.Tick));
            StepUntilGone(runner, carrier);
            Think(runner, context, drone);
            Assert.AreEqual(S, new MothershipRetreatedNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipDestroyedNode().Evaluate(context));

            Assert.IsTrue(runner.Deploy(new RosterId(1), runner.Tick));
            runner.Step();
            Think(runner, context, drone);
            Assert.AreEqual(RosterStatus.Active, runner.Snapshot.Roster[0].Status);
            Assert.AreNotEqual(carrier, runner.Snapshot.Roster[0].ActiveEntity);
            Assert.AreEqual(S, new MothershipRetreatedNode().Evaluate(context));
            Assert.AreEqual(F, new TargetMothershipNode().Evaluate(context));
        }

        // Phase 5, step 7c: legacy MothershipLowHp compares the armor fraction of the mothership; without one it fails.
        [Test]
        public void MothershipLowHpReadsTheMothershipArmor()
        {
            var runner = CarrierBattle(out var carrier, out var drone);
            var context = DroneContext(runner, drone);
            Assert.IsTrue(runner.Snapshot.TryGetShip(carrier, out var mothership));
            var fraction = mothership.Armor.Value / mothership.Armor.Max;

            Assert.AreEqual(F, new MothershipLowHpNode(fraction).Evaluate(context));
            Assert.AreEqual(S, new MothershipLowHpNode(fraction + 0.01f).Evaluate(context));
            Assert.AreEqual(F, new MothershipLowHpNode(2f).Evaluate(Context(out _)), "not a drone");

            Assert.IsTrue(runner.Retreat(carrier, runner.Tick));
            StepUntilGone(runner, carrier);
            Think(runner, context, drone);
            Assert.AreEqual(F, new MothershipLowHpNode(2f).Evaluate(context), "the mothership left");
        }

        [Test]
        public void ShipsWithoutBayHaveNoMothership()
        {
            var context = Context(out _);
            Assert.IsTrue(context.Mothership.IsNull);
            Assert.AreEqual(F, new TargetMothershipNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipRetreatedNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipDestroyedNode().Evaluate(context));
        }

        // Phase 5, step 10d: legacy MothershipLowEnergy reads the mothership's energy share; MothershipLowShield reads it
        // too when the mothership has an energy shield, else its shield share. A missing resource gives 0.
        [Test]
        public void MothershipLowEnergyAndLowShieldReadTheMothershipsShares()
        {
            var runner = CarrierBattle(out var carrier, out var drone, FragileCarrier);
            var context = DroneContext(runner, drone);
            Assert.IsTrue(runner.Snapshot.TryGetShip(carrier, out var mothership));
            var energy = mothership.Energy.Value / mothership.Energy.Max;
            Assert.AreEqual(F, new MothershipLowEnergyNode(energy).Evaluate(context));
            Assert.AreEqual(S, new MothershipLowEnergyNode(energy + 0.01f).Evaluate(context));
            Assert.AreEqual(S, new MothershipLowShieldNode(0.01f).Evaluate(context), "no shield resource");

            var shielded = new AiContext(Drone, AiSettings.ForDrone(30f), 0.02f, default,
                BattleData(definition => definition == DroneShip ? Drone : EnergyShieldShip));
            Assert.IsTrue(runner.Snapshot.TryGetShip(drone, out var state));
            shielded.Update(runner.Snapshot, state);
            Assert.AreEqual(F, new MothershipLowShieldNode(energy).Evaluate(shielded), "reads the energy");
            Assert.AreEqual(S, new MothershipLowShieldNode(energy + 0.01f).Evaluate(shielded));
            Assert.AreEqual(F, new MothershipLowEnergyNode(2f).Evaluate(Context(out _)), "not a drone");
            Assert.AreEqual(F, new MothershipLowShieldNode(2f).Evaluate(Context(out _)), "not a drone");
        }

        private static AiShipProfile EnergyShieldShip => new AiShipProfile(null,
            new[] { new AiDevice(0, DeviceClass.EnergyShield, isAutomatic: false) }, null, isDrone: false, isStarbase: false,
            SizeClass.Frigate, enginePower: 10f, turnRate: 10f, propulsion: 20f, kineticResistance: 0f,
            rammingDamageMultiplier: 1f, modelScale: 2f, chanceToAvoidDrone: 0f);

        private static float Gap(in ShipState a, in ShipState b) => Arena.Delta(a.Position, b.Position).Length;

        private static ulong[] Select(params float[] distances)
        {
            var ships = new List<EntityId>();
            for (var i = 0; i < distances.Length; ++i) ships.Add(new EntityId((ulong)i + 1));
            TargetLists.SelectNearest(ships, new List<float>(distances));
            return ships.ConvertAll(id => id.Value).ToArray();
        }

        private static AiShipProfile WithWeaponRange(float range) => new AiShipProfile(
            new[] { new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Projectile, AiWeaponCapability.DamageEnemy, range, 0f) },
            null, null, false, false, SizeClass.Frigate, 10f, 10f, 20f, 0f, 1f, 2f, 0f);

        // Slots 1 and 2 on side 0, 101 and 102 on side 1; snapshot order 1, 101, 2, 102.
        private static BattleRunner Battle2V2(ShipDefinition secondEnemy = null)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Ship),
                new RosterSlotSetup(new RosterId(101), 1, Ship),
                new RosterSlotSetup(new RosterId(2), 0, Ship),
                new RosterSlotSetup(new RosterId(102), 1, secondEnemy ?? Ship),
            };
            return Start(roster, 2, new NoPilots());
        }

        private static BattleRunner Battle16V16()
        {
            var roster = new List<RosterSlotSetup>();
            for (var i = 0; i < 16; ++i)
            {
                roster.Add(new RosterSlotSetup(new RosterId((uint)(1 + i)), 0, Ship));
                roster.Add(new RosterSlotSetup(new RosterId((uint)(101 + i)), 1, Ship));
            }

            return Start(roster, 16, new NoPilots());
        }

        private static BattleRunner Start(List<RosterSlotSetup> roster, int maxActive, IPilotProvider pilots)
        {
            var runner = new BattleRunner(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: maxActive), pilots);
            foreach (var slot in roster)
                runner.Deploy(slot.RosterId, runner.Tick);
            runner.Step();
            return runner;
        }

        private static AiContext ContextOf(BattleRunner runner, int index, AiShipProfile profile = null,
            Func<ShipDefinition, AiShipProfile> profiles = null)
        {
            var context = new AiContext(profile ?? ArmedShip, AiSettings.Default, 0.02f, Utilites.RandomState.FromSeed(1),
                BattleData(profiles));
            context.Update(runner.Snapshot, runner.Snapshot.Ships[index]);
            return context;
        }

        private static void Think(BattleRunner runner, AiContext context, int index)
        {
            runner.Step();
            context.Update(runner.Snapshot, runner.Snapshot.Ships[index]);
        }

        internal static void Think(BattleRunner runner, AiContext context, EntityId ship)
        {
            runner.Step();
            Assert.IsTrue(runner.Snapshot.TryGetShip(ship, out var state), "the drone is still in the battle");
            context.Update(runner.Snapshot, state);
        }

        private static readonly ShipDefinition DroneShip = new ShipDefinition(0.5f, 1f, Ship.Engine, 100f,
            new ResourceDefinition(0f), new ResourceDefinition(100f));

        private static readonly ShipDefinition Carrier = new ShipDefinition(1f, 1f, Ship.Engine, 1000f,
            new ResourceDefinition(500f), new ResourceDefinition(1000f),
            devices: new DeviceDefinition[] { new DroneBayDefinition(1, 2f, DroneShip) });

        // A carrier without shield and with 1 armor; its pilot flies at the enemy.
        private static readonly ShipDefinition FragileCarrier = new ShipDefinition(1f, 10f, Ship.Engine, 1f,
            new ResourceDefinition(0f), new ResourceDefinition(1000f),
            devices: new DeviceDefinition[] { new DroneBayDefinition(1, 2f, DroneShip) });

        // Ramming damage grows with the smaller mass; the core ignores 1 point or less.
        private static readonly ShipDefinition HeavyShip = new ShipDefinition(1f, 100f, Ship.Engine, 1000f,
            new ResourceDefinition(0f), new ResourceDefinition(0f));

        // Slot 1 is a carrier that holds its bay key, so it launches one drone; slot 101 is an enemy and slot 2 a reserve.
        internal static BattleRunner CarrierBattle(out EntityId carrier, out EntityId drone, ShipDefinition carrierShip = null)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, carrierShip ?? Carrier),
                new RosterSlotSetup(new RosterId(101), 1, carrierShip == FragileCarrier ? HeavyShip : Ship),
                new RosterSlotSetup(new RosterId(2), 0, Ship),
            };
            var runner = Start(roster, 1, new LaunchPilots());
            carrier = runner.Snapshot.Ships[0].Id;
            drone = default;
            for (var i = 0; i < 100 && drone.IsNull; ++i)
            {
                runner.Step();
                foreach (var ship in runner.Snapshot.Ships)
                    if (ship.IsDrone) drone = ship.Id;
            }

            Assert.IsFalse(drone.IsNull, "the carrier launches a drone");
            return runner;
        }

        internal static AiContext DroneContext(BattleRunner runner, EntityId drone)
        {
            var context = new AiContext(Drone, AiSettings.ForDrone(30f), 0.02f, default,
                BattleData(definition => definition == DroneShip ? Drone : ArmedShip));
            Assert.IsTrue(runner.Snapshot.TryGetShip(drone, out var state));
            context.Update(runner.Snapshot, state);
            return context;
        }

        private static void StepUntilGone(BattleRunner runner, EntityId ship)
        {
            for (var i = 0; i < 5000 && runner.Snapshot.TryGetShip(ship, out _); ++i)
                runner.Step();

            Assert.IsFalse(runner.Snapshot.TryGetShip(ship, out _), "the ship left the battle");
        }

        private sealed class LaunchPilots : IPilotProvider
        {
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                if (ship.Definition == Carrier) return new LaunchPilot(ram: false);
                if (ship.Definition == FragileCarrier) return new LaunchPilot(ram: true);
                return null;
            }
        }

        // Holds the bay key; a ramming pilot also flies at the first enemy.
        private sealed class LaunchPilot : IPilot
        {
            public LaunchPilot(bool ram) => _ram = ram;

            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                float? course = null;
                if (_ram)
                    foreach (var ship in snapshot.Ships)
                    {
                        if (ship.Side == self.Side) continue;
                        var delta = Arena.Delta(self.Position, ship.Position);
                        course = (float)Math.Atan2(delta.Y, delta.X);
                        break;
                    }

                controls = PilotControls.Create(course, course.HasValue ? 1f : 0f, 1UL);
                return true;
            }

            private readonly bool _ram;
        }
    }
}

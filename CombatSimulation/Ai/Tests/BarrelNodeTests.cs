using System;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using GameDatabase.Enums;
using NUnit.Framework;
using Utilites;

namespace CombatSimulation.Ai.Tests
{
    public class BarrelNodeTests
    {
        private const float Degree = (float)(Math.PI / 180.0);

        [Test]
        public void AttackMainTargetAllowsTheAutoAimingArcBeforeTheFirstShot()
        {
            var context = Context(out var runner, false);
            Assert.IsTrue(runner.Snapshot.GetBarrels(context.Ship)[0].Target.IsNull);
            Assert.AreEqual(NodeState.Success, new AttackMainTargetNode(false).Evaluate(context));
            Assert.AreEqual(1UL, context.Controls.Systems);
            runner.ApplyControls(context.Ship.Id, runner.Tick, context.Controls.ToPilotControls());
            runner.Step();
            Assert.AreEqual(context.TargetShip, runner.Snapshot.GetBarrels(runner.Snapshot.Ships[0])[0].Target);
            Assert.AreEqual(15f, runner.Snapshot.Bullets[0].Angle / Degree, 0.01f);
        }

        [Test]
        public void TurretNodeRequestsASearchEvenWhenTheTreeFails()
        {
            var context = Context(out var runner, false);
            var pilot = new BehaviorTreePilot(new AttackTurretTargetsNode(), context, null);
            Assert.IsTrue(pilot.Think(runner.Snapshot, context.Ship, out var controls));
            Assert.AreEqual(1UL, controls.SearchTurretSystems);
            Assert.AreEqual(0UL, controls.Systems);
            runner.ApplyControls(context.Ship.Id, runner.Tick, controls);
            runner.Step();
            Assert.IsTrue(pilot.Think(runner.Snapshot, runner.Snapshot.Ships[0], out controls));
            Assert.AreEqual(1UL, controls.Systems);
        }

        [Test]
        public void AllyTargetSurvivesPilotSubmissionToTheCore()
        {
            var context = Context(out var runner, true);
            var ally = runner.Battle.FindShip(context.TargetShip);
            ally.Allegiance = runner.Battle.FindShip(context.Ship.Id).Allegiance;
            runner.Step();
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            new AttackMainTargetNode(false).Evaluate(context);
            var controls = context.Controls.ToPilotControls();
            Assert.AreEqual(ally.Id, controls.TurretTarget);
            Assert.AreEqual(1UL, controls.TurretTargetSystems);
            runner.ApplyControls(context.Ship.Id, runner.Tick, controls);
            runner.Step();
            Assert.AreEqual(ally.Id, runner.Snapshot.GetBarrels(runner.Snapshot.Ships[0])[0].Target);
        }

        [Test]
        public void ArcDoesNotHelpWhenTheBarrelHasAnotherTarget()
        {
            var context = Context(out var runner, false);
            var ship = runner.Battle.FindShip(context.Ship.Id);
            ship.GetBarrel(0).Target = ship.Id;
            runner.Step();
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            Assert.AreEqual(NodeState.Failure, new AttackMainTargetNode(false).Evaluate(context));
            Assert.AreEqual(0UL, context.Controls.Systems);
        }

        // Phase 5, step 12c: legacy FixedPlatform.ActiveTarget of an aiming satellite. The node fires at it without a
        // search request; the fire check still reads the ship heading, as legacy does.
        [Test]
        public void TurretNodeFiresAnAimingSatelliteAtItsTarget()
        {
            var satellite = new ShipDefinition(1f, 1f, AiFixtures.Engine, 1000f,
                new ResourceDefinition(0f), new ResourceDefinition(100f),
                barrels: new[] { new BarrelDefinition(new Vec2(0.4f, 0f), satellite: 0) },
                devices: new[] { new CannonDefinition(0, 10f, 0f, 0f, new BulletDefinition(0.1f, 20f, 50f, 0f)) },
                satellites: new[] { new SatelliteDefinition(new Vec2(-1f, 2.2f), 0f, 0.4f, -10f * Degree, 190f * Degree) });
            var context = Context(out var runner, false, satellite, new Vec2(70f, 52.2f), 60);
            Assert.AreEqual(context.TargetShip, runner.Snapshot.GetBarrels(context.Ship)[0].Target);
            var pilot = new BehaviorTreePilot(new AttackTurretTargetsNode(), context, null);
            Assert.IsTrue(pilot.Think(runner.Snapshot, context.Ship, out var controls));
            Assert.AreEqual(1UL, controls.Systems);
            Assert.AreEqual(0UL, controls.SearchTurretSystems);
        }

        private static AiContext Context(out BattleRunner runner, bool turret, ShipDefinition definition = null,
            Vec2? targetPosition = null, int settleTicks = 1)
        {
            definition = definition ?? new ShipDefinition(1f, 1f, AiFixtures.Engine, 1000f,
                new ResourceDefinition(0f), new ResourceDefinition(100f),
                barrels: new[] { new BarrelDefinition(Vec2.Zero, 0f, 20f * Degree, 270f * Degree, turret) },
                devices: new[] { new CannonDefinition(0, 10f, 0f, 0f, new BulletDefinition(0.1f, 20f, 50f, 0f)) });
            var profile = new AiShipProfile(new[] { new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Projectile,
                AiWeaponCapability.DamageEnemy, 50f, 0f) }, null, null, false, false, SizeClass.Frigate,
                10f, 10f, 20f, 0f, 1f, 2f, 0f);
            runner = new BattleRunner(new BattleSetup(new BattleId(1), 7, new[] {
                new RosterSlotSetup(new RosterId(1), 0, definition),
                new RosterSlotSetup(new RosterId(101), 1, AiFixtures.Ship) }), new AiFixtures.NoPilots());
            runner.Deploy(new RosterId(1), 0); runner.Deploy(new RosterId(101), 0); runner.Step();
            var ship = runner.Battle.FindShip(runner.Snapshot.Ships[0].Id);
            var target = runner.Battle.FindShip(runner.Snapshot.Ships[1].Id);
            var world = runner.Battle.World;
            world.SetPose(ship.Body, new Vec2(50f, 50f), 0f);
            world.SetPose(target.Body, new Vec2(150f, 150f), 0f);
            ship.GetBarrel(0).Reset(definition.Barrels[0], runner.Tick);
            for (var i = 0; i < 50; ++i) runner.Step();
            world.SetPose(target.Body, targetPosition ??
                new Vec2(50f, 50f) + 20f * new Vec2((float)Math.Cos(15f * Degree), (float)Math.Sin(15f * Degree)), 0f);
            for (var i = 0; i < settleTicks; ++i) runner.Step();
            var context = new AiContext(profile, AiSettings.Default, 0.02f, RandomState.FromSeed(1),
                AiFixtures.BattleData(_ => profile));
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            context.TargetShip = target.Id;
            return context;
        }
    }
}

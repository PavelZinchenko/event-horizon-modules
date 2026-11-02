using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using GameDatabase.DataModel;
using GameDatabase.Enums;
using GameDatabase.Serializable;
using NUnit.Framework;
using static CombatSimulation.Ai.Tests.AiFixtures;

namespace CombatSimulation.Ai.Tests
{
    // Movement, aiming and attack nodes against the legacy rules (phase 4, step 7d). Fixture ships spawn far apart
    // (Battle.SpawnMinEnemyDistance), at random headings.
    public class ActionNodeTests
    {
        private const NodeState S = NodeState.Success;
        private const NodeState F = NodeState.Failure;
        private const NodeState R = NodeState.Running;
        private const float Degree = (float)(Math.PI / 180.0);

        [Test]
        public void AnglesFollowMathf()
        {
            Assert.AreEqual(20f * Degree, LegacyMath.DeltaAngle(350f * Degree, 10f * Degree), 1e-5f);
            Assert.AreEqual(-20f * Degree, LegacyMath.DeltaAngle(10f * Degree, 350f * Degree), 1e-5f);
            Assert.AreEqual((float)Math.PI, LegacyMath.DeltaAngle(0f, (float)Math.PI), 1e-5f, "half a turn is positive");
            Assert.AreEqual(270f * Degree, LegacyMath.Angle(new Vec2(0f, -1f)), 1e-5f, "headings are in [0, 2π)");
        }

        [Test]
        public void InterceptGeometry()
        {
            Assert.IsTrue(LegacyMath.TryIntercept(new Vec2(10f, 0f), Vec2.Zero, Vec2.Zero, 10f, out var position, out var time));
            Assert.AreEqual(1f, time, 1e-6f);
            Assert.AreEqual(10f, position.X, 1e-6f);

            Assert.IsTrue(LegacyMath.TryIntercept(new Vec2(10f, 0f), Vec2.Zero, Vec2.Zero, 10f, 1f, out _, out time));
            Assert.AreEqual(0.9f, time, 1e-6f, "the bullet reaches the edge");

            Assert.IsTrue(LegacyMath.TryIntercept(new Vec2(0.5f, 0f), Vec2.Zero, Vec2.Zero, 10f, 1f, out _, out time));
            Assert.AreEqual(0f, time, "point blank");

            // 100 + 25 t² = 100 t².
            Assert.IsTrue(LegacyMath.TryIntercept(new Vec2(10f, 0f), new Vec2(0f, 5f), Vec2.Zero, 10f, out position, out time));
            Assert.AreEqual((float)Math.Sqrt(4.0 / 3.0), time, 1e-5f);
            Assert.AreEqual(10f * time, position.Length, 1e-4f, "the bullet travels to the meeting point");

            Assert.IsFalse(LegacyMath.TryIntercept(new Vec2(10f, 0f), new Vec2(20f, 0f), Vec2.Zero, 10f, out _, out _), "too fast");
        }

        // Legacy SelectWeaponNode.Create: a category without a weapon is Failure at build; All selects every weapon.
        [Test]
        public void SelectWeaponByCategory()
        {
            var context = Context(out _);
            var builder = Builder(context);

            Assert.AreSame(EmptyNode.Failure, builder.Build(SelectWeapon(AiWeaponCategory.Repair)));

            var damage = builder.Build(SelectWeapon(AiWeaponCategory.Damage));
            Assert.IsInstanceOf<SelectWeaponNode>(damage);
            Assert.AreEqual(S, damage.Evaluate(context));
            Assert.AreEqual(1, context.SelectedWeapons.List.Count);
            Assert.AreEqual(30f, context.SelectedWeapons.RangeMax);

            var all = builder.Build(SelectWeapon(AiWeaponCategory.All));
            Assert.AreEqual(S, all.Evaluate(context));
            Assert.AreSame(context.Profile.Weapons, context.SelectedWeapons.List);

            var unarmed = Context(out _, profile: Unarmed);
            Assert.AreEqual(F, Builder(unarmed).Build(SelectWeapon(AiWeaponCategory.All)).Evaluate(unarmed));
        }

        // Legacy RechargeEnergy: below the lower bound it fails until the energy is back at the upper bound.
        [Test]
        public void RechargeEnergyWaitsForTheUpperBound()
        {
            var context = Context(out _);
            var node = new RechargeEnergyNode(0.2f, 0.8f);
            var max = context.Ship.Energy.Max;

            Assert.AreEqual(S, node.Evaluate(context));
            context.LockedEnergy = 0.9f * max;
            Assert.AreEqual(F, node.Evaluate(context));
            Assert.IsTrue(context.RestoringEnergy);
            context.LockedEnergy = 0.5f * max;
            Assert.AreEqual(F, node.Evaluate(context), "still restoring at 50 %");
            context.LockedEnergy = 0f;
            Assert.AreEqual(S, node.Evaluate(context));
            Assert.IsFalse(context.RestoringEnergy);
        }

        // Without a target the target nodes fail; Escape succeeds, as legacy does without a target.
        [Test]
        public void NodesWithoutATarget()
        {
            var context = Context(out _);

            Assert.AreEqual(F, new AttackMainTargetNode(true).Evaluate(context));
            Assert.AreEqual(F, new AttackMainTargetNode(false).Evaluate(context));
            Assert.AreEqual(F, new AttackAdditionalTargetsNode(true).Evaluate(context));
            Assert.AreEqual(F, new AttackTurretTargetsNode().Evaluate(context));
            Assert.AreEqual(F, new MatchVelocityNode(1f).Evaluate(context));
            Assert.AreEqual(F, new ChaseNode().Evaluate(context));
            Assert.AreEqual(F, new RamNode().Evaluate(context));
            Assert.AreEqual(F, new MoveToAttackRangeNode(0.5f, 1f).Evaluate(context));
            Assert.AreEqual(F, new LookAtTargetNode().Evaluate(context));
            Assert.AreEqual(F, new SustainAimNode(false).Evaluate(context));
            Assert.AreEqual(S, new EscapeTargetAttackRadiusNode().Evaluate(context));
            Assert.AreEqual(S, new AvoidThreatsNode().Evaluate(context), "no threats");
            Assert.AreEqual(F, new FlyAroundMothershipNode(5f).Evaluate(context), "not a drone");
            Assert.AreEqual(F, new KeepDistanceNode(0f, 10f).Evaluate(context));
            Assert.IsFalse(context.Controls.RotationLocked);
            Assert.IsFalse(context.Controls.MovementLocked);
        }

        // Chase, LookAtTarget and MoveToAttackRange steer at a target that stands still.
        [Test]
        public void MovementNodesHeadForTheTarget()
        {
            var context = TargetContext(out var target);
            var heading = LegacyMath.Angle(context.Delta(context.Ship.Position, target.Position));

            Assert.AreEqual(R, new LookAtTargetNode().Evaluate(context));
            Assert.AreEqual(heading, context.Controls.Course.Value, 1e-5f);
            Assert.IsFalse(context.Controls.MovementLocked);

            context.Controls.Reset();
            Assert.AreEqual(R, new ChaseNode().Evaluate(context));
            Assert.AreEqual(heading, context.Controls.Course.Value, 1e-5f);

            context.Controls.Reset();
            Assert.AreEqual(R, new RamNode().Evaluate(context), "a target that stands still can be hit");

            context.Controls.Reset();
            Assert.AreEqual(R, new MoveToAttackRangeNode(0.5f, 1f).Evaluate(context), "the enemy spawns beyond 30");
            Assert.AreEqual(heading, context.Controls.Course.Value, 1e-5f);

            context.Controls.Reset();
            Assert.AreEqual(S, new MatchVelocityNode(0.1f).Evaluate(context), "both stand still");
        }

        // EscapeTargetAttackRadius flies around the target outside its attack range plus both model scales.
        [Test]
        public void EscapeKeepsOutOfTheTargetsRange()
        {
            var context = TargetContext(out var target);
            var distance = context.Delta(context.Ship.Position, target.Position).Length;
            var minDistance = 30f + 2f + 2f + 2f / 3 + 2f / 3;
            Assert.Greater(distance, minDistance, "fixture enemies spawn at least 40 apart");

            Assert.AreEqual(S, new EscapeTargetAttackRadiusNode().Evaluate(context));
            Assert.IsFalse(context.Controls.RotationLocked);

            // A target of range 1000 is too close: the ship turns away.
            var runner = Battle(new NoPilots());
            runner.Step();
            var inside = new AiContext(ArmedShip, AiSettings.Default, 0.02f, default, BattleData(definition => LongRange));
            inside.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            inside.TargetShip = runner.Snapshot.Ships[1].Id;
            Assert.AreEqual(R, new EscapeTargetAttackRadiusNode().Evaluate(inside));
            Assert.IsTrue(inside.Controls.RotationLocked);
        }

        // AttackWithAllWeapons turns the barrel to the target; once aimed and in range, it fires.
        [Test]
        public void AttackMainTargetAimsThenFires()
        {
            var context = TargetContext(out var target, LongRange, ticks: 30);
            Assert.IsTrue(context.DeviceOf(0).Ready, "the barrel cooldown after the spawn has passed");
            var mount = WeaponMount.Of(context.Ship, context.Profile.Weapons[0]);
            var course = mount.OptimalShipCourse(mount.Position + context.Delta(mount.Position, target.Position));

            var result = new AttackMainTargetNode(true).Evaluate(context);
            Assert.AreEqual(course, context.Controls.Course.Value, 1e-4f);

            // The fixture heading is random: the cannon fires only if the barrel already points at the target.
            var fired = context.Controls.IsSystemActivated(0);
            Assert.AreEqual(fired ? S : R, result);

            context.Controls.Reset();
            Assert.AreEqual(fired ? S : F, new AttackMainTargetNode(false).Evaluate(context), "standing still does not aim");
            Assert.IsFalse(context.Controls.RotationLocked);
        }

        // A weapon that is not ready (cooldown, energy) neither aims nor fires.
        [Test]
        public void AttackSkipsWeaponsThatAreNotReady()
        {
            var runner = Battle(new FirePilots());
            for (var i = 0; i < 3; ++i) runner.Step();

            var context = new AiContext(LongRange, AiSettings.Default, 0.02f, default, BattleData());
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            context.TargetShip = runner.Snapshot.Ships[1].Id;
            Assert.IsFalse(context.DeviceOf(0).Ready, "the cannon cools down");
            Assert.IsTrue(context.DeviceOf(0).Active);

            Assert.AreEqual(F, new AttackMainTargetNode(true).Evaluate(context));
            Assert.IsFalse(context.Controls.RotationLocked);

            Assert.AreEqual(R, new SustainAimNode(false).Evaluate(context), "the cannon fired, so it keeps the aim");
            Assert.IsTrue(context.Controls.RotationLocked);
        }

        // Legacy RecoilNode, ActivateDeviceNode and SpawnDronesNode press the device bits; automatic devices are left out.
        [Test]
        public void DeviceNodesPressTheirBits()
        {
            var context = Context(out _);
            var builder = Builder(context);

            Assert.AreSame(EmptyNode.Failure, builder.Build(Node(Leaf(BehaviorNodeType.UseRecoil))), "no recoil weapon");

            var activate = builder.Build(Node(new BehaviorTreeNodeSerializable
                { Type = BehaviorNodeType.ActivateDevice, DeviceClass = DeviceClass.PointDefense }));
            Assert.AreEqual(S, activate.Evaluate(context));
            Assert.IsTrue(context.Controls.IsSystemActivated(1));

            var spawn = builder.Build(Node(Leaf(BehaviorNodeType.SpawnDrones)));
            Assert.AreEqual(R, spawn.Evaluate(context), "a drone is in the hangar");
            Assert.IsTrue(context.Controls.IsSystemActivated(2));

            var recoil = Context(out _, profile: WithWeapon(new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Projectile,
                AiWeaponCapability.DamageEnemy, 30f, 0.5f)));
            context.Controls.Reset();
            Assert.AreEqual(R, Builder(recoil).Build(Node(Leaf(BehaviorNodeType.UseRecoil))).Evaluate(recoil));
            Assert.IsTrue(recoil.Controls.IsSystemActivated(0));

            var automatic = Context(out _, profile: new AiShipProfile(ArmedShip.Weapons,
                new[] { new AiDevice(1, DeviceClass.PointDefense, isAutomatic: true) }, new[] { new AiDroneBay(2, isAutomatic: true) },
                false, false, SizeClass.Frigate, 10f, 10f, 20f, 0f, 1f, 2f, AiTargetPriority.Normal, 0f));
            var automaticBuilder = Builder(automatic);
            Assert.AreSame(EmptyNode.Failure, automaticBuilder.Build(Node(new BehaviorTreeNodeSerializable
                { Type = BehaviorNodeType.ActivateDevice, DeviceClass = DeviceClass.PointDefense })));
            Assert.AreEqual(F, automaticBuilder.Build(Node(Leaf(BehaviorNodeType.SpawnDrones))).Evaluate(automatic),
                "a node without bays, as in legacy");
        }

        // Phase 5, step 7b: legacy DetonateShipNode holds the first detonator; with InRange only when the ship and its
        // target, moved on for the lock time, touch.
        [Test]
        public void DetonateShipHoldsTheDetonator()
        {
            var leaf = Leaf(BehaviorNodeType.DetonateShip);
            Assert.AreSame(EmptyNode.Failure, Builder(Context(out _)).Build(Node(leaf)), "no detonator");

            var profile = new AiShipProfile(ArmedShip.Weapons,
                new[] { new AiDevice(1, DeviceClass.Detonator, isAutomatic: false, activationTime: 1f) }, null,
                false, false, SizeClass.Frigate, 10f, 10f, 20f, 0f, 1f, 2f, AiTargetPriority.Normal, 0f);
            var context = TargetContext(out var target, profile);
            Assert.AreEqual(S, Builder(context).Build(Node(leaf)).Evaluate(context));
            Assert.IsTrue(context.Controls.IsSystemActivated(1));

            // Both model scales are 2: the ships touch at a centre distance of 2.
            var inRange = new BehaviorTreeNodeSerializable { Type = BehaviorNodeType.DetonateShip, InRange = true };
            var distance = context.Delta(context.Ship.Position, target.Position).Length;
            Assert.Greater(distance, 2f);
            context.Controls.Reset();
            Assert.AreEqual(F, Builder(context).Build(Node(inRange)).Evaluate(context), "the target is far");
            Assert.IsFalse(context.Controls.IsSystemActivated(1));

            context.TargetShip = default;
            Assert.AreEqual(F, Builder(context).Build(Node(inRange)).Evaluate(context), "no target");
        }

        // Phase 5, step 7c: legacy KeepDistance succeeds between the distances plus a third of both model scales; it
        // flies toward a target that is too far and away from one that is too near. It swaps reversed bounds.
        [Test]
        public void KeepDistanceHoldsTheTargetBetweenItsBounds()
        {
            var context = TargetContext(out var target);
            var distance = context.Delta(context.Ship.Position, target.Position).Length;
            var heading = LegacyMath.Angle(context.Delta(context.Ship.Position, target.Position));

            Assert.AreEqual(S, new KeepDistanceNode(0f, distance).Evaluate(context));
            Assert.AreEqual(S, new KeepDistanceNode(distance, 0f).Evaluate(context), "reversed bounds");
            Assert.IsFalse(context.Controls.Course.HasValue);

            Assert.AreEqual(R, new KeepDistanceNode(0f, 1f).Evaluate(context), "too far");
            Assert.AreEqual(heading, context.Controls.Course.Value, 1e-5f);

            context.Controls.Reset();
            Assert.AreEqual(R, new KeepDistanceNode(distance + 10f, distance + 20f).Evaluate(context), "too near");
            Assert.AreEqual(0f, LegacyMath.DeltaAngle(heading + (float)System.Math.PI, context.Controls.Course.Value), 1e-4f);
        }

        // Legacy CommonControls clamps the throttle.
        [Test]
        public void ThrottleIsClamped()
        {
            var controls = new AiControls();
            controls.Thrust = 5f;
            Assert.AreEqual(1f, controls.ToPilotControls().Throttle);

            controls.Reset();
            controls.Thrust = float.NaN;
            Assert.AreEqual(0f, controls.ToPilotControls().Throttle);
        }

        // A tree that finds the enemy, attacks it and chases it otherwise damages an idle enemy with its cannon, and the
        // same seed gives the same battle.
        [Test]
        public void AttackTreeDamagesAnIdleEnemy()
        {
            var first = RunAttackTree(out var fired);
            var second = RunAttackTree(out _);

            Assert.IsTrue(fired, "the cannon fired");
            Assert.Less(first.Shield.Value, first.Shield.Max, "the enemy took damage");
            Assert.AreEqual(first.Position, second.Position);
            Assert.AreEqual(first.Shield.Value, second.Shield.Value);
        }

        private static ShipState RunAttackTree(out bool fired)
        {
            var tree = Node(Composite(BehaviorNodeType.Sequence, Leaf(BehaviorNodeType.FindEnemy),
                Composite(BehaviorNodeType.Selector, Leaf(BehaviorNodeType.AttackMainTarget), Leaf(BehaviorNodeType.Chase))));

            var runner = Battle(new SidePilots(tree));
            runner.Step();
            var attacker = runner.Snapshot.Ships[0].Id;
            fired = false;
            for (var i = 0; i < 3000; ++i)
            {
                runner.Step();
                Assert.IsTrue(runner.Snapshot.TryGetShip(attacker, out var self));
                fired |= runner.Snapshot.GetDevices(self)[0].Active;
                Assert.IsTrue(runner.Snapshot.TryGetShip(runner.Snapshot.Ships[1].Id, out var enemy));
                if (enemy.Shield.Value < enemy.Shield.Max - 30f) break;
            }

            return runner.Snapshot.Ships[1];
        }

        // A weapon of range 1000, so the aim does not depend on where the fixture ships spawn.
        private static AiShipProfile LongRange => WithWeapon(new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Projectile,
            AiWeaponCapability.DamageEnemy, 1000f, 0f));

        private static AiShipProfile WithWeapon(AiWeapon weapon) => new AiShipProfile(new[] { weapon }, ArmedShip.Devices,
            ArmedShip.DroneBays, false, false, SizeClass.Frigate, 10f, 10f, 20f, 0f, 1f, 2f, AiTargetPriority.Normal, 0f);

        private static BehaviorTreeNode SelectWeapon(AiWeaponCategory category) =>
            Node(new BehaviorTreeNodeSerializable { Type = BehaviorNodeType.SelectWeapon, WeaponType = category });

        // The fixture context of ship 0 with the enemy as its main target.
        private static AiContext TargetContext(out ShipState target, AiShipProfile profile = null, int ticks = 0)
        {
            var context = Context(out var runner, profile: profile);
            for (var i = 0; i < ticks; ++i)
                Think(context, runner);

            target = runner.Snapshot.Ships[1];
            context.TargetShip = target.Id;
            return context;
        }

        // Ship 0 holds its cannon; the enemy idles.
        private sealed class FirePilots : IPilotProvider
        {
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                return ship.Side == 0 ? new FirePilot() : null;
            }
        }

        private sealed class FirePilot : IPilot
        {
            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                controls = PilotControls.Create(null, 0f, 1UL);
                return true;
            }
        }

        // Phase 5, step 7a: at full throttle Chase holds the first warp drive while the target is within its range.
        [Test]
        public void ChaseUsesTheWarpDriveTowardATargetInRange()
        {
            Assert.IsTrue(ChaseWarps(1000f), "aligned within 5 degrees and in range");
            Assert.IsFalse(ChaseWarps(5f), "beyond the warp range");
        }

        // The point-defense bit (device 1) stands in for the warp drive: the snapshot shows the held bit.
        private static bool ChaseWarps(float range)
        {
            var runner = Battle(new ChasePilots(new AiDevice(1, DeviceClass.TeleporterV2, false, range)));
            for (var i = 0; i < 300; ++i)
            {
                runner.Step();
                if ((runner.Snapshot.Ships[0].Systems & 2UL) != 0) return true;
            }
            return false;
        }

        // Ship 0 chases the enemy; the enemy idles.
        private sealed class ChasePilots : IPilotProvider
        {
            public ChasePilots(AiDevice warpDrive) => _warpDrive = warpDrive;

            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                if (ship.Side != 0) return null;

                var context = new AiContext(ArmedShip, AiSettings.Default, schedule.Period * CombatSimulation.Core.Battle.TickDuration,
                    Utilites.RandomState.FromSeed(1), BattleData());
                return new BehaviorTreePilot(new ChaseEnemy(new ChaseNode(_warpDrive)), context,
                    new Dictionary<BehaviorNodeType, int>());
            }

            private readonly AiDevice _warpDrive;
        }

        private sealed class ChaseEnemy : INode
        {
            public ChaseEnemy(INode chase) => _chase = chase;

            public NodeState Evaluate(AiContext context)
            {
                foreach (var ship in context.Snapshot.Ships)
                    if (ship.Side != context.Ship.Side) context.TargetShip = ship.Id;
                return _chase.Evaluate(context);
            }

            private readonly INode _chase;
        }

        // Ship 0 runs the tree; the enemy idles.
        private sealed class SidePilots : IPilotProvider
        {
            public SidePilots(BehaviorTreeNode tree) => _tree = tree;

            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                if (ship.Side != 0) return null;

                var context = new AiContext(ArmedShip, AiSettings.Default, schedule.Period * CombatSimulation.Core.Battle.TickDuration,
                    Utilites.RandomState.FromSeed(1), BattleData());
                var builder = Builder(context);
                return new BehaviorTreePilot(builder.Build(_tree), context, builder.Stubs);
            }

            private readonly BehaviorTreeNode _tree;
        }
    }
}

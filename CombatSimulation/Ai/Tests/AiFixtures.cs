using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using GameDatabase.DataModel;
using GameDatabase.Enums;
using GameDatabase.Serializable;

namespace CombatSimulation.Ai.Tests
{
    internal static class AiFixtures
    {
        private const float Degrees = (float)(Math.PI / 180.0);

        public static readonly EngineDefinition Engine = new EngineDefinition(20f, 10f, 300f * Degrees, 120f * Degrees);

        // A drone without devices. Fixture battles give drones no pilot.
        public static readonly ShipDefinition DroneShip = new ShipDefinition(0.5f, 1f, Engine, 100f,
            new ResourceDefinition(0f), new ResourceDefinition(100f));

        // The devices of the ArmedShip profile: a cannon on a forward barrel (device 0), point defense (1) and a drone
        // bay (2).
        public static readonly ShipDefinition Ship = new ShipDefinition(1f, 1f, Engine, 1000f,
            new ResourceDefinition(500f, 10f, 1f), new ResourceDefinition(300f, 20f, 0.5f),
            barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
            devices: new DeviceDefinition[]
            {
                new CannonDefinition(0, 0.25f, 5f, 0f, new BulletDefinition(0.25f, 30f, 30f, 10f)),
                new PointDefenseDefinition(3f, 0.3f, 1f),
                new DroneBayDefinition(1, 5f, DroneShip),
            });

        public static AiShipProfile ArmedShip => new AiShipProfile(
            new[] { new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Projectile, AiWeaponCapability.DamageEnemy, 30f, 0f) },
            new[] { new AiDevice(1, DeviceClass.PointDefense, isAutomatic: false) }, new[] { new AiDroneBay(2, isAutomatic: false) },
            isDrone: false, isStarbase: false, SizeClass.Frigate,
            enginePower: 10f, turnRate: 10f, propulsion: 20f, kineticResistance: 0.2f, rammingDamageMultiplier: 1f,
            modelScale: 2f, AiTargetPriority.Normal, chanceToAvoidDrone: 0f);

        public static AiShipProfile Drone => new AiShipProfile(
            new[] { new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Projectile, AiWeaponCapability.DamageEnemy, 10f, 0f) },
            null, null, isDrone: true, isStarbase: false, SizeClass.Undefined,
            enginePower: 20f, turnRate: 20f, propulsion: 40f, kineticResistance: 0f, rammingDamageMultiplier: 1f,
            modelScale: 0.5f, AiTargetPriority.Low, chanceToAvoidDrone: 0f);

        public static AiShipProfile Starbase => new AiShipProfile(
            new[] { new AiWeapon(0, AiWeaponClass.Continuous, AiBulletBehavior.Beam, AiWeaponCapability.DamageEnemy, 40f, 0f) },
            null, new[] { new AiDroneBay(2, isAutomatic: false) }, isDrone: false, isStarbase: true, SizeClass.Starbase,
            enginePower: 0f, turnRate: 0f, propulsion: 0f, kineticResistance: 0.5f, rammingDamageMultiplier: 1f,
            modelScale: 10f, AiTargetPriority.Normal, chanceToAvoidDrone: 0f);

        public static AiShipProfile Unarmed => new AiShipProfile(null, null, null, isDrone: false,
            isStarbase: false, SizeClass.Destroyer, enginePower: 10f, turnRate: 10f, propulsion: 20f,
            kineticResistance: 0f, rammingDamageMultiplier: 1f, modelScale: 2f, AiTargetPriority.Normal,
            chanceToAvoidDrone: 0f);

        // Every fixture ship shares one definition, so every ship in a fixture battle is an armed ship.
        public static AiShipProfile Profiles(ShipDefinition definition) => ArmedShip;

        // The arena of BattleSetup's default size.
        public static readonly Torus Arena = new Torus(200f, 200f);

        // Legacy projectile prefabs have a collider of radius 0.25 at body size 1.
        public static float BulletScale(BulletDefinition definition) => 4f * definition.Radius;

        public static AiBattle BattleData(Func<ShipDefinition, AiShipProfile> profiles = null) =>
            new AiBattle(Arena, profiles ?? Profiles, BulletScale);

        public static BehaviorTreeFactory Factory(ulong matchSeed = 7) => new BehaviorTreeFactory(matchSeed, Arena, Profiles, BulletScale);

        // Slot 1 on side 0 and slot 101 on side 1 (2 ships per side with extraSlots), deployed at the first tick.
        public static BattleRunner Battle(IPilotProvider pilots, ulong matchSeed = 7, bool extraSlots = false)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Ship),
                new RosterSlotSetup(new RosterId(101), 1, Ship),
            };
            if (extraSlots)
            {
                roster.Add(new RosterSlotSetup(new RosterId(2), 0, Ship));
                roster.Add(new RosterSlotSetup(new RosterId(102), 1, Ship));
            }

            var setup = new BattleSetup(new BattleId(1), matchSeed, roster, maxActiveShipsPerSide: extraSlots ? 2 : 1);
            var runner = new BattleRunner(setup, pilots);
            foreach (var slot in roster)
                runner.Deploy(slot.RosterId, runner.Tick);
            return runner;
        }

        // A context that has thought once on the first snapshot of a battle without pilots.
        public static AiContext Context(out BattleRunner runner, float deltaTime = 0.02f, AiShipProfile profile = null,
            AiSettings? settings = null)
        {
            runner = Battle(new NoPilots());
            runner.Step();
            var context = new AiContext(profile ?? ArmedShip, settings ?? AiSettings.Default, deltaTime,
                Utilites.RandomState.FromSeed(1), BattleData());
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            return context;
        }

        // Steps the battle and lets the context think on the new snapshot.
        public static void Think(AiContext context, BattleRunner runner)
        {
            runner.Step();
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
        }

        public static BehaviorTreeNode Node(BehaviorTreeNodeSerializable serializable) => BehaviorTreeNode.Create(serializable, null);

        public static BehaviorTreeNodeSerializable Leaf(BehaviorNodeType type) => new BehaviorTreeNodeSerializable { Type = type };

        public static BehaviorTreeNodeSerializable Composite(BehaviorNodeType type, params BehaviorTreeNodeSerializable[] nodes) =>
            new BehaviorTreeNodeSerializable { Type = type, Nodes = nodes };

        public static NodeBuilder Builder(AiContext context, MessageHub hub = null) => new NodeBuilder(context, hub ?? new MessageHub());

        public sealed class NoPilots : IPilotProvider
        {
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                return null;
            }
        }
    }

    // Returns the given results in turn, then the last one; counts evaluations.
    internal sealed class ScriptedNode : INode
    {
        public ScriptedNode(params NodeState[] results) => _results = results;

        public int Evaluations { get; private set; }

        public NodeState Evaluate(AiContext context)
        {
            var result = _results[Math.Min(Evaluations, _results.Length - 1)];
            Evaluations++;
            return result;
        }

        private readonly NodeState[] _results;
    }
}

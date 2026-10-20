using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core.Tests
{
    // Code fixtures for core tests. Side 0 slots get roster IDs 1, 2, ...; side 1 slots get 101, 102, ...
    internal static class Fixtures
    {
        public const float Degrees = (float)(Math.PI / 180.0);

        // 20 u/s² propulsion, 10 u/s, 300°/s² turn rate, 120°/s.
        public static readonly EngineDefinition Engine = new EngineDefinition(20f, 10f, 300f * Degrees, 120f * Degrees);

        // The B4 acceptance ship: 1000 armor, 500 shield, 300 energy.
        public static readonly ResourceDefinition Shield = new ResourceDefinition(500f, 10f, 1f);
        public static readonly ResourceDefinition Energy = new ResourceDefinition(300f, 20f, 0.5f);

        public static readonly ShipDefinition Ship = ShipWith();

        public static ShipDefinition ShipWith(float hullRadius = 1f, float mass = 1f, EngineDefinition engine = null,
            float armor = 1000f, ResourceDefinition? shield = null, ResourceDefinition? energy = null,
            EngineDefinition engineWithoutEnergy = null, IReadOnlyList<Circle> extraHullParts = null,
            float rammingDamageMultiplier = 1f) =>
            new ShipDefinition(hullRadius, mass, engine ?? Engine, armor, shield ?? Shield, energy ?? Energy,
                engineWithoutEnergy, extraHullParts, rammingDamageMultiplier);

        public static List<RosterSlotSetup> Roster(int side0Slots, int side1Slots)
        {
            var roster = new List<RosterSlotSetup>();
            for (var i = 0; i < side0Slots; ++i) roster.Add(new RosterSlotSetup(new RosterId((uint)(1 + i)), 0, Ship));
            for (var i = 0; i < side1Slots; ++i) roster.Add(new RosterSlotSetup(new RosterId((uint)(101 + i)), 1, Ship));
            return roster;
        }

        public static BattleSetup Setup(int side0Slots = 1, int side1Slots = 1, long timeLimitTicks = 0,
            int maxActiveShipsPerSide = 1, ulong battleId = 1, ulong matchSeed = 7) =>
            new BattleSetup(new BattleId(battleId), matchSeed, Roster(side0Slots, side1Slots),
                timeLimitTicks: timeLimitTicks, maxActiveShipsPerSide: maxActiveShipsPerSide);

        // One slot per side; slot 1 flies the given ship, slot 101 the default one.
        public static BattleSetup Setup(ShipDefinition ship) =>
            new BattleSetup(new BattleId(1), 7, new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, ship),
                new RosterSlotSetup(new RosterId(101), 1, Ship),
            });

        // One slot per side with the given ships: slot 1 on side 0, slot 101 on side 1.
        public static BattleSetup Setup(ShipDefinition side0, ShipDefinition side1) =>
            new BattleSetup(new BattleId(1), 7, new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, side0),
                new RosterSlotSetup(new RosterId(101), 1, side1),
            });
    }

    // Submits roster commands with increasing command IDs, targeted at the next tick by default.
    internal sealed class Driver
    {
        private ulong _nextCommandId = 1;

        public Driver(BattleSetup setup) => Battle = new Battle(setup) { ThrowOnDefect = true };

        public Battle Battle { get; }
        public long Tick => Battle.Tick;
        public BattleId BattleId => Battle.BattleId;

        public bool Deploy(uint rosterId, long? tick = null) =>
            Battle.Submit(RosterCommand.Deploy(BattleId, _nextCommandId++, tick ?? Tick, new RosterId(rosterId)));

        public bool Retreat(EntityId id, long? tick = null) =>
            Battle.Submit(RosterCommand.Retreat(BattleId, _nextCommandId++, tick ?? Tick, id));

        public bool Surrender(int side, long? tick = null) =>
            Battle.Submit(RosterCommand.Surrender(BattleId, _nextCommandId++, tick ?? Tick, side));

        public bool KillAll(int requestingSide, long? tick = null) =>
            Battle.Submit(RosterCommand.KillAll(BattleId, _nextCommandId++, tick ?? Tick, requestingSide));

        // Controls for the active ship of a slot.
        public bool Control(uint rosterId, float? course = null, float throttle = 0f, ulong systems = 0, long? tick = null) =>
            Control(Entity(rosterId), course, throttle, systems, tick);

        public bool Control(EntityId id, float? course = null, float throttle = 0f, ulong systems = 0, long? tick = null) =>
            Battle.Submit(ControlFrame.Create(BattleId, _nextCommandId++, tick ?? Tick, id, course, throttle, systems));

        public void Kill(uint rosterId) => Battle.QueueDeath(Entity(rosterId));

        public void Step(long count = 1)
        {
            for (var i = 0; i < count; ++i) Battle.Step();
        }

        public void StepUntil(long tick)
        {
            while (Battle.Tick < tick) Battle.Step();
        }

        public RosterSlot Slot(uint rosterId) => Battle.Roster.Find(new RosterId(rosterId));
        public RosterStatus Status(uint rosterId) => Slot(rosterId).Status;
        public EntityId Entity(uint rosterId) => Slot(rosterId).ActiveShip?.Id ?? default;
        public ShipEntity Ship(uint rosterId) => Slot(rosterId).ActiveShip;

        // Teleports the active ship of a slot, at angle 0 and without spin.
        public void Place(uint rosterId, Vec2 position, Vec2 velocity)
        {
            var body = Ship(rosterId).Body;
            Battle.World.SetPose(body, position, 0f);
            Battle.World.SetLinearVelocity(body, velocity);
            Battle.World.SetAngularVelocity(body, 0f);
        }

        // Steps and collects the events of every step.
        public List<BattleEvent> StepCollecting(long count = 1)
        {
            var events = new List<BattleEvent>();
            for (var i = 0; i < count; ++i)
            {
                Battle.Step();
                foreach (var item in Battle.Snapshot.Events) events.Add(item);
            }
            return events;
        }
    }
}

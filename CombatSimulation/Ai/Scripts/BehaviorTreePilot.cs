using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using GameDatabase.DataModel;
using GameDatabase.Enums;
using GameDatabase.Model;
using Utilites;

namespace CombatSimulation.Ai
{
    // Runs one ship's behavior tree on each snapshot it thinks on (legacy ShipBehaviorTree). A result other than Failure
    // submits the controls the nodes set. Failure submits zero controls once, then keeps them (T7). Ship commands
    // (vanish, allegiance) go with the next frame whatever the result: legacy applies them at once (step 7e).
    public sealed class BehaviorTreePilot : IPilot
    {
        internal BehaviorTreePilot(INode root, AiContext context, IReadOnlyDictionary<BehaviorNodeType, int> stubs)
        {
            _root = root;
            Context = context;
            Stubs = stubs;
        }

        public AiContext Context { get; }

        // Node types built as stubs, with their counts (phase 4, step 6).
        public IReadOnlyDictionary<BehaviorNodeType, int> Stubs { get; }

        // The last think was not Failure: the tree drives the ship.
        public bool IsActive => _lastResult;

        public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
        {
            Context.Update(snapshot, self);
            var result = _root.Evaluate(Context) != NodeState.Failure;
            if (!result) Context.Controls.Reset();

            var commands = Context.Controls.HasCommands;
            controls = Context.Controls.ToPilotControls();
            Context.Controls.Reset();
            Context.Controls.ClearCommands();

            var submit = result || _lastResult || commands;
            _lastResult = result;
            return submit;
        }

        private readonly INode _root;
        private bool _lastResult;
    }

    // Builds the tree pilots of one battle; they share its message hub (T5), the arena and the side tables: profiles
    // returns the AI facts of every ship by its definition (step 7b), bulletScales the legacy body scale of every
    // bullet definition (step 7c). broadcast shows a ShowMessage text (ship, database text key, colour); it runs
    // inside Think, on the thread that steps the battle.
    public sealed class BehaviorTreeFactory
    {
        public BehaviorTreeFactory(ulong matchSeed, Torus arena, Func<ShipDefinition, AiShipProfile> profiles,
            Func<BulletDefinition, float> bulletScales, Action<EntityId, string, ColorData> broadcast = null)
        {
            _matchSeed = matchSeed;
            _battle = new AiBattle(arena, profiles ?? throw new ArgumentNullException(nameof(profiles)),
                bulletScales ?? throw new ArgumentNullException(nameof(bulletScales)), broadcast);
        }

        // Legacy PlayerInputSignal: the player steered, thrust or fired in the snapshot of this tick. Every tree of the
        // battle reads one clock (IsNotControledByPlayer), as legacy AiManager does.
        public void OnPlayerInput(long tick) => _battle.OnPlayerInput(tick);

        // A null model gives a pilot whose root is Failure: the ship idles (T6).
        public BehaviorTreePilot Create(BehaviorTreeModel model, AiShipProfile profile, AiSettings settings,
            EntityId ship, PilotSchedule schedule)
        {
            _battle.Register(ship);
            var context = new AiContext(profile, settings, schedule.Period * Battle.TickDuration, CreateRandom(ship), _battle);
            var builder = new NodeBuilder(context, _messageHub);
            var root = builder.BuildTree(model);
            return new BehaviorTreePilot(root, context, builder.Stubs);
        }

        // A player ship with the autopilot component (AI data, gap 4): the player pilot and the autopilot tree in one
        // pilot on the player schedule (k = 1).
        public AutopilotPilot CreateAutopilot(IPilot player, BehaviorTreeModel model, AiShipProfile profile, EntityId ship) =>
            new AutopilotPilot(player, Create(model, profile, AiSettings.Default, ship, AutopilotPilot.Schedule));

        // The stub types a ship reaches, by the tree that holds them (step 7a). Stubs do not fold, so the count is not cut
        // at the first stub of a sequence; legacy folding of requirements and constants stays.
        public static IReadOnlyDictionary<int, HashSet<BehaviorNodeType>> CountStubs(BehaviorTreeModel model,
            AiShipProfile profile, AiSettings settings)
        {
            // The count builds a tree and never evaluates it, so the context needs no battle.
            var context = new AiContext(profile, settings, Battle.TickDuration, default, null);
            var builder = new NodeBuilder(context, new MessageHub(), foldStubs: false);
            builder.BuildTree(model);
            return builder.StubsByTree;
        }

        // One stream per pilot from the match seed and the entity ID (T4). Entity IDs are already mixed (fmix64).
        private RandomState CreateRandom(EntityId ship) =>
            new RandomState { Value = PcgRandomAlgorithm.InitializeState(_matchSeed ^ ship.Value) };

        private readonly ulong _matchSeed;
        private readonly AiBattle _battle;
        private readonly MessageHub _messageHub = new MessageHub();
    }

    // Legacy ShipFactory gives a ship with the autopilot component a keyboard controller and the autopilot tree; both
    // write the same controls, and the tree (IsNotControledByPlayer) waits while the player gives input. Here one pilot
    // holds both: the tree's frame while the tree is active, else the player's.
    public sealed class AutopilotPilot : IPilot
    {
        public static PilotSchedule Schedule => PilotSchedule.PlayerShip;

        internal AutopilotPilot(IPilot player, BehaviorTreePilot tree)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            Tree = tree;
        }

        public BehaviorTreePilot Tree { get; }

        public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
        {
            var submit = Tree.Think(snapshot, self, out controls);
            if (Tree.IsActive) return submit;
            return _player.Think(snapshot, self, out controls);
        }

        private readonly IPilot _player;
    }

    // Legacy ControllerFactory: which tree a ship runs (T6). Null means no tree; legacy then used a hard-coded controller.
    public static class BehaviorTreeSelection
    {
        public static BehaviorTreeModel ForEnemy(CombatSettings settings, BehaviorTreeModel customAi) => customAi ?? settings?.EnemyAI;

        public static BehaviorTreeModel ForStarbase(CombatSettings settings, BehaviorTreeModel customAi) => customAi ?? settings?.StarbaseAI;

        public static BehaviorTreeModel ForClone(CombatSettings settings, BehaviorTreeModel customAi) => customAi ?? settings?.CloneAI;

        public static BehaviorTreeModel ForAutopilot(CombatSettings settings) => settings?.AutopilotAI;

        // Legacy DroneBay picks the bay tree of the bay behaviour, else the drone's own; ControllerFactory then falls
        // back to the CombatSettings tree of that behaviour unless the bay has ImprovedAi.
        public static BehaviorTreeModel ForDrone(CombatSettings settings, in DroneBayStats bay, BehaviorTreeModel droneCustomAi, bool defensive)
        {
            var tree = (defensive ? bay.DefensiveDroneAI : bay.OffensiveDroneAI) ?? droneCustomAi;
            if (tree != null || bay.ImprovedAi)
                return tree;

            return defensive ? settings?.DefensiveDroneAI : settings?.OffensiveDroneAI;
        }
    }
}

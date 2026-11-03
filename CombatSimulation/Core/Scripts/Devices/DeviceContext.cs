using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // What a device reads and asks of the battle (phase 5, step 8b, item 8; decision 1). Battle builds one and passes
    // it to every device call.
    internal sealed class DeviceContext
    {
        public DeviceContext(BattleWorld world, SpawnSystem spawns, WormTails tails, BattleNetwork network)
        {
            _world = world;
            _spawns = spawns;
            _tails = tails;
            _network = network;
        }

        public BattleSetup Setup => _world.Setup;
        public long Tick => _world.Tick;
        public PhysicsWorld Physics => _world.Physics;
        public BulletStore Bullets => _world.Bullets;

        // Stage 2: a device asks for a ship, a bullet or a beam; stage 5 creates it.
        public void RequestSpawn(in SpawnRequest request) => _spawns.Request(request);

        // The owner records a use that started or ended at the tick; without remote owners nothing is recorded.
        public void CaptureDeviceUse(ShipEntity ship, int deviceIndex, bool started, long tick) =>
            _network?.CaptureDeviceUse(ship, deviceIndex, started, tick);

        public int CountAttachedSegments(WormTailDevice device) => _tails.CountAttachedSegments(device);
        public void GrowTail(ShipEntity ship, WormTailDevice device) => _tails.GrowTail(ship, device);

        private readonly BattleWorld _world;
        private readonly SpawnSystem _spawns;
        private readonly WormTails _tails;
        // Null without remote owners (decision 3b).
        private readonly BattleNetwork _network;
    }
}

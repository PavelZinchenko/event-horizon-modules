using System;

namespace CombatSimulation.Core
{
    // Immutable setup shared by battle instances. It contains no mutable physics settings.
    public sealed class BattleSetup
    {
        public BattleSetup(BattleId battleId, ulong matchSeed, float width = 200f, float height = 200f)
        {
            if (!(width > 0f) || float.IsInfinity(width))
                throw new ArgumentOutOfRangeException(nameof(width), "Arena width must be finite and positive.");
            if (!(height > 0f) || float.IsInfinity(height))
                throw new ArgumentOutOfRangeException(nameof(height), "Arena height must be finite and positive.");

            BattleId = battleId;
            MatchSeed = matchSeed;
            Width = width;
            Height = height;
        }

        public BattleId BattleId { get; }
        public ulong MatchSeed { get; }
        public float Width { get; }
        public float Height { get; }
    }
}

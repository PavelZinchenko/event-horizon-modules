namespace CombatSimulation.Core
{
    // Who hits whom (phase 4, D13): the legacy UnitSide. The side stays the team (result, timer, surrender); the
    // allegiance of each ship and bullet decides hostility and collision. Side 0 starts as Player, side 1 as Enemy.
    // Undefined: a drone without its owner (legacy GoBerserk) attacks everybody. Neutral: friendly fire (phase 5).
    // Ally: Frontier merchants later; Event Horizon battles create none.
    public enum Allegiance : byte
    {
        Undefined = 0,
        Player = 1,
        Ally = 2,
        Enemy = 3,
        Neutral = 4,
    }

    public static class Allegiances
    {
        public const Allegiance Last = Allegiance.Neutral;

        // Collision groups: Player and Ally share one (legacy layer matrix).
        internal const int GroupCount = 4;

        public static Allegiance OfSide(int side) => side == 0 ? Allegiance.Player : Allegiance.Enemy;

        public static bool IsValid(Allegiance allegiance) => allegiance <= Last;

        // Legacy UnitSideExtensions.IsEnemy: equal allegiances and the Player-Ally pair are friends.
        public static bool IsHostile(Allegiance first, Allegiance second) => Group(first) != Group(second);

        // Legacy UnitType.GetCollisionLayer order: Player and Ally, Enemy, Neutral, Undefined.
        internal static int Group(Allegiance allegiance)
        {
            switch (allegiance)
            {
                case Allegiance.Player:
                case Allegiance.Ally:
                    return 0;
                case Allegiance.Enemy:
                    return 1;
                case Allegiance.Neutral:
                    return 2;
                default:
                    return 3;
            }
        }
    }
}

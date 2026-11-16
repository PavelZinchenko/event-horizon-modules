namespace Utilites
{
    public struct RandomState
    {
        public ulong Value;

        public static RandomState FromSeed(ulong seed) => new RandomState { Value = PcgRandomAlgorithm.InitializeState(seed) };
        public static RandomState FromTickCount() => new RandomState { Value = PcgRandomAlgorithm.InitializeState((ulong)System.Environment.TickCount) };
    }
}

namespace CombatSimulation.Core
{
    // FNV-1a over little-endian fields, finished by the Murmur3 fmix64 finalizer.
    // The result depends only on the appended bytes: no runtime hash codes, no allocation history.
    internal static class StableHash
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        // Starts a hash with a one-byte domain tag, so different uses never share an input space.
        internal static ulong Begin(byte domainTag) => AppendByte(OffsetBasis, domainTag);

        internal static ulong Append(ulong hash, ulong value, int byteCount)
        {
            for (var i = 0; i < byteCount; ++i)
            {
                hash = AppendByte(hash, unchecked((byte)value));
                value >>= 8;
            }
            return hash;
        }

        internal static ulong AppendByte(ulong hash, byte value) => unchecked((hash ^ value) * Prime);

        // FNV-1a leaves each low output bit a function of the same low input bits only.
        // fmix64 is a bijection, so it spreads every input bit without adding collisions.
        internal static ulong Mix(ulong value)
        {
            unchecked
            {
                value ^= value >> 33;
                value *= 0xFF51AFD7ED558CCDUL;
                value ^= value >> 33;
                value *= 0xC4CEB9FE1A85EC53UL;
                value ^= value >> 33;
                return value;
            }
        }
    }
}

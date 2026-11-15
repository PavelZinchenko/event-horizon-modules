using System.Collections.Generic;

namespace CombatSimulation.Ai
{
    // Gives each name a small ID in first-use order. Legacy IdentifiersMap is a struct in readonly fields, so every call
    // works on a copy and every name gets ID 0; built-in trees use one flag name and one message name, so a working map
    // gives the same behaviour for them.
    internal sealed class IdentifiersMap
    {
        public int GetId(string name)
        {
            name ??= string.Empty;
            if (_ids.TryGetValue(name, out var id))
                return id;

            id = _ids.Count;
            _ids.Add(name, id);
            return id;
        }

        private readonly Dictionary<string, int> _ids = new Dictionary<string, int>();
    }

    // 64 flags; IDs outside 0-63 read false and ignore writes (legacy BitArray64).
    internal struct BitArray64
    {
        public bool this[int index]
        {
            get => index >= 0 && index <= 63 && ((_value >> index) & 1) == 1;
            set
            {
                if (index < 0 || index > 63) return;
                if (value)
                    _value |= 1UL << index;
                else
                    _value &= ~(1UL << index);
            }
        }

        private ulong _value;
    }
}

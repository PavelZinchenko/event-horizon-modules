using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // One damage type (D4). Its behaviour is data; the damage stage has no per-type branch.
    public sealed class DamageTypeDefinition
    {
        public DamageTypeDefinition(int shieldOrder)
        {
            ShieldOrder = shieldOrder;
        }

        // Types with a lower order meet the shield first; types with the same order share it in proportion.
        public int ShieldOrder { get; }
    }

    // The damage types of a battle. A damage type is an index into this table.
    public sealed class DamageTypeTable
    {
        // A hit record carries the type in one byte at most.
        public const int MaxTypes = 128;

        public DamageTypeTable(IReadOnlyList<DamageTypeDefinition> types, int rammingDamageType)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            if (types.Count < 1 || types.Count > MaxTypes)
                throw new ArgumentOutOfRangeException(nameof(types), $"A battle has 1 to {MaxTypes} damage types.");
            if (rammingDamageType < 0 || rammingDamageType >= types.Count)
                throw new ArgumentOutOfRangeException(nameof(rammingDamageType), "Ramming damage type does not exist.");

            var definitions = new DamageTypeDefinition[types.Count];
            for (var i = 0; i < definitions.Length; ++i)
                definitions[i] = types[i] ?? throw new ArgumentException("Damage types must not be null.", nameof(types));

            // Shield walk order: by shield order, then by type index.
            var order = new int[definitions.Length];
            for (var i = 0; i < order.Length; ++i) order[i] = i;
            Array.Sort(order, (a, b) => definitions[a].ShieldOrder != definitions[b].ShieldOrder
                ? definitions[a].ShieldOrder.CompareTo(definitions[b].ShieldOrder) : a.CompareTo(b));

            _types = definitions;
            ShieldWalk = order;
            RammingDamageType = rammingDamageType;
        }

        public int Count => _types.Length;
        public DamageTypeDefinition this[int type] => _types[type];

        // Legacy DefaultCollisionBehaviour: ramming deals this type.
        public int RammingDamageType { get; }

        // Type indices in shield order. A group is a run of equal shield orders.
        internal int[] ShieldWalk { get; }

        public bool Contains(int type) => type >= 0 && type < _types.Length;

        // A definition checks the index range only; BattleSetup checks it against the table.
        internal static void ValidateIndex(int type, string name)
        {
            if (type < 0 || type >= MaxTypes)
                throw new ArgumentOutOfRangeException(name, $"Damage type must be from 0 to {MaxTypes - 1}.");
        }

        private readonly DamageTypeDefinition[] _types;
    }
}

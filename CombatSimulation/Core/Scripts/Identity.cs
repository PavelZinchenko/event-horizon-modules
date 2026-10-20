using System;

namespace CombatSimulation.Core
{
    public readonly struct BattleId : IEquatable<BattleId>, IComparable<BattleId>
    {
        public readonly ulong Value;

        public BattleId(ulong value) => Value = value;
        public bool Equals(BattleId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is BattleId other && Equals(other);
        public override int GetHashCode() => unchecked((int)(Value ^ (Value >> 32)));
        public int CompareTo(BattleId other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(BattleId left, BattleId right) => left.Equals(right);
        public static bool operator !=(BattleId left, BattleId right) => !left.Equals(right);
    }

    public readonly struct RosterId : IEquatable<RosterId>, IComparable<RosterId>
    {
        public readonly uint Value;

        public RosterId(uint value) => Value = value;
        public bool Equals(RosterId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is RosterId other && Equals(other);
        public override int GetHashCode() => unchecked((int)Value);
        public int CompareTo(RosterId other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(RosterId left, RosterId right) => left.Equals(right);
        public static bool operator !=(RosterId left, RosterId right) => !left.Equals(right);
    }

    // A spawn-derived public identity, never an array index or a physics handle.
    public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
    {
        public readonly ulong Value;

        public EntityId(ulong value) => Value = value;
        public bool IsNull => Value == 0;
        public bool Equals(EntityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is EntityId other && Equals(other);
        public override int GetHashCode() => unchecked((int)(Value ^ (Value >> 32)));
        public int CompareTo(EntityId other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);
        public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
    }
}

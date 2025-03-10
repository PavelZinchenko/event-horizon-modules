using System.Collections.Generic;
using System.Linq;

namespace Utilites.Collections
{
    public class SimpleInventory<T> : IInventory<T>
    {
        private readonly Dictionary<T, int> _items = new();

        public bool IsDirty { get; set; }
        public IReadOnlyCollection<T> Items => _items.Keys;
        public IEnumerable<InventoryItem<T>> AvailableItems => _items.Where(IsAvailable).Select(InventoryItem<T>.Create);

        public int GetQuantity(T item) => _items.TryGetValue(item, out var quantity) ? quantity : 0;

        public void Add(T item, int quantity = 1)
        {
            if (quantity <= 0) return;
            if (_items.TryGetValue(item, out int oldQuantity))
                _items[item] = oldQuantity + quantity;
            else
                _items.Add(item, quantity);

            OnDataChanged();
        }

        public int Remove(T item, int quantity = 1)
        {
            if (quantity <= 0) return 0;
            if (!_items.TryGetValue(item, out int oldQuantity))
                return 0;

            if (oldQuantity <= quantity)
            {
                _items.Remove(item);
                quantity = oldQuantity;
            }
            else
            {
                _items[item] = oldQuantity - quantity;
            }

            OnDataChanged();
            return quantity;
        }

        public void Clear()
        {
            _items.Clear();
            OnDataChanged();
        }

        private void OnDataChanged()
        {
            IsDirty = true;
        }

        private static bool IsAvailable(KeyValuePair<T, int> item) => item.Value > 0;
    }
}

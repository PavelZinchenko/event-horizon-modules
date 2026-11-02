using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Maps each live public EntityId to its storage handle (Identity, item 5).
    internal sealed class EntityRegistry
    {
        private readonly Dictionary<EntityId, EntityHandle> _handles = new Dictionary<EntityId, EntityHandle>();

        public int Count => _handles.Count;

        public bool Contains(EntityId id) => _handles.ContainsKey(id);

        // Returns false when the ID is already alive; the caller decides how to report the defect.
        public bool TryAdd(EntityId id, EntityHandle handle)
        {
            if (_handles.ContainsKey(id)) return false;
            _handles.Add(id, handle);
            return true;
        }

        public bool TryGet(EntityId id, out EntityHandle handle) => _handles.TryGetValue(id, out handle);

        public void Remove(EntityId id) => _handles.Remove(id);
    }
}

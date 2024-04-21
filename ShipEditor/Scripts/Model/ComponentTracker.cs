using System.Collections.Generic;
using System.Linq;
using GameDatabase.DataModel;
using GameDatabase.Extensions;
using GameDatabase.Enums;
using Constructor.Ships;
using Constructor;
using Constructor.Extensions;

namespace ShipEditor.Model
{
	public interface IComponentTracker
	{
		void OnComponentAdded(Component component);
		void OnComponentRemoved(Component component);
		void OnKeyBindingChanged(Component component, int keyBinding);
	}

	public interface ICompatibilityChecker
	{
		bool IsCompatible(Satellite satellite);
		bool IsCompatible(Component component);
		bool UniqueComponentInstalled(Component component);
		int GetDefaultKey(Component component);
	}

	public class ComponentTracker : ICompatibilityChecker, IComponentTracker
	{
		private const int _actionKeyCount = 6;

		private readonly IShip _ship;
        private readonly Inventory<Component> _limitedComponents = new();
		private readonly Inventory<string> _uniqueComponents = new();
		private readonly Dictionary<Component, int> _keyBindings = new();

		public ComponentTracker(IShip ship)
		{
			_ship = ship;
		}

		public bool UniqueComponentInstalled(Component component)
		{
            var maxAmount = component.Restrictions.MaxComponentAmount;
            var key = component.GetUniqueKey();

            if (maxAmount == 0)
                return !string.IsNullOrEmpty(key) && _uniqueComponents.Quantity(key) > 0;

            if (string.IsNullOrEmpty(key))
                return _limitedComponents.Quantity(component) >= maxAmount;
            else
                return _uniqueComponents.Quantity(key) >= maxAmount;
        }

		public bool IsCompatible(Satellite satellite)
		{
			return _ship.IsSuitableSatelliteSize(satellite);
		}

		public bool IsCompatible(Component component)
		{
			if (!Constructor.Component.CompatibilityChecker.IsCompatibleComponent(component, _ship.Model))
				return false;

			if (UniqueComponentInstalled(component)) 
				return false;

			return true;
		}

		public int GetDefaultKey(Component component)
		{
			if (component.GetActivationType() == ActivationType.None)
				return 0;

			if (_keyBindings.TryGetValue(component, out var key))
				return key;

			var usedKeys = _keyBindings.Values.ToHashSet();
			for (int i = 0; i < _actionKeyCount; ++i)
				if (!usedKeys.Contains(i)) return i;

			return 0;
		}

		public void OnComponentAdded(Component component)
		{
            var maxAmount = component.Restrictions.MaxComponentAmount;
            var key = component.GetUniqueKey();

            if (maxAmount == 0)
            {
                if (!string.IsNullOrEmpty(key) && _uniqueComponents.Add(key) > 1)
                    GameDiagnostics.Trace.LogError($"A unique component with key '{key}' is already installed");
            }
            else if (!string.IsNullOrEmpty(key))
            {
                var quantity = _uniqueComponents.Add(key);
                if (quantity > maxAmount)
                    GameDiagnostics.Trace.LogError($"Too many components with key '{key}' were installed: ({quantity}/{maxAmount})");
            }
            else
            {
                var quantity = _limitedComponents.Add(component);
                if (quantity > maxAmount)
                    GameDiagnostics.Trace.LogError($"Too many {component.Name} were installed: ({quantity}/{maxAmount})");
            }
        }

		public void OnComponentRemoved(Component component)
		{
            var maxAmount = component.Restrictions.MaxComponentAmount;
            var key = component.GetUniqueKey();

            if (!string.IsNullOrEmpty(key))
                _uniqueComponents.Remove(key);
            else if (maxAmount > 0)
                _limitedComponents.Remove(component);

            _keyBindings.Remove(component);
		}

		public void OnKeyBindingChanged(Component component, int keyBinding)
		{
			if (component.GetActivationType() == ActivationType.None) return;
			_keyBindings[component] = keyBinding;
		}

        private class Inventory<T>
        {
            private readonly Dictionary<T, int> _items = new();

            public int Quantity(T key) => _items.TryGetValue(key, out var quantity) ? quantity : 0;

            public int Add(T key)
            {
                var quantity = Quantity(key) + 1;
                _items[key] = quantity;
                return quantity;
            }

            public bool Remove(T key)
            {
                var quantity = Quantity(key);
                if (quantity == 0) 
                    return false;

                if (quantity == 1)
                {
                    _items.Remove(key);
                    return true;
                }

                _items[key] = quantity - 1;
                return true;
            }
        }
    }
}

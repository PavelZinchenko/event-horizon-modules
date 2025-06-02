using UnityEngine;
using Constructor;
using Gui.ComponentList;
using UnityEngine.Events;
using Constructor.Ships;
using ShipEditor.Model;
using Zenject;
using GameDatabase;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace ShipEditor.UI
{
    public class ComponentsPanel : MonoBehaviour
    {
        [Inject] private readonly IShipEditorModel _shipEditor;
        [Inject] private readonly IDatabase _database;

        [SerializeField] private ComponentContentFiller _contentFiller;
        [SerializeField] private ListScrollRect _componentList;
        [SerializeField] private GameObject _noItemsText;
        [SerializeField] private UnityEngine.UI.Button _backButton;

        [SerializeField] private UnityEvent<int> _satelliteGroupSelected;
        [SerializeField] private UnityEvent<ComponentInfo> _componentSelected;

		private RootNode _rootNode;
		private List<SatellitesNode> _satelliteNodes = new();
		private IComponentTreeNode _selectedNode;

		private void Start()
		{
			var components = _shipEditor.Inventory.Components;
			_rootNode = new RootNode(new ComponentQuantityProvider(_shipEditor.Inventory), _database);
			_rootNode.Assign(components);
			_rootNode.IsVisible = true;

            _satelliteNodes.Clear();
            UpdateSatelliteGroups();

            _selectedNode = _rootNode;
            _noItemsText.SetActive(components.Count == 0);
            _contentFiller.InitializeItems(_selectedNode);
			_componentList.RefreshContent();
		}

		private void OnEnable()
		{
			_shipEditor.Events.ShipChanged += OnShipSelected;
			_shipEditor.Events.SatelliteChanged += OnSatelliteChanged;
			_shipEditor.Events.ComponentAdded += OnComponentAddedOrRemoved;
			_shipEditor.Events.ComponentRemoved += OnComponentAddedOrRemoved;
			_shipEditor.Events.MultipleComponentsChanged += RefreshList;
		}

		private void OnDisable()
		{
			_shipEditor.Events.ShipChanged -= OnShipSelected;
			_shipEditor.Events.SatelliteChanged -= OnSatelliteChanged;
			_shipEditor.Events.ComponentAdded -= OnComponentAddedOrRemoved;
			_shipEditor.Events.ComponentRemoved -= OnComponentAddedOrRemoved;
			_shipEditor.Events.MultipleComponentsChanged -= RefreshList;
		}

		private void OnComponentAddedOrRemoved(IComponentModel component) => RefreshList();

		public bool Visible
		{
			get => gameObject.activeSelf;
			set 
			{
				if (Visible == value) return;
				gameObject.SetActive(value);
				if (value) RefreshList();
			}
		}

		public void GoBack()
		{
			_selectedNode = _selectedNode.GetVisibleParent() ?? _rootNode;
			RefreshList();
		}

		public void ShowAll()
		{
			_selectedNode = _rootNode;
			RefreshList();
		}

		public void OnItemSelected(ComponentListItem item)
		{
			_componentSelected?.Invoke(item.Component);
		}

		public void OnGroupSelected(GroupListItem item)
        {
            for (int i = 0; i < _satelliteNodes.Count; ++i)
                if (item.Node == _satelliteNodes[i])
                {
                    _satelliteGroupSelected?.Invoke(i);
                    return;
                }

            if (_selectedNode == item.Node)
                _selectedNode = _selectedNode.GetVisibleParent() ?? _rootNode;
            else
                _selectedNode = item.Node;

            RefreshList();
        }

        private void RefreshList()
        {
			if (_rootNode == null) return;
            _rootNode.Assign(_shipEditor.Inventory.Components);

			while (_selectedNode != _rootNode && _selectedNode.ItemCount == 0)
                _selectedNode = _selectedNode.GetVisibleParent() ?? _rootNode;

			UpdateSatelliteGroups();
			_contentFiller.InitializeItems(_selectedNode);
            _componentList.RefreshContent();
            _backButton.interactable = _selectedNode != _rootNode;
        }

        private void OnShipSelected(IShip ship) => UpdateSatelliteGroups();

		private void OnSatelliteChanged(int mountPoint) => UpdateSatelliteGroups();

        private void CreateSatelliteNodes()
        {
            int mountPointCount = _shipEditor.Ship.Model.Satellites.Count;
            for (int i = _satelliteNodes.Count; i < mountPointCount; ++i)
            {
                var node = new SatellitesNode(_rootNode, i);
                _satelliteNodes.Add(node);
                _rootNode.AddNode(node, true);
            }
        }

        private void UpdateSatelliteGroups()
		{
            int mountPointCount = _shipEditor.Ship.Model.Satellites.Count;
            if (mountPointCount > _satelliteNodes.Count)
                CreateSatelliteNodes();

            var haveSatellites = _shipEditor.Inventory.Satellites.Count > 0 || _shipEditor.Inventory.SatelliteBuilds.Count > 0;
            for (int i = 0; i < _satelliteNodes.Count; ++i)
            {
                var node = _satelliteNodes[i];
                node.IsVisible = i < mountPointCount && (haveSatellites || _shipEditor.HasSatellite(i));
            }
		}

        private class ComponentQuantityProvider : IComponentQuantityProvider
        {
            public ComponentQuantityProvider(Context.IInventoryProvider inventoryProvider)
            {
                _inventoryProvider = inventoryProvider;
            }

            public int GetQuantity(ComponentInfo component)
            {
                return _inventoryProvider.GetQuantity(component);
            }

            private readonly Context.IInventoryProvider _inventoryProvider;
        }
    }
}

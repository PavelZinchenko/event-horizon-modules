using UnityEngine;
using ShipEditor.Model;
using Zenject;
using UnityEngine.UI;
using Constructor.Ships;
using Services.Resources;
using Services.Localization;
using Services.Gui;
using Gui.Utils;

namespace ShipEditor.UI
{
    public class BuildsPanel : MonoBehaviour
    {
        [Inject] private readonly ILocalization _localization;
        [Inject] private readonly IResourceLocator _resourceLocator;
        [Inject] private readonly IShipEditorModel _shipEditor;
        [Inject] private readonly IGuiManager _guiManager;
        [Inject] private readonly CommandList _commandList;

        [SerializeField] private LayoutGroup _itemsLayoutGroup;
        [SerializeField] private Button _saveButton;
        [SerializeField] private Button _loadButton;
        [SerializeField] private Button _deleteButton;
        [SerializeField] private InputField _newPresetName;

        private IShipPreset _selectedItem;

        private void OnEnable()
        {
            _shipEditor.Events.ShipChanged += OnShipChanged;
            UpdateContent();
            UpdateButtons();
        }

        private void OnDisable()
        {
            _shipEditor.Events.ShipChanged -= OnShipChanged;
        }

        public void OnNewPresetSelected(bool selected)
        {
            if (!selected) return;
            _selectedItem = null;
            UpdateButtons();
        }

        public void OnPresetSelected(IShipPreset preset)
		{
            _selectedItem = preset;
            UpdateButtons();
		}

		public bool Visible
		{
			get => gameObject.activeSelf;
			set => gameObject.SetActive(value);
		}

        public void SavePreset()
        {
            if (_selectedItem == null)
            {
                var preset = _shipEditor.Presets.Create(_shipEditor.Ship.Model.OriginalShip);
                _shipEditor.SaveShipToPreset(preset);
                preset.Name = _newPresetName.text;
                UpdateContent();
            }
            else
            {
                _guiManager.ShowConfirmationDialog(_localization.GetString("$OverwritePresetConfirmation"),
                    () => _shipEditor.SaveShipToPreset(_selectedItem));
            }
        }

        public void LoadPreset()
        {
            LoadPreset(_selectedItem);
            _commandList.Clear();
        }

        public void DeletePreset()
        {
            _guiManager.ShowConfirmationDialog(_localization.GetString("$DeletePresetConfirmation"), () =>
            {
                _shipEditor.Presets.Delete(_selectedItem);
                _selectedItem = null;
                UpdateContent();
                UpdateButtons();
            });
        }

        private void LoadPreset(IShipPreset preset)
        {
            if (!_shipEditor.LoadShipFromPreset(preset))
                _guiManager.ShowMessage(_localization.GetString("$PartiallyLoadedPreset"));
        }

        private void OnShipChanged(IShip ship)
        {
            _selectedItem = null;
            UpdateContent();
        }

        private void UpdateButtons()
        {
            _loadButton.gameObject.SetActive(_selectedItem != null);
            _deleteButton.gameObject.SetActive(_selectedItem != null);
        }

        private void UpdateContent()
        {
            var presets = _shipEditor.Presets.GetPresets(_shipEditor.Ship.Model.OriginalShip);
            _itemsLayoutGroup.transform.InitializeElements<ShipPresetItem, IShipPreset>(presets, UpdatePresets);
        }

        private void UpdatePresets(ShipPresetItem item, IShipPreset preset)
        {
            item.Initialize(preset, _resourceLocator, _localization);
        }
    }
}

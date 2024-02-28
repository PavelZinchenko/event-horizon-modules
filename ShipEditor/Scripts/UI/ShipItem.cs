using Constructor.Ships;
using Services.Localization;
using Services.Resources;
using UnityEngine;
using UnityEngine.UI;

namespace ShipEditor.UI
{
    public class ShipItem : MonoBehaviour
    {
        [SerializeField] private Image _icon;
        [SerializeField] private Text _name;
		[SerializeField] private Text _classText;
		[SerializeField] private Text _levelText;

		public void Initialize(IShip ship, IResourceLocator resourceLocator, ILocalization localization)
        {
            Ship = ship;
            var icon = resourceLocator.GetSprite(ship.Model.IconImage);
            if (icon != null)
            {
                _icon.sprite = icon;
                _icon.rectTransform.localScale = 1.4f * ship.Model.IconScale * Vector3.one;
            }
            else
            {
                _icon.sprite = resourceLocator.GetSprite(ship.Model.ModelImage);
                _icon.rectTransform.localScale = Vector3.one;
            }

            _icon.color = ship.ColorScheme.HsvColor;

			_name.text = localization.GetString(ship.Name);
			_classText.text = ship.Model.SizeClass.ToString(localization);
			var level = ship.Experience.Level;
			_levelText.text = level > 0 ? level.ToString() : "0";
		}

		public IShip Ship { get; private set; }
    }
}

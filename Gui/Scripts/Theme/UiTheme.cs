using UnityEngine;

namespace Gui.Theme
{
    public enum ThemeColor
    {
        Default = 0,

        Window = 1,
        ScrollBar = 2,
        Icon = 3,
        InnerPanel = 5,
        Selection = 6,

        Button = 50,
        ButtonFocus = 51,
        ButtonText = 52,
        ButtonIcon = 53,

        Text = 100,
        HeaderText = 101,
        PaleText = 102,
        BrightText = 103,
    }

    public enum ThemeFont
    {
        Default = 0,

        Font1 = 1,
        Font2 = 2,
        Font3 = 3,
    }

    public enum ThemeFontSize
    {
        Default = 0,

        ButtonText = 1,
        MenuButtonText = 2,
        Text = 3,
        HeaderText = 5,
        LargeText = 8,
        CompactText = 6,
        SmallText = 4,
        TinyText = 7,
    }

    [CreateAssetMenu(fileName = "UiTheme", menuName = "AppConfig/UiTheme")]
    public class UiTheme : ScriptableObject
    {
        private static UiTheme _current;
        private Font _defaultFont;

        [SerializeField] private Color _windowColor = new Color32(80,192,255,255);
        [SerializeField] private Color _scrollBarColor = new Color32(80, 192, 255, 192);
        [SerializeField] private Color _iconColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _innerPanelColor = new Color32(80, 192, 255, 128);
        [SerializeField] private Color _selectionColor = new Color32(128, 255, 255, 255);

        [SerializeField] private Color _buttonColor = new Color32(80, 192, 255, 255);
        [SerializeField] private Color _buttonFocusColor = new Color32(80, 192, 255, 64);
        [SerializeField] private Color _buttonTextColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _buttonIconColor = new Color32(80, 192, 255, 255);

        [SerializeField] private Color _textColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _headerTextColor = new Color32(255, 255, 192, 255);
        [SerializeField] private Color _paleTextColor = new Color32(192, 192, 192, 255);
        [SerializeField] private Color _brightTextColor = new Color32(255, 255, 255, 255);

        [SerializeField] private Color _itemLowQualityColor = new Color32(192, 192, 192, 255);
        [SerializeField] private Color _itemCommonQualityColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _itemMediumQualityColor = new Color32(128, 255, 128, 255);
        [SerializeField] private Color _itemHighQualityColor = new Color32(240, 159, 255, 255);
        [SerializeField] private Color _itemPerfectQualityColor = new Color32(255, 223, 81, 255);

        [SerializeField] private int _buttonTextFontSize = 24;
        [SerializeField] private int _menuButtonTextFontSize = 38;
        [SerializeField] private int _headerFontSize = 26;
        [SerializeField] private int _normalTextFontSize = 24;
        [SerializeField] private int _smallTextFontSize = 20;
        [SerializeField] private int _compactTextFontSize = 22;
        [SerializeField] private int _tinyTextFontSize = 18;
        [SerializeField] private int _largeTextFontSize = 32;

        [SerializeField] private FontInfo _font1;
        [SerializeField] private FontInfo _font2;
        [SerializeField] private FontInfo _font3;

        public static UiTheme Current
        {
            get
            {
                if (_current == null)
                    _current = CreateInstance<UiTheme>();
                return _current;
            }
            set
            {
                _current = value;
            }
        }

        public Color GetColor(ThemeColor themeColor)
        {
            switch (themeColor)
            {
                case ThemeColor.Window: return _windowColor;
                case ThemeColor.ScrollBar: return _scrollBarColor;
                case ThemeColor.Icon: return _iconColor;
                case ThemeColor.InnerPanel: return _innerPanelColor;
                case ThemeColor.Selection: return _selectionColor;

                case ThemeColor.Button: return _buttonColor;
                case ThemeColor.ButtonFocus: return _buttonFocusColor;
                case ThemeColor.ButtonText: return _buttonTextColor;
                case ThemeColor.ButtonIcon: return _buttonIconColor;

                case ThemeColor.Text: return _textColor;
                case ThemeColor.HeaderText: return _headerTextColor;
                case ThemeColor.PaleText: return _paleTextColor;
                case ThemeColor.BrightText: return _brightTextColor;
            }

            GameDiagnostics.Debug.LogError($"Invalid color type {themeColor}");
            return Color.white;
        }

        public int GetFontSize(ThemeFontSize themeFontSize)
        {
            switch (themeFontSize)
            {
                case ThemeFontSize.ButtonText: return _buttonTextFontSize;
                case ThemeFontSize.MenuButtonText: return _menuButtonTextFontSize;
                case ThemeFontSize.Text: return _normalTextFontSize;
                case ThemeFontSize.SmallText: return _smallTextFontSize;
                case ThemeFontSize.HeaderText: return _headerFontSize;
                case ThemeFontSize.CompactText: return _compactTextFontSize;
                case ThemeFontSize.TinyText: return _tinyTextFontSize;
                case ThemeFontSize.LargeText: return _largeTextFontSize;
            }

            GameDiagnostics.Debug.LogError($"Invalid element type {themeFontSize}");
            return 24;
        }

        public FontInfo GetFont(ThemeFont themeFont)
        {
            FontInfo fontInfo = new();
            switch (themeFont)
            {
                case ThemeFont.Font1: fontInfo = _font1; break;
                case ThemeFont.Font2: fontInfo = _font2; break;
                case ThemeFont.Font3: fontInfo = _font3; break;
            }

            if (fontInfo.Font == null)
            {
                GameDiagnostics.Debug.LogError($"Invalid font type {themeFont}");

                if (_defaultFont == null) 
                    _defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

                fontInfo.Font = _defaultFont;
                fontInfo.SizeMultiplier = 1.0f;
            }

            return fontInfo;
        }

        public Color GetQualityColor(Economy.ItemType.ItemQuality quality)
        {
            switch (quality)
            {
                case Economy.ItemType.ItemQuality.Low: return _itemLowQualityColor;
                case Economy.ItemType.ItemQuality.Common: return _itemCommonQualityColor;
                case Economy.ItemType.ItemQuality.Medium: return _itemMediumQualityColor;
                case Economy.ItemType.ItemQuality.High: return _itemHighQualityColor;
                case Economy.ItemType.ItemQuality.Perfect: return _itemPerfectQualityColor;
            }

            GameDiagnostics.Debug.LogError($"Invalid quality value {quality}");
            return _itemCommonQualityColor;
        }

        public void Import(GameDatabase.DataModel.UiSettings settings)
        {
            _windowColor = settings.WindowColor;
            _scrollBarColor = settings.ScrollBarColor;
            _iconColor = settings.IconColor;
            _innerPanelColor = settings.InnerPanelColor;
            _selectionColor = settings.SelectionColor;

            _buttonColor = settings.ButtonColor;
            _buttonFocusColor = settings.ButtonFocusColor;
            _buttonTextColor = settings.ButtonTextColor;
            _buttonIconColor = settings.ButtonIconColor;

            _textColor = settings.TextColor;
            _headerTextColor = settings.HeaderTextColor;
            _paleTextColor = settings.PaleTextColor;
            _brightTextColor = settings.BrightTextColor;

            _itemLowQualityColor = settings.LowQualityItemColor;
            _itemCommonQualityColor = settings.CommonQualityItemColor;
            _itemMediumQualityColor = settings.MediumQualityItemColor;
            _itemHighQualityColor = settings.HighQualityItemColor;
            _itemPerfectQualityColor = settings.PerfectQualityItemColor;
        }

        [System.Serializable]
        public struct FontInfo
        {
            public Font Font;
            public float SizeMultiplier;
        }
    }
}

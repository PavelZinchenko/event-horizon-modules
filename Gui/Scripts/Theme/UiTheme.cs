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

        WarningButton = 60,
        WarningButtonFocus = 61,
        WarningButtonText = 62,
        WarningButtonIcon = 63,

        Text = 100,
        HeaderText = 101,
        PaleText = 102,
        BrightText = 103,
        ErrorText = 104,
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

        ButtonText24 = 1,
        NormalText24 = 3,
        TitleText26 = 5,
        LargeText38 = 2,
        LargeText32 = 8,
        CompactText22 = 6,
        SmallText20 = 4,
        SmallText18 = 7,
    }

    [CreateAssetMenu(fileName = "UiTheme", menuName = "AppConfig/UiTheme")]
    public class UiTheme : ScriptableObject
    {
        private static UiTheme _current;
        private Font _defaultFont;

        [SerializeField] private Color _windowColor = new Color32(80,192,255,255);
        [SerializeField] private Color _scrollBarColor = new Color32(80, 192, 255, 192);
        [SerializeField] private Color _iconColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _selectionColor = new Color32(128, 255, 255, 255);

        [SerializeField] private Color _buttonColor = new Color32(80, 192, 255, 255);
        [SerializeField] private Color _buttonFocusColor = new Color32(80, 192, 255, 64);
        [SerializeField] private Color _buttonTextColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _buttonIconColor = new Color32(80, 192, 255, 255);

        [SerializeField] private Color _warningButtonColor = new Color32(255, 128, 80, 255);
        [SerializeField] private Color _warningButtonFocusColor = new Color32(255, 192, 80, 32);
        [SerializeField] private Color _warningButtonTextColor = new Color32(255, 255, 192, 255);
        [SerializeField] private Color _warningButtonIconColor = new Color32(255, 255, 192, 255);

        [SerializeField] private Color _textColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _errorTextColor = new Color32(255, 192, 0, 255);
        [SerializeField] private Color _headerTextColor = new Color32(255, 255, 192, 255);
        [SerializeField] private Color _paleTextColor = new Color32(255, 255, 255, 160);
        [SerializeField] private Color _brightTextColor = new Color32(255, 255, 255, 255);

        [SerializeField] private Color _itemLowQualityColor = new Color32(192, 192, 192, 255);
        [SerializeField] private Color _itemCommonQualityColor = new Color32(128, 255, 255, 255);
        [SerializeField] private Color _itemMediumQualityColor = new Color32(128, 255, 128, 255);
        [SerializeField] private Color _itemHighQualityColor = new Color32(240, 159, 255, 255);
        [SerializeField] private Color _itemPerfectQualityColor = new Color32(255, 223, 81, 255);

        [SerializeField] private int _smallText_18 = 18;
        [SerializeField] private int _smallText_20 = 20;
        [SerializeField] private int _compactText_22 = 22;
        [SerializeField] private int _buttonText_24 = 24;
        [SerializeField] private int _normalText_24 = 24;
        [SerializeField] private int _titleText_26 = 26;
        [SerializeField] private int _largeText_32 = 32;
        [SerializeField] private int _largeText_38 = 38;

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
                case ThemeColor.InnerPanel: return _windowColor.Transparent(0.5f);
                case ThemeColor.Selection: return _selectionColor;

                case ThemeColor.Button: return _buttonColor;
                case ThemeColor.ButtonFocus: return _buttonFocusColor;
                case ThemeColor.ButtonText: return _buttonTextColor;
                case ThemeColor.ButtonIcon: return _buttonIconColor;

                case ThemeColor.WarningButton: return _warningButtonColor;
                case ThemeColor.WarningButtonFocus: return _warningButtonFocusColor;
                case ThemeColor.WarningButtonText: return _warningButtonTextColor;
                case ThemeColor.WarningButtonIcon: return _warningButtonIconColor;

                case ThemeColor.Text: return _textColor;
                case ThemeColor.HeaderText: return _headerTextColor;
                case ThemeColor.PaleText: return _paleTextColor;
                case ThemeColor.BrightText: return _brightTextColor;
                case ThemeColor.ErrorText: return _errorTextColor;
            }

            throw new System.InvalidOperationException($"Invalid color type {themeColor}");
        }

        public int GetFontSize(ThemeFontSize themeFontSize)
        {
            switch (themeFontSize)
            {
                case ThemeFontSize.SmallText18: return _smallText_18;
                case ThemeFontSize.SmallText20: return _smallText_20;
                case ThemeFontSize.CompactText22: return _compactText_22;
                case ThemeFontSize.ButtonText24: return _buttonText_24;
                case ThemeFontSize.NormalText24: return _normalText_24;
                case ThemeFontSize.TitleText26: return _titleText_26;
                case ThemeFontSize.LargeText32: return _largeText_32;
                case ThemeFontSize.LargeText38: return _largeText_38;
            }

            throw new System.InvalidOperationException($"Invalid element type {themeFontSize}");
        }

        public FontInfo GetFont(ThemeFont themeFont)
        {
            FontInfo fontInfo = new();
            switch (themeFont)
            {
                case ThemeFont.Font1: fontInfo = _font1; break;
                case ThemeFont.Font2: fontInfo = _font2; break;
                case ThemeFont.Font3: fontInfo = _font3; break;
                default:
                    throw new System.InvalidOperationException($"Invalid font type {themeFont}");
            }

            if (fontInfo.Font == null)
            {
                GameDiagnostics.Debug.LogError($"Font {themeFont} is not defined. Replaced with the built-in font");

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

            throw new System.InvalidOperationException($"Invalid quality value {quality}");
        }

        public void Import(GameDatabase.DataModel.UiSettings settings)
        {
            _windowColor = settings.WindowColor;
            _scrollBarColor = settings.ScrollBarColor;
            _iconColor = settings.IconColor;
            _selectionColor = settings.SelectionColor;

            _buttonColor = settings.ButtonColor;
            _buttonFocusColor = settings.ButtonFocusColor;
            _buttonTextColor = settings.ButtonTextColor;
            _buttonIconColor = settings.ButtonIconColor;

            _warningButtonColor = settings.WarningButtonColor;
            _warningButtonFocusColor = settings.WarningButtonFocusColor;
            _warningButtonTextColor = settings.WarningButtonTextColor;
            _warningButtonIconColor = settings.WarningButtonIconColor;
            _errorTextColor = settings.ErrorTextColor;

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

    public static class ColorExtension
    {
        public static Color Transparent(this Color color, float alpha) => new Color(color.r, color.g, color.b, color.a * alpha);
    }
}
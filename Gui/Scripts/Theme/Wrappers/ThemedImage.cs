using UnityEngine;
using UnityEngine.UI;

namespace Gui.Theme.Wrappers
{
    public class ThemedImage : Image
    {
        [SerializeField] private ThemeColor _themeColor;

        protected override void Start()
        {
            base.Start();

#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying) return;
#endif

            try
            {

                if (_themeColor != ThemeColor.Default)
                    color = UiTheme.Current.GetColor(_themeColor);
            }
            catch (System.Exception e)
            {
                GameDiagnostics.Debug.LogException(e);
            }
        }
    }
}

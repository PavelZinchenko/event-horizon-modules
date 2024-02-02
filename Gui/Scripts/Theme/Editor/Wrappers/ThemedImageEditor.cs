using UnityEditor;
using UnityEditor.UI;

namespace Gui.Theme.Wrappers
{
    [CustomEditor(typeof(ThemedImage))]
    [CanEditMultipleObjects]
    public class ThemedImageEditor : ImageEditor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_themeColor"));
            serializedObject.ApplyModifiedProperties();

            base.OnInspectorGUI();
        }
    }
}

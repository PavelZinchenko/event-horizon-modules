using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CombatView
{
    // Loads every fixed prefab of CombatViewAssets and the ship HSV material, and reports the missing ones, missing
    // scripts and missing references (Frontier's TODO/shared_views.md, V1). Each game supplies these assets, so run it in
    // each game. Effect names from the database are not checked.
    public static class CheckViewPrefabs
    {
        [MenuItem("Tools/Combat/Check View Prefabs")]
        public static void Run()
        {
            var problems = new StringBuilder();
            var prefabs = 0;
            foreach (var field in typeof(CombatViewAssets).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.IsLiteral || field.FieldType != typeof(string) || !field.Name.EndsWith("Prefab"))
                    continue;

                ++prefabs;
                var path = (string)field.GetRawConstantValue();
                var prefab = CombatViewAssets.LoadPrefab(path);
                if (prefab)
                    CheckReferences(prefab, path, problems);
                else
                    problems.AppendLine($"{path}: no prefab at Resources/{CombatViewAssets.ResourceRoot}{path}");
            }

            if (!Resources.Load<Material>(CombatViewAssets.ShipHsvMaterial))
                problems.AppendLine($"no material at Resources/{CombatViewAssets.ShipHsvMaterial}");

            var count = problems.ToString().Split('\n').Length - 1;
            var report = $"CheckViewPrefabs: {prefabs} prefabs, {count} problems\n{problems}";
            if (count > 0) Debug.LogError(report);
            else Debug.Log(report);
        }

        private static void CheckReferences(GameObject prefab, string path, StringBuilder problems)
        {
            foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
            {
                var name = transform == prefab.transform
                    ? path
                    : $"{path}/{AnimationUtility.CalculateTransformPath(transform, prefab.transform)}";
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (!component)
                    {
                        problems.AppendLine($"{name}: missing script");
                        continue;
                    }

                    var property = new SerializedObject(component).GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType == SerializedPropertyType.ObjectReference && !property.objectReferenceValue &&
                            property.objectReferenceInstanceIDValue != 0)
                            problems.AppendLine($"{name}: {component.GetType().Name}.{property.propertyPath} is missing");
                    }
                }
            }
        }
    }
}

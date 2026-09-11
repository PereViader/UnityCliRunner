using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp
{
    internal static class UnityResultFormatter
    {
        public static string FormatResult(object result, bool isVoidStatement = false, bool prettyPrint = true)
        {
            if (result == null)
            {
                return isVoidStatement ? null : "null";
            }

            if (result is bool b)
            {
                return b ? "true" : "false";
            }

            var type = result.GetType();

            if (type.IsPrimitive || result is string || result is decimal || type.IsEnum)
            {
                return result.ToString();
            }

            if (result is UnityEngine.Object unityObj && unityObj == null)
            {
                if (result is GameObject) return "null (GameObject)";
                if (result is Transform) return "null (Transform)";
                if (result is Component) return "null (Component)";
                return $"null ({result.GetType().Name})";
            }

            // GameObject formatting
            if (result is GameObject go)
            {
                if (go == null) return "null (GameObject)";
                var compNames = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name);

                return $"{go.name} (GameObject) [active: {go.activeSelf}, tag: \"{go.tag}\", layer: {go.layer}, components: {string.Join(", ", compNames)}]";
            }

            // Transform formatting (must precede Component formatting)
            if (result is Transform t)
            {
                if (t == null) return "null (Transform)";
                return $"Transform \"{t.name}\" [children: {t.childCount}, localPos: {t.localPosition}, localRot: {t.localEulerAngles}]";
            }

            // Component formatting
            if (result is Component comp)
            {
                if (comp == null) return "null (Component)";
                string goName = comp.gameObject != null ? comp.gameObject.name : "null";
                return $"{comp.GetType().Name} (Component on \"{goName}\")";
            }

            // ScriptableObject formatting
            if (result is ScriptableObject so)
            {
                if (so == null) return $"null ({result.GetType().Name})";
                return $"{so.GetType().Name} (ScriptableObject) [name: \"{so.name}\", assetPath: \"{AssetDatabase.GetAssetPath(so)}\"]";
            }

            // Scene formatting
            if (result is Scene scene)
            {
                return $"Scene \"{scene.name}\" [path: \"{scene.path}\", isLoaded: {scene.isLoaded}, isDirty: {scene.isDirty}, rootCount: {scene.rootCount}]";
            }

            // SerializedObject formatting
            if (result is SerializedObject serializedObj)
            {
                return $"SerializedObject on \"{serializedObj.targetObject?.name}\" ({serializedObj.targetObject?.GetType().Name})";
            }

            // SerializedProperty formatting
            if (result is SerializedProperty prop)
            {
                string propVal = FormatSerializedPropertyValue(prop);
                return propVal != null
                    ? $"SerializedProperty \"{prop.propertyPath}\" ({prop.propertyType}) = {propVal}"
                    : $"SerializedProperty \"{prop.propertyPath}\" ({prop.propertyType})";
            }

            // Collections / IEnumerable
            if (result is IEnumerable enumerable && !(result is string))
            {
                var items = new List<string>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    count++;
                    if (count > 100)
                    {
                        items.Add("... (truncated)");
                        break;
                    }
                    items.Add(FormatResult(item, false, prettyPrint));
                }
                return "[" + string.Join(", ", items) + "]";
            }

            // JsonUtility serialization fallback
            try
            {
                string json = JsonUtility.ToJson(result, prettyPrint);
                if (!string.IsNullOrEmpty(json) && json.Trim() != "{}")
                {
                    return json;
                }
            }
            catch { }

            return result.ToString();
        }

        public static string FormatSerializedPropertyValue(SerializedProperty prop)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        return prop.intValue.ToString();
                    case SerializedPropertyType.Boolean:
                        return prop.boolValue ? "true" : "false";
                    case SerializedPropertyType.Float:
                        return prop.floatValue.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.String:
                        return $"\"{prop.stringValue}\"";
                    case SerializedPropertyType.Color:
                        return prop.colorValue.ToString();
                    case SerializedPropertyType.ObjectReference:
                        var obj = prop.objectReferenceValue;
                        return obj != null ? $"\"{obj.name}\" ({obj.GetType().Name})" : "null";
                    case SerializedPropertyType.Enum:
                        return prop.enumNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length
                            ? prop.enumNames[prop.enumValueIndex]
                            : prop.enumValueIndex.ToString();
                    case SerializedPropertyType.Vector2:
                        return prop.vector2Value.ToString();
                    case SerializedPropertyType.Vector3:
                        return prop.vector3Value.ToString();
                    case SerializedPropertyType.Vector4:
                        return prop.vector4Value.ToString();
                    case SerializedPropertyType.Rect:
                        return prop.rectValue.ToString();
                    case SerializedPropertyType.ArraySize:
                        return prop.intValue.ToString();
                    case SerializedPropertyType.Character:
                        return ((char)prop.intValue).ToString();
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

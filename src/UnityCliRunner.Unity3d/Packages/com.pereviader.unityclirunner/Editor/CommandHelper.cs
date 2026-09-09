using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliRunner
{
    [InitializeOnLoad]
    internal static class CommandHelper
    {
        // Capture this once on Unity's main thread. User execute/eval code can
        // change Environment.CurrentDirectory, which must not redirect protocol
        // files to an arbitrary directory.
        private static string s_ProjectRoot;

        internal static string ProjectRoot
        {
            get
            {
                if (string.IsNullOrEmpty(s_ProjectRoot))
                {
                    EnsureInitialized();
                }
                return s_ProjectRoot;
            }
        }

        internal static void EnsureInitialized()
        {
            if (string.IsNullOrEmpty(s_ProjectRoot))
            {
                try
                {
                    s_ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"UnityCliRunner: Failed to initialize ProjectRoot: {ex}");
                }
            }
        }

        public static void RunActionAfterStoppingPlaymode(Action action)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.Log("UnityCliRunner: Stopping PlayMode before executing command...");
                EditorApplication.isPlaying = false;

                EditorApplication.CallbackFunction checkPlaymode = null;
                checkPlaymode = () =>
                {
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        EditorApplication.update -= checkPlaymode;
                        Debug.Log("UnityCliRunner: PlayMode stopped. Executing command...");
                        try
                        {
                            action();
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                };
                EditorApplication.update += checkPlaymode;
            }
            else
            {
                action();
            }
        }

        public static string[] SplitArguments(string commandLine)
        {
            var args = new List<string>();
            if (string.IsNullOrEmpty(commandLine))
            {
                return args.ToArray();
            }

            var current = new StringBuilder();
            bool inQuotes = false;
            bool isEscaped = false;
            bool inArg = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (isEscaped)
                {
                    switch (c)
                    {
                        case 'n': current.Append('\n'); break;
                        case 'r': current.Append('\r'); break;
                        case 't': current.Append('\t'); break;
                        case '"': current.Append('"'); break;
                        case '\\': current.Append('\\'); break;
                        default:
                            current.Append('\\');
                            current.Append(c);
                            break;
                    }
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    if (i + 1 < commandLine.Length)
                    {
                        char next = commandLine[i + 1];
                        if (next == '"' || next == '\\' || next == 'n' || next == 'r' || next == 't')
                        {
                            isEscaped = true;
                            inArg = true;
                        }
                        else
                        {
                            current.Append(c);
                            inArg = true;
                        }
                    }
                    else
                    {
                        current.Append(c);
                        inArg = true;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                    inArg = true;
                }
                else if (c == ' ' && !inQuotes)
                {
                    if (inArg)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                        inArg = false;
                    }
                }
                else
                {
                    current.Append(c);
                    inArg = true;
                }
            }

            if (inArg)
            {
                args.Add(current.ToString());
            }
            return args.ToArray();
        }

        public static object ConvertParameter(string rawArg, Type targetType)
        {
            if (targetType == typeof(string))
            {
                return rawArg;
            }

            if (rawArg == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            // Support Nullable<T>
            Type underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (rawArg.Equals("null", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(rawArg))
                {
                    return null;
                }
                return ConvertParameter(rawArg, underlying);
            }

            // Support Enums (by name or integral value)
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, rawArg, true);
            }

            // Support Guid
            if (targetType == typeof(Guid))
            {
                return Guid.Parse(rawArg);
            }

            // Support Primitive & Value Types
            if (targetType == typeof(int)) return int.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(rawArg);
            if (targetType == typeof(long)) return long.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint)) return uint.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(ulong)) return ulong.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(byte)) return byte.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(sbyte)) return sbyte.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(short)) return short.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(ushort)) return ushort.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(char)) return rawArg.Length > 0 ? rawArg[0] : '\0';
            if (targetType == typeof(decimal)) return decimal.Parse(rawArg, CultureInfo.InvariantCulture);

            // Fallback for complex structs/objects via JsonUtility
            return JsonUtility.FromJson(rawArg, targetType);
        }

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

        private static string FormatSerializedPropertyValue(SerializedProperty prop)
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

        public static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if (type != null)
                        return type;
                }
                catch { }
            }
            return null;
        }

        public static MethodInfo FindStaticMethod(Type type, string methodName, int paramCount)
        {
            return FindStaticMethod(type, methodName, paramCount, allowTrailingCancellationToken: true);
        }

        public static MethodInfo FindStaticMethod(Type type, string methodName, int paramCount, bool allowTrailingCancellationToken)
        {
            var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var candidatesWithCt = new List<MethodInfo>();
            var candidatesWithoutCt = new List<MethodInfo>();

            foreach (var m in methods)
            {
                if (m.Name != methodName) continue;

                var parameters = m.GetParameters();

                if (allowTrailingCancellationToken &&
                    parameters.Length == paramCount + 1 &&
                    parameters[paramCount].ParameterType == typeof(CancellationToken))
                {
                    candidatesWithCt.Add(m);
                }
                else if (parameters.Length == paramCount)
                {
                    candidatesWithoutCt.Add(m);
                }
            }

            if (candidatesWithCt.Count == 1)
            {
                return candidatesWithCt[0];
            }
            if (candidatesWithCt.Count > 1)
            {
                throw new AmbiguousMatchException($"Ambiguous match: multiple static methods named '{methodName}' with {paramCount} arguments and CancellationToken found in type '{type.FullName}'.");
            }

            if (candidatesWithoutCt.Count == 1)
            {
                return candidatesWithoutCt[0];
            }
            if (candidatesWithoutCt.Count > 1)
            {
                throw new AmbiguousMatchException($"Ambiguous match: multiple static methods named '{methodName}' with {paramCount} parameters found in type '{type.FullName}'.");
            }

            return null;
        }

        public static bool IsAssetImportWorkerProcess()
        {
            string[] args = Environment.GetCommandLineArgs();
            for(int i = 0; i < args.Length; i++)
            {
                if(IsAssetImportWorkerName(args[i]))
                {
                    return true;
                }

                if(string.Equals(args[i], "-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && IsAssetImportWorkerName(args[i + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAssetImportWorkerName(string value)
        {
            return string.Equals(value, "AssetImport", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("AssetImportWorker", StringComparison.OrdinalIgnoreCase);
        }

        public static string ReadFileWithRetry(string path, int maxRetries = 5, int delayMs = 10)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
            }

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}

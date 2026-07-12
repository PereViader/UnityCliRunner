using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    [InitializeOnLoad]
    public static class UnityCliCompilationTracker
    {
        private const string CompilationDiagnosticsFileName = "unity_compilation_errors.txt";

        private static volatile bool s_IsCompiling;
        private static volatile bool s_IsUpdating;
        private static volatile bool s_RefreshPending;
        private static volatile bool s_ScriptCompilationFailed;
        private static volatile bool s_CompilationRequested;
        private static double s_CompilationRequestTime;

        public static bool IsCompiling => s_IsCompiling;
        public static bool IsUpdating => s_IsUpdating;
        public static bool ScriptCompilationFailed => s_ScriptCompilationFailed;

        public static bool RefreshPending
        {
            get => s_RefreshPending;
            set => s_RefreshPending = value;
        }

        public static bool CompilationRequested
        {
            get => s_CompilationRequested;
            set
            {
                s_CompilationRequested = value;
                if (value)
                {
                    s_CompilationRequestTime = EditorApplication.timeSinceStartup;
                }
            }
        }

        static UnityCliCompilationTracker()
        {
            EditorApplication.update += UpdateCompilationState;
        }

        public static void UpdateCompilationState()
        {
            s_IsCompiling = EditorApplication.isCompiling;
            s_IsUpdating = EditorApplication.isUpdating;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;

            if (s_CompilationRequested)
            {
                if (s_IsCompiling)
                {
                    s_CompilationRequested = false;
                }
                else if (EditorApplication.timeSinceStartup - s_CompilationRequestTime > 1.5)
                {
                    s_CompilationRequested = false;
                }
            }
        }

        public static void ClearActiveEntries()
        {
            try
            {
                var logEntriesType = FindType("UnityEditor.LogEntries") ?? FindType("UnityEditorInternal.LogEntries");
                var clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                clearMethod?.Invoke(null, null);
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to clear active compilation diagnostics: {e}");
            }
        }

        public static void DeleteDiagnosticsFile()
        {
            try
            {
                string diagnosticsPath = Path.Combine(GetTempDirectory(), CompilationDiagnosticsFileName);
                if(File.Exists(diagnosticsPath))
                {
                    File.Delete(diagnosticsPath);
                }
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to delete compilation diagnostics file: {e}");
            }
        }

        private static Type FindType(string fullName)
        {
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if(type != null)
                        return type;
                }
                catch { }
            }
            return null;
        }

        private static string GetTempDirectory()
        {
            string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
            if(!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }
            return tempDir;
        }

        public static void WriteActiveErrorsToFile()
        {
            try
            {
                var logEntriesType = FindType("UnityEditor.LogEntries") ?? FindType("UnityEditorInternal.LogEntries");
                var logEntryType = FindType("UnityEditor.LogEntry") ?? FindType("UnityEditorInternal.LogEntry");

                var getCountMethod = logEntriesType?.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public);
                var getEntryMethod = logEntriesType?.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);
                var startGettingEntriesMethod = logEntriesType?.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var endGettingEntriesMethod = logEntriesType?.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);

                var messageField = logEntryType?.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fileField = logEntryType?.GetField("file", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var lineField = logEntryType?.GetField("line", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var columnField = logEntryType?.GetField("column", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var modeField = logEntryType?.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if(logEntriesType == null || logEntryType == null || getCountMethod == null || getEntryMethod == null || messageField == null || modeField == null)
                {
                    WriteFallbackDiagnosticsIfCompilationFailed("Unity editor reports scriptCompilationFailed is true, but UnityCliRunner could not read Editor Console compilation entries. Missing Unity console reflection members.");
                    return;
                }

                string errorsPath = Path.Combine(GetTempDirectory(), CompilationDiagnosticsFileName);
                var diagnostics = new List<string>();

                startGettingEntriesMethod?.Invoke(null, null);
                try
                {
                    int count = (int) getCountMethod.Invoke(null, null);
                    var logEntry = Activator.CreateInstance(logEntryType);
                    var parameters = new object[] { 0, logEntry };

                    for(int i = 0; i < count; i++)
                    {
                        parameters[0] = i;
                        getEntryMethod.Invoke(null, parameters);
                        var currentEntry = parameters[1];

                        int mode = (int) modeField.GetValue(currentEntry);
                        bool isCompileError = (mode & (1 << 11)) != 0;
                        bool isCompileWarning = (mode & (1 << 12)) != 0;

                        if(isCompileError || isCompileWarning)
                        {
                            string message = (string) messageField.GetValue(currentEntry);
                            string file = fileField != null ? (string) fileField.GetValue(currentEntry) : "";
                            int line = lineField != null ? (int) lineField.GetValue(currentEntry) : 0;
                            int column = columnField != null ? (int) columnField.GetValue(currentEntry) : 0;

                            string typeStr = isCompileError ? "error" : "warning";
                            if(string.IsNullOrEmpty(message))
                            {
                                continue;
                            }

                            string lineStr = message;
                            if(!string.IsNullOrEmpty(file) && !lineStr.Contains(file))
                            {
                                lineStr = $"{file}({line},{column}): {typeStr} {message}";
                            }
                            diagnostics.Add(lineStr);
                        }
                    }
                }
                finally
                {
                    endGettingEntriesMethod?.Invoke(null, null);
                }

                if(diagnostics.Count > 0)
                {
                    File.WriteAllLines(errorsPath, diagnostics, new UTF8Encoding(false));
                }
                else if(EditorUtility.scriptCompilationFailed)
                {
                    WriteFallbackDiagnosticsIfCompilationFailed("Unity editor reports scriptCompilationFailed is true, but no compiler log entries were captured from the Editor Console.");
                }
                else
                {
                    DeleteDiagnosticsFile();
                }
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write active compilation errors: {e}");
                WriteFallbackDiagnosticsIfCompilationFailed($"Unity editor reports scriptCompilationFailed is true, but UnityCliRunner failed to capture compiler diagnostics: {e.Message}");
            }
        }

        private static void WriteFallbackDiagnosticsIfCompilationFailed(string message)
        {
            try
            {
                if(!EditorUtility.scriptCompilationFailed)
                {
                    DeleteDiagnosticsFile();
                    return;
                }

                string diagnosticsPath = Path.Combine(GetTempDirectory(), CompilationDiagnosticsFileName);
                string diagnostic = $"UnityCliRunner(1,1): error UC0001: {message}";
                File.WriteAllText(diagnosticsPath, diagnostic + Environment.NewLine, new UTF8Encoding(false));
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write fallback compilation diagnostics: {e}");
            }
        }
    }
}

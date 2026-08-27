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

        private static readonly List<string> s_PipelineDiagnostics = new List<string>();
        private static readonly object s_PipelineDiagnosticsLock = new object();

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
                    s_ScriptCompilationFailed = false;
                }
            }
        }

        static UnityCliCompilationTracker()
        {
            DeleteDiagnosticsFile();
            UpdateCompilationState();
            EditorApplication.update += UpdateCompilationState;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnCompilationStarted;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        private static void OnCompilationStarted(object obj)
        {
            s_IsCompiling = true;
            s_CompilationRequested = false;
            s_ScriptCompilationFailed = false;
            lock (s_PipelineDiagnosticsLock)
            {
                s_PipelineDiagnostics.Clear();
            }
            DeleteDiagnosticsFile();
        }

        private static void OnCompilationFinished(object obj)
        {
            s_IsCompiling = false;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;
            WriteActiveErrorsToFile();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, UnityEditor.Compilation.CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
                return;

            lock (s_PipelineDiagnosticsLock)
            {
                foreach (var msg in messages)
                {
                    if (msg.type == UnityEditor.Compilation.CompilerMessageType.Error || msg.type == UnityEditor.Compilation.CompilerMessageType.Warning)
                    {
                        string typeStr = msg.type == UnityEditor.Compilation.CompilerMessageType.Error ? "error" : "warning";
                        string lineStr = msg.message;
                        if (!string.IsNullOrEmpty(msg.file) && !lineStr.Contains(msg.file))
                        {
                            lineStr = $"{msg.file}({msg.line},{msg.column}): {typeStr} {msg.message}";
                        }
                        s_PipelineDiagnostics.Add(lineStr);
                    }
                }
            }
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

            if (!s_IsCompiling && !s_CompilationRequested && !s_RefreshPending)
            {
                WriteActiveErrorsToFile();
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
                string errorsPath = Path.Combine(GetTempDirectory(), CompilationDiagnosticsFileName);
                var diagnostics = new List<string>();

                lock (s_PipelineDiagnosticsLock)
                {
                    if (s_PipelineDiagnostics.Count > 0)
                    {
                        diagnostics.AddRange(s_PipelineDiagnostics);
                    }
                }

                if (diagnostics.Count == 0)
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

                    if (logEntriesType != null && logEntryType != null && getCountMethod != null && getEntryMethod != null && messageField != null && modeField != null)
                    {
                        startGettingEntriesMethod?.Invoke(null, null);
                        try
                        {
                            int count = (int) getCountMethod.Invoke(null, null);
                            var logEntry = Activator.CreateInstance(logEntryType);
                            var parameters = new object[] { 0, logEntry };

                            for (int i = 0; i < count; i++)
                            {
                                parameters[0] = i;
                                getEntryMethod.Invoke(null, parameters);
                                var currentEntry = parameters[1];

                                string message = (string) messageField.GetValue(currentEntry);
                                int mode = (int) modeField.GetValue(currentEntry);
                                bool isCompileError = (mode & (1 << 11)) != 0 || (!string.IsNullOrEmpty(message) && message.Contains("error CS"));
                                bool isCompileWarning = (mode & (1 << 12)) != 0 || (!string.IsNullOrEmpty(message) && message.Contains("warning CS"));

                                if (isCompileError || isCompileWarning)
                                {
                                    string file = fileField != null ? (string) fileField.GetValue(currentEntry) : "";
                                    int line = lineField != null ? (int) lineField.GetValue(currentEntry) : 0;
                                    int column = columnField != null ? (int) columnField.GetValue(currentEntry) : 0;

                                    string typeStr = isCompileError ? "error" : "warning";
                                    if (string.IsNullOrEmpty(message))
                                    {
                                        continue;
                                    }

                                    string lineStr = message;
                                    if (!string.IsNullOrEmpty(file) && !lineStr.Contains(file))
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
                    }
                }

                if (diagnostics.Count > 0)
                {
                    File.WriteAllLines(errorsPath, diagnostics, new UTF8Encoding(false));
                }
                else if (EditorUtility.scriptCompilationFailed)
                {
                    WriteFallbackDiagnosticsIfCompilationFailed("Unity editor reports scriptCompilationFailed is true, but no compiler diagnostics were captured.");
                }
                else
                {
                    DeleteDiagnosticsFile();
                }
            }
            catch (Exception e)
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

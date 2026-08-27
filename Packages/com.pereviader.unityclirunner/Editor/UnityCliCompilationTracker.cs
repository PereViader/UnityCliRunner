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

        private static readonly Dictionary<string, List<string>> s_AssemblyDiagnostics = new Dictionary<string, List<string>>();
        private static readonly object s_DiagnosticsLock = new object();

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
            UpdateCompilationState();
            WriteActiveErrorsToFile();
            EditorApplication.update += UpdateCompilationState;
            EditorApplication.quitting += DeleteDiagnosticsFile;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnCompilationStarted;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        private static void OnCompilationStarted(object obj)
        {
            s_IsCompiling = true;
            s_CompilationRequested = false;
        }

        private static void OnCompilationFinished(object obj)
        {
            s_IsCompiling = false;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;
            WriteActiveErrorsToFile();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, UnityEditor.Compilation.CompilerMessage[] messages)
        {
            string key = assemblyPath ?? "";
            lock (s_DiagnosticsLock)
            {
                if (messages == null || messages.Length == 0)
                {
                    s_AssemblyDiagnostics.Remove(key);
                    return;
                }

                var list = new List<string>();
                foreach (var msg in messages)
                {
                    if (msg.type == UnityEditor.Compilation.CompilerMessageType.Error || msg.type == UnityEditor.Compilation.CompilerMessageType.Warning)
                    {
                        bool isError = msg.type == UnityEditor.Compilation.CompilerMessageType.Error;
                        string formatted = FormatCompilerDiagnostic(msg.message, msg.file, msg.line, msg.column, isError);
                        if (!string.IsNullOrEmpty(formatted))
                        {
                            list.Add(formatted);
                        }
                    }
                }

                if (list.Count > 0)
                {
                    s_AssemblyDiagnostics[key] = list;
                }
                else
                {
                    s_AssemblyDiagnostics.Remove(key);
                }
            }
        }

        private static string FormatCompilerDiagnostic(string rawMessage, string file, int line, int column, bool isError)
        {
            string msg = (rawMessage ?? "").Trim();
            if (string.IsNullOrEmpty(msg)) return null;

            if (System.Text.RegularExpressions.Regex.IsMatch(msg, @"^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\):\s*(error|warning)\s+[a-zA-Z0-9]+:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                int newlineIdx = msg.IndexOfAny(new[] { '\r', '\n' });
                if (newlineIdx >= 0)
                {
                    msg = msg.Substring(0, newlineIdx).Trim();
                }
                return msg;
            }

            string typeStr = isError ? "error" : "warning";
            if (msg.StartsWith("error ", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(6).TrimStart();
            }
            else if (msg.StartsWith("warning ", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(8).TrimStart();
            }

            int nlIdx = msg.IndexOfAny(new[] { '\r', '\n' });
            if (nlIdx >= 0)
            {
                msg = msg.Substring(0, nlIdx).Trim();
            }

            if (!string.IsNullOrEmpty(file) && line > 0)
            {
                return $"{file}({line},{column}): {typeStr} {msg}";
            }

            if (!string.IsNullOrEmpty(file))
            {
                return $"{file}: {typeStr} {msg}";
            }

            return $"{typeStr} {msg}";
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
                    WriteActiveErrorsToFile();
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
                string errorsPath = Path.Combine(GetTempDirectory(), CompilationDiagnosticsFileName);
                var diagnostics = new List<string>();

                lock (s_DiagnosticsLock)
                {
                    foreach (var list in s_AssemblyDiagnostics.Values)
                    {
                        if (list != null && list.Count > 0)
                        {
                            diagnostics.AddRange(list);
                        }
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

                    var messageField = logEntryType?.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                    ?? logEntryType?.GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

                                    string formatted = FormatCompilerDiagnostic(message, file, line, column, isCompileError);
                                    if (!string.IsNullOrEmpty(formatted))
                                    {
                                        diagnostics.Add(formatted);
                                    }
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
                    WriteDiagnosticsFileAtomically(errorsPath, diagnostics);
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

        private static void WriteDiagnosticsFileAtomically(string errorsPath, IEnumerable<string> lines)
        {
            string tmpPath = errorsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(tmpPath, lines, new UTF8Encoding(false));
                if (File.Exists(errorsPath))
                {
                    File.Delete(errorsPath);
                }
                File.Move(tmpPath, errorsPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write diagnostics file atomically to {errorsPath}: {e}");
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
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
                WriteDiagnosticsFileAtomically(diagnosticsPath, new[] { diagnostic });
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write fallback compilation diagnostics: {e}");
            }
        }
    }
}

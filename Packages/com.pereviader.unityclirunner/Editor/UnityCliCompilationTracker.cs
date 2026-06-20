using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    public static class UnityCliCompilationTracker
    {
        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
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

                if (logEntriesType == null || logEntryType == null || getCountMethod == null || getEntryMethod == null || messageField == null || modeField == null) return;

                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string errorsPath = Path.Combine(tempDir, "unity_compilation_errors.txt");

                startGettingEntriesMethod?.Invoke(null, null);
                try
                {
                    int count = (int)getCountMethod.Invoke(null, null);
                    using (var writer = new StreamWriter(errorsPath, false, new UTF8Encoding(false)))
                    {
                        var logEntry = Activator.CreateInstance(logEntryType);
                        var parameters = new object[] { 0, logEntry };

                        for (int i = 0; i < count; i++)
                        {
                            parameters[0] = i;
                            getEntryMethod.Invoke(null, parameters);
                            var currentEntry = parameters[1];

                            int mode = (int)modeField.GetValue(currentEntry);
                            bool isCompileError = (mode & (1 << 11)) != 0;
                            bool isCompileWarning = (mode & (1 << 12)) != 0;

                            if (isCompileError || isCompileWarning)
                            {
                                string message = (string)messageField.GetValue(currentEntry);
                                string file = fileField != null ? (string)fileField.GetValue(currentEntry) : "";
                                int line = lineField != null ? (int)lineField.GetValue(currentEntry) : 0;
                                int column = columnField != null ? (int)columnField.GetValue(currentEntry) : 0;

                                string typeStr = isCompileError ? "error" : "warning";
                                if (string.IsNullOrEmpty(message)) continue;

                                string lineStr = message;
                                if (!string.IsNullOrEmpty(file) && !lineStr.Contains(file))
                                {
                                    lineStr = $"{file}({line},{column}): {typeStr} {message}";
                                }
                                writer.WriteLine(lineStr);
                            }
                        }
                    }
                }
                finally
                {
                    endGettingEntriesMethod?.Invoke(null, null);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write active compilation errors: {e}");
            }
        }
    }
}

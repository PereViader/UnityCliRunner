using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCliRunner
{
    [InitializeOnLoad]
    public static class UnityCliCompilationTracker
    {
        private static readonly List<CompilerMessage> s_CompilerMessages = new List<CompilerMessage>();
        private static readonly object s_Lock = new object();

        static UnityCliCompilationTracker()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        private static void OnCompilationStarted(object context)
        {
            lock (s_Lock)
            {
                s_CompilerMessages.Clear();
            }
            DeleteErrorsFile();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;
            lock (s_Lock)
            {
                s_CompilerMessages.AddRange(messages);
            }
        }

        private static void OnCompilationFinished(object context)
        {
            WriteErrorsFile();
        }

        private static void DeleteErrorsFile()
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                string errorsPath = Path.Combine(tempDir, "unity_compilation_errors.txt");
                if (File.Exists(errorsPath))
                {
                    File.Delete(errorsPath);
                }
            }
            catch (Exception) { }
        }

        private static void WriteErrorsFile()
        {
            try
            {
                lock (s_Lock)
                {
                    if (s_CompilerMessages.Count == 0) return;

                    string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                    if (!Directory.Exists(tempDir))
                    {
                        Directory.CreateDirectory(tempDir);
                    }
                    string errorsPath = Path.Combine(tempDir, "unity_compilation_errors.txt");

                    using (var writer = new StreamWriter(errorsPath, false, Encoding.UTF8))
                    {
                        foreach (var msg in s_CompilerMessages)
                        {
                            string lineStr = msg.message;
                            if (string.IsNullOrEmpty(lineStr)) continue;

                            // If msg.message does not already contain file info, format it
                            if (string.IsNullOrEmpty(msg.file) || !lineStr.Contains(msg.file))
                            {
                                string typeStr = msg.type == CompilerMessageType.Error ? "error" : "warning";
                                lineStr = $"{msg.file}({msg.line},{msg.column}): {typeStr} {msg.message}";
                            }
                            writer.WriteLine(lineStr);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write compilation errors file: {e}");
            }
        }
    }
}

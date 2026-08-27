using System;
using System.IO;
using UnityEditor;

namespace UnityCliRunner
{
    internal class PollRefreshHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string response = GetRefreshPollResponse();
            writer.WriteLine(response);
        }

        private string GetRefreshPollResponse()
        {
            if (UnityCliCompilationTracker.RefreshPending || UnityCliCompilationTracker.CompilationRequested)
            {
                return "COMPILING";
            }

            if (UnityCliCompilationTracker.IsCompiling)
            {
                return "COMPILING";
            }

            if (UnityCliCompilationTracker.IsUpdating)
            {
                return "UPDATING";
            }

            if (UnityCliCompilationTracker.ScriptCompilationFailed)
            {
                string diagnosticsPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_compilation_errors.txt");
                if (!File.Exists(diagnosticsPath) || new FileInfo(diagnosticsPath).Length == 0)
                {
                    UnityCliCompilationTracker.WriteActiveErrorsToFile();
                }

                if (File.Exists(diagnosticsPath) && new FileInfo(diagnosticsPath).Length > 0)
                {
                    return "COMPILATION_ERROR";
                }

                return "COMPILING";
            }

            return "READY";
        }
    }
}

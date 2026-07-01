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
            UnityCliCompilationTracker.UpdateCompilationState();

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
                UnityCliCompilationTracker.WriteActiveErrorsToFile();
                return "COMPILATION_ERROR";
            }

            UnityCliCompilationTracker.WriteActiveErrorsToFile();
            return "READY";
        }
    }
}

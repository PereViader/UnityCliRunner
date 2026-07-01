using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RecompileHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            writer.WriteLine("RECOMPILING");

            UnityCliCompilationTracker.RefreshPending = true;
            UnityCliCompilationTracker.CompilationRequested = true;
            try
            {
                Debug.Log("UnityCliRunner: Triggering force recompilation via CompilationPipeline.RequestScriptCompilation()");
                UnityCliCompilationTracker.DeleteDiagnosticsFile();
                UnityCliCompilationTracker.ClearActiveEntries();
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(UnityEditor.Compilation.RequestScriptCompilationOptions.CleanBuildCache);
            }
            finally
            {
                UnityCliCompilationTracker.RefreshPending = false;
            }
        }
    }
}

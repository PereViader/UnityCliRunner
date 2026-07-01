using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RecompileHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            UnityCliServer.RefreshPending = true;
            UnityCliServer.CompilationRequested = true;
            UnityCliServer.EnqueueToMainThread(() =>
            {
                try
                {
                    Debug.Log("UnityCliRunner: Triggering force recompilation via CompilationPipeline.RequestScriptCompilation()");
                    UnityCliCompilationTracker.DeleteDiagnosticsFile();
                    UnityCliCompilationTracker.ClearActiveEntries();
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(UnityEditor.Compilation.RequestScriptCompilationOptions.CleanBuildCache);
                }
                finally
                {
                    UnityCliServer.RefreshPending = false;
                }
            });
            writer.WriteLine("RECOMPILING");
        }
    }
}

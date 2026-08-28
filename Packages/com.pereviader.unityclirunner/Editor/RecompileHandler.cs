using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RecompileHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            if (UnityCliCompilationTracker.TryReadRefreshResult(operationId, out _))
            {
                writer.WriteLine("RECOMPILING");
                return;
            }
            var begin = UnityCliOperationStore.TryBegin(operationId, "recompile", "Requested", out var existing);
            if (begin == BeginOperationResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }
            if (begin == BeginOperationResult.Busy)
            {
                writer.WriteLine($"BUSY {existing.kind} {existing.operationId}");
                return;
            }

            writer.WriteLine("RECOMPILING");
            writer.Flush();
            if (begin == BeginOperationResult.AlreadyStarted)
            {
                return;
            }

            UnityCliCompilationTracker.DeleteRefreshResult();
            UnityCliCompilationTracker.ClearCapturedDiagnostics();

            UnityCliCompilationTracker.RefreshPending = true;
            UnityCliCompilationTracker.CompilationRequested = true;
            UnityCliOperationStore.Update(operationId, "Recompiling");
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
                UnityCliCompilationTracker.ObserveOperationUntilSettled();
            }
        }
    }
}

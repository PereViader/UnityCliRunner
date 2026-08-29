using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RefreshHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            if (UnityCliCompilationTracker.TryReadRefreshResult(operationId, out _))
            {
                writer.WriteLine("REFRESHING");
                return;
            }
            var begin = UnityCliOperationStore.TryBegin(operationId, "refresh", "Requested", out var existing);
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

            writer.WriteLine("REFRESHING");
            writer.Flush();

            // A retry with the same identity is an acknowledgement, never a
            // second AssetDatabase.Refresh invocation.
            if (begin == BeginOperationResult.AlreadyStarted)
            {
                return;
            }

            UnityCliCompilationTracker.ResetRefreshResultCache();
            UnityCliCompilationTracker.DeleteDiagnosticsFile();
            UnityCliCompilationTracker.ClearCapturedDiagnostics();

            UnityCliCompilationTracker.RefreshPending = true;
            UnityCliCompilationTracker.CompilationRequested = true;
            UnityCliOperationStore.Update(operationId, "Refreshing");
            try
            {
                Debug.Log("UnityCliRunner: Triggering AssetDatabase.Refresh()");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                UnityCliCompilationTracker.RefreshPending = false;
                UnityCliCompilationTracker.WriteActiveErrorsToFile();
                UnityCliCompilationTracker.ObserveOperationUntilSettled();
            }
        }
    }
}

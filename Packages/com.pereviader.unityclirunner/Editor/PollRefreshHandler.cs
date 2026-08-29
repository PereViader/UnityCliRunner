using System;
using System.IO;
using UnityEditor;

namespace UnityCliRunner
{
    internal class PollRefreshHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string response = GetRefreshPollResponse(payload);
            writer.WriteLine(response);
        }

        private string GetRefreshPollResponse(string payload)
        {
            string operationId = payload?.Trim();
            if (!string.IsNullOrEmpty(operationId) && UnityCliCompilationTracker.TryReadRefreshResult(operationId, out var result))
            {
                if (result.interrupted) return $"INTERRUPTION {result.message}";
                return result.success ? "READY" : "COMPILATION_ERROR";
            }
            var operation = UnityCliOperationStore.ReadThreadSafeSnapshot();
            if (operation != null)
            {
                if (!string.IsNullOrEmpty(operationId) && operation.operationId != operationId)
                {
                    return $"BUSY {operation.kind} {operation.operationId}";
                }

                if (operation.kind == OperationKinds.Refresh || operation.kind == OperationKinds.Recompile)
                {
                    if (operation.status == OperationStatus.Interrupted)
                    {
                        return "INTERRUPTION Unity editor restarted before the operation completed.";
                    }
                    return "COMPILING";
                }
            }

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
                string diagnosticsPath = UnityCliPaths.DiagnosticsFile;
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

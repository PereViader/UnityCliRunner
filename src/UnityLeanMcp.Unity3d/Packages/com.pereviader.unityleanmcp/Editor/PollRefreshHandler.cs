using System;
using System.IO;
using UnityEditor;

namespace UnityLeanMcp
{
    internal class PollRefreshHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string response = GetRefreshPollResponse(payload);
            writer.WriteLine(response);
        }

        private string GetRefreshPollResponse(string payload)
        {
            string operationId = payload?.Trim();
            if (!string.IsNullOrEmpty(operationId) && UnityLeanMcpCompilationTracker.TryReadRefreshResult(operationId, out var result))
            {
                if (result.interrupted) return $"INTERRUPTION {PollHelper.EscapeLine(result.message)}";
                return result.success ? "READY" : "COMPILATION_ERROR";
            }
            var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
            if (operation != null)
            {
                if (operation.kind == OperationKinds.Refresh || operation.kind == OperationKinds.Recompile)
                {
                    if (operation.status == OperationStatus.Interrupted)
                    {
                        return "INTERRUPTION Unity editor restarted before the operation completed.";
                    }
                    return "COMPILING";
                }

                if (string.IsNullOrEmpty(operationId) || operation.operationId != operationId)
                {
                    return $"BUSY {operation.kind} {operation.operationId}";
                }
            }

            if (UnityLeanMcpCompilationTracker.RefreshPending || UnityLeanMcpCompilationTracker.CompilationRequested)
            {
                return "COMPILING";
            }

            if (UnityLeanMcpCompilationTracker.IsCompiling)
            {
                return "COMPILING";
            }

            if (UnityLeanMcpCompilationTracker.IsUpdating)
            {
                return "UPDATING";
            }

            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed)
            {
                string diagnosticsPath = UnityLeanMcpPaths.DiagnosticsFile;
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

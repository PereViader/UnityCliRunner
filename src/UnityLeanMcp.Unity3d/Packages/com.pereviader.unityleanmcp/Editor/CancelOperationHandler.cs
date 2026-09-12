using System;
using System.IO;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class CancelOperationHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            CancelActiveOperation(operationId, writer);
        }

        public static void CancelActiveOperation(string operationId, StreamWriter writer)
        {
            operationId = operationId?.Trim();
            var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot() ?? UnityLeanMcpOperationStore.Read();
            if (operation == null)
            {
                writer.WriteLine("NO_OPERATION");
                writer.Flush();
                return;
            }

            if (!string.IsNullOrEmpty(operationId) && operation.operationId != operationId)
            {
                writer.WriteLine($"MISMATCH {operation.operationId}");
                writer.Flush();
                return;
            }

            OperationLifecycleRegistry.Cancel(operation, writer);
        }
    }
}

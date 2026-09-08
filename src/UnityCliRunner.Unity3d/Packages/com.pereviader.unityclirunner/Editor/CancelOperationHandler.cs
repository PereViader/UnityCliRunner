using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal class CancelOperationHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            CancelActiveOperation(operationId, writer);
        }

        public static void CancelActiveOperation(string operationId, StreamWriter writer)
        {
            operationId = operationId?.Trim();
            var operation = UnityCliOperationStore.ReadThreadSafeSnapshot() ?? UnityCliOperationStore.Read();
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

            switch (operation.kind)
            {
                case OperationKinds.Test:
                    RunTestsHandler.CancelActiveTestRun(operation.operationId, writer);
                    writer.Flush();
                    return;

                case OperationKinds.Execute:
                    bool execCancelled = ExecuteMethodHandler.CancelActiveExecute(operation.operationId);
                    if (execCancelled)
                    {
                        writer.WriteLine("CANCELLED");
                    }
                    else
                    {
                        writer.WriteLine("NOT_CANCELABLE");
                    }
                    writer.Flush();
                    return;

                case OperationKinds.Eval:
                    bool evalCancelled = EvalHandler.CancelActiveEval(operation.operationId);
                    if (evalCancelled)
                    {
                        writer.WriteLine("CANCELLED");
                    }
                    else
                    {
                        writer.WriteLine("NOT_CANCELABLE");
                    }
                    writer.Flush();
                    return;

                case OperationKinds.Refresh:
                case OperationKinds.Recompile:
                default:
                    writer.WriteLine("NOT_CANCELABLE");
                    writer.Flush();
                    return;
            }
        }
    }
}

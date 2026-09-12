using System.IO;

namespace UnityLeanMcp
{
    internal class PollExecuteHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            PollHelper.PollOperationResult<UnityExecuteResult>(
                operationId,
                UnityLeanMcpPaths.GetExecuteResultFile(operationId),
                null,
                writer,
                res => res.operationId,
                PollHelper.WriteOperationResultResponse);
        }
    }
}

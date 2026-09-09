using System.IO;

namespace UnityCliRunner
{
    internal class PollExecuteHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            PollHelper.PollOperationResult<UnityExecuteResult>(
                operationId,
                UnityCliPaths.ExecuteResultFile,
                null,
                writer,
                res => res.operationId,
                PollHelper.WriteOperationResultResponse);
        }
    }
}

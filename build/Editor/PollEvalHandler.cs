using System.IO;

namespace UnityCliRunner
{
    internal class PollEvalHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            PollHelper.PollOperationResult<UnityEvalResult>(
                operationId,
                UnityCliPaths.EvalResultFile,
                null,
                writer,
                res => res.operationId,
                PollHelper.WriteOperationResultResponse);
        }
    }
}

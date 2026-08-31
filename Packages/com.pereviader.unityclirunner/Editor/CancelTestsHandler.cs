using System.IO;

namespace UnityCliRunner
{
    internal class CancelTestsHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            RunTestsHandler.CancelActiveTestRun(operationId, writer);
        }
    }
}

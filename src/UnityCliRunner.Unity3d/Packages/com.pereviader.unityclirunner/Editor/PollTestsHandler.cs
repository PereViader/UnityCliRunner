using System.IO;

namespace UnityCliRunner
{
    internal class PollTestsHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            PollHelper.PollOperationResult<UnityTestRunResult>(
                operationId,
                UnityCliPaths.TestResultsFile,
                UnityCliPaths.TestRunningFile,
                writer,
                res => res.runId,
                (res, w) =>
                {
                    string skipStr = res.skipCount > 0 ? $", {res.skipCount} skipped" : "";
                    if (res.success)
                    {
                        w.WriteLine($"SUCCESS {res.passCount} passed{skipStr}");
                    }
                    else if (res.resultState == "Interrupted")
                    {
                        w.WriteLine($"INTERRUPTION {res.message}");
                    }
                    else if (!string.IsNullOrEmpty(res.message))
                    {
                        w.WriteLine($"FAILURE {res.message}");
                    }
                    else
                    {
                        w.WriteLine($"FAILURE {res.failCount} failed, {res.passCount} passed{skipStr}");
                    }
                },
                (runningPath, opId) =>
                {
                    var running = RunTestsHandler.ReadRunningState();
                    return running != null && (string.IsNullOrEmpty(opId) || running.runId == opId);
                });
        }
    }
}

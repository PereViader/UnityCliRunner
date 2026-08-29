using System.IO;

namespace UnityCliRunner
{
    internal class PollEvalHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            PollHelper.PollOperationResult<UnityEvalResult>(
                operationId,
                UnityCliPaths.EvalResultFile,
                UnityCliPaths.EvalRunningFile,
                writer,
                res => res.operationId,
                (res, w) =>
                {
                    if (res.success)
                    {
                        if (!string.IsNullOrEmpty(res.payload))
                        {
                            w.WriteLine($"SUCCESS {res.payload}");
                        }
                        else
                        {
                            w.WriteLine("SUCCESS");
                        }
                    }
                    else if (res.interrupted)
                    {
                        w.WriteLine($"INTERRUPTION {res.message}");
                    }
                    else
                    {
                        w.WriteLine($"FAILURE {res.message}");
                    }
                });
        }
    }
}

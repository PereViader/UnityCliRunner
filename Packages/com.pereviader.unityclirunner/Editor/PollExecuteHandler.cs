using System.IO;

namespace UnityCliRunner
{
    internal class PollExecuteHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            PollHelper.PollOperationResult<UnityExecuteResult>(
                operationId,
                UnityCliPaths.ExecuteResultFile,
                UnityCliPaths.ExecuteRunningFile,
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

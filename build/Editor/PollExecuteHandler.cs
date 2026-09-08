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
                (res, w) =>
                {
                    if (res.success)
                    {
                        if (!string.IsNullOrEmpty(res.payload))
                        {
                            w.WriteLine($"SUCCESS {PollHelper.EscapeLine(res.payload)}");
                        }
                        else
                        {
                            w.WriteLine("SUCCESS");
                        }
                    }
                    else if (res.interrupted)
                    {
                        w.WriteLine($"INTERRUPTION {PollHelper.EscapeLine(res.message)}");
                    }
                    else
                    {
                        w.WriteLine($"FAILURE {PollHelper.EscapeLine(res.message)}");
                    }
                });
        }
    }
}

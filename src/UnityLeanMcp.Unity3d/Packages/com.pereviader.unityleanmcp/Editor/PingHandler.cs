using System.IO;

namespace UnityLeanMcp
{
    internal class PingHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            writer.WriteLine("PONG");
        }
    }
}

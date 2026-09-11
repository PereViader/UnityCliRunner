using System.IO;

namespace UnityLeanMcp
{
    internal enum CommandExecutionTarget
    {
        WorkerThread,
        MainThread,
        EditModeOnly
    }

    internal interface ICommandHandler
    {
        CommandExecutionTarget ExecutionTarget { get; }
        void Handle(string payload, StreamWriter writer);
    }
}

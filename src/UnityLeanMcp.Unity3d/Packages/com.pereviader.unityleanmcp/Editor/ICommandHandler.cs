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
        bool IsMutating => false;
        bool RequiresCompilationSettled => false;
        void Handle(string payload, StreamWriter writer);
    }
}

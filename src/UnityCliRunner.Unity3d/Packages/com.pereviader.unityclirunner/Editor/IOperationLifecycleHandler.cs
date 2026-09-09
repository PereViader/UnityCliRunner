using System.IO;

namespace UnityCliRunner
{
    internal interface IOperationLifecycleHandler
    {
        string OperationKind { get; }
        bool TryCancel(string operationId, StreamWriter writer);
        void OnEditorRestarted(string operationId, string message);
        void OnDomainReloaded(string operationId, string message);
        void OnEditorQuitting(string operationId);
    }
}

using System.Threading;
using System.Threading.Tasks;

namespace UnityCliRunner.Mcp;

public interface IUnityProcessManager
{
    string ProjectRoot { get; }
    IUnityPathResolver PathResolver { get; }
    bool IsUnityRunning(out int? processId);
    string GetUnityMode(int? pid = null);
    string? GetProjectEditorVersion();
    int ReadPortFile();
    Task<bool> StartUnityAsync(CancellationToken cancellationToken = default);
    Task<bool> WaitForHealthyAsync(CancellationToken cancellationToken = default);
    Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default);
    Task<bool> StopUnityAsync(CancellationToken cancellationToken = default);
    void PurgeOperationState();
}

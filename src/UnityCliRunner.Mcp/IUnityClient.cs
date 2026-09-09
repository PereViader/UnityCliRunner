using System;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;

namespace UnityCliRunner.Mcp;

public interface IUnityClient
{
    int PollIntervalMs { get; set; }
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<UnityRefreshResult> RefreshAsync(bool isRecompile = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    Task<UnityEvalResult> EvalAsync(string code, CancellationToken cancellationToken = default);
    Task<UnityExecuteResult> ExecuteMethodAsync(string methodName, string[]? args, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    Task<UnityTestRunResult> RunTestsAsync(string? filter, string? category, string? mode, bool failedOnly = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
}

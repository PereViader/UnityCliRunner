using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace UnityCliRunner.Mcp;

public class UnityClient : IUnityClient
{
    private readonly IUnityProcessManager _processManager;
    private readonly IUnityPathResolver _pathResolver;
    private readonly ILogger<UnityClient> _logger;

    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public UnityClient(IUnityProcessManager processManager, IUnityPathResolver pathResolver, ILogger<UnityClient> logger)
    {
        _processManager = processManager;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    public UnityClient(IUnityProcessManager processManager, ILogger<UnityClient> logger)
        : this(processManager, processManager.PathResolver, logger)
    {
    }

    /// <summary>
    /// Interval in milliseconds between polling checks during long-running operations. Defaults to 500ms.
    /// </summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>
    /// Returns current Editor connection state: Ready, Not Running, Compiling, Running Unreachable, Busy.
    /// </summary>
    public virtual async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_processManager.IsUnityRunning(out _))
        {
            return "Not Running";
        }

        int port = _processManager.ReadPortFile();
        if (port <= 0)
        {
            var op = TryReadJsonFile<UnityCliOperationState>(_pathResolver.OperationFile, _ => true);
            if (op != null)
            {
                if (op.Status == "Compiling" || op.Status == "Reloading" || op.Status == "Refreshing" || op.Status == "Recompiling")
                {
                    return "Compiling";
                }
                if (!string.IsNullOrEmpty(op.Kind))
                {
                    return FormatBusyStatus(op);
                }
            }
            return "Running Unreachable";
        }

        string? pingResp = await SendCommandAsync("PING", 2, cancellationToken);
        if (pingResp == "PONG")
        {
            string? pollResp = await SendCommandAsync("POLL_REFRESH", 2, cancellationToken);
            if (pollResp == "COMPILING" || pollResp == "UPDATING")
            {
                return "Compiling";
            }

            var op = TryReadJsonFile<UnityCliOperationState>(_pathResolver.OperationFile, _ => true);
            if (op != null)
            {
                if (op.Status == "Compiling" || op.Status == "Reloading" || op.Status == "Refreshing" || op.Status == "Recompiling")
                {
                    return "Compiling";
                }
                if (!string.IsNullOrEmpty(op.Kind))
                {
                    return FormatBusyStatus(op);
                }
            }

            return "Ready";
        }

        var activeOp = TryReadJsonFile<UnityCliOperationState>(_pathResolver.OperationFile, _ => true);
        if (activeOp != null)
        {
            if (activeOp.Status == "Compiling" || activeOp.Status == "Reloading" || activeOp.Status == "Refreshing" || activeOp.Status == "Recompiling")
            {
                return "Compiling";
            }
            if (!string.IsNullOrEmpty(activeOp.Kind))
            {
                return FormatBusyStatus(activeOp);
            }
        }

        return "Running Unreachable";
    }

    private static string FormatBusyStatus(UnityCliOperationState op)
    {
        return string.IsNullOrEmpty(op.StartedUtc)
            ? $"Busy ({op.Kind})"
            : $"Busy ({op.Kind}, started {op.StartedUtc})";
    }

    private async Task CancelOperationAsync(string opId, string kind)
    {
        _logger.LogInformation("Cancellation requested. Sending CANCEL_OPERATION for {OpId} ({Kind})...", opId, kind);
        try
        {
            using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SendCommandAsync($"CANCEL_OPERATION {opId}", 3, cancelCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to send CANCEL_OPERATION command for {OpId}", opId);
        }
    }

    public Task<UnityRefreshResult> RefreshAsync(bool isRecompile, CancellationToken cancellationToken) =>
        RefreshAsync(isRecompile, null, cancellationToken);

    public Task<UnityRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshAsync(false, null, cancellationToken);

    /// <summary>
    /// Refreshes AssetDatabase, waits for compilation, and returns diagnostics.
    /// If isRecompile is true, triggers clean script recompilation.
    /// </summary>
    public virtual async Task<UnityRefreshResult> RefreshAsync(
        bool isRecompile = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string opId = Guid.NewGuid().ToString("N");

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = 100,
            Message = "Checking Unity Editor connection..."
        });

        try
        {
            await _processManager.EnsureUnityRunningAsync(cancellationToken);
        }
        catch (UnityCompilationException ex)
        {
            return new UnityRefreshResult
            {
                OperationId = opId,
                Success = false,
                Message = ex.Message
            };
        }

        string triggerCommand = isRecompile ? $"RECOMPILE {opId}" : $"REFRESH {opId}";

        _logger.LogInformation("Triggering {Kind} operation with id {OpId}...", isRecompile ? "recompile" : "refresh", opId);

        // Pre-check if Unity is busy
        string? busyCheck = await SendCommandAsync($"POLL_REFRESH {opId}", 2, cancellationToken);
        if (busyCheck != null && busyCheck.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityRefreshResult
            {
                OperationId = opId,
                Success = false,
                Message = $"Unity is busy with another operation: {busyCheck}"
            };
        }

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 10,
            Total = 100,
            Message = isRecompile
                ? "Triggering clean script recompilation..."
                : "Triggering AssetDatabase refresh..."
        });

        // Send refresh/recompile command
        string? initialResponse = await SendCommandAsync(triggerCommand, 10, cancellationToken);
        string expectedAck = isRecompile ? "RECOMPILING" : "REFRESHING";

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityRefreshResult
            {
                OperationId = opId,
                Success = false,
                Message = $"Unity is busy: {initialResponse}"
            };
        }

        int compileProgress = 30;

        var spec = new OperationPollingSpec<UnityRefreshResult>
        {
            OperationId = opId,
            Kind = null,
            ShouldCancelOnAborted = false,
            OperationDisplayName = "refresh operation",
            ResultFilePath = _pathResolver.RefreshResultFile,
            IsMatch = r => r.OperationId == opId,
            PollCommand = $"POLL_REFRESH {opId}",
            PollTimeoutSeconds = 2,
            PollIntervalMs = PollIntervalMs,
            OnResultFound = r =>
            {
                progress?.Report(new ProgressNotificationValue
                {
                    Progress = 90,
                    Total = 100,
                    Message = "Compilation finished, waiting for Editor to settle..."
                });

                EnrichRefreshResultWithDiagnostics(r);

                if (r.Success)
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = 100,
                        Total = 100,
                        Message = isRecompile
                            ? "Clean script recompilation completed."
                            : "AssetDatabase refresh completed."
                    });
                }

                return r;
            },
            CustomResponseHandler = async (pollResp, ct) =>
            {
                if (pollResp == "READY")
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = 90,
                        Total = 100,
                        Message = "Compilation finished, waiting for Editor to settle..."
                    });

                    var result = TryReadJsonFile<UnityRefreshResult>(_pathResolver.RefreshResultFile, r => r.OperationId == opId)
                        ?? new UnityRefreshResult
                        {
                            OperationId = opId,
                            Success = true,
                            Message = "AssetDatabase refresh completed successfully."
                        };

                    EnrichRefreshResultWithDiagnostics(result);

                    if (result.Success)
                    {
                        progress?.Report(new ProgressNotificationValue
                        {
                            Progress = 100,
                            Total = 100,
                            Message = isRecompile
                                ? "Clean script recompilation completed."
                                : "AssetDatabase refresh completed."
                        });
                    }

                    return result;
                }

                if (pollResp == "COMPILATION_ERROR")
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = 90,
                        Total = 100,
                        Message = "Compilation finished, waiting for Editor to settle..."
                    });

                    // Allow brief moment for diagnostics file to settle
                    await Task.Delay(200, ct);
                    string diag = ReadCompilationErrors();
                    return new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = !string.IsNullOrWhiteSpace(diag) ? diag : "Unity script compilation failed."
                    };
                }

                return null;
            },
            OnAfterPoll = (pollResp, ct) =>
            {
                var opState = TryReadJsonFile<UnityCliOperationState>(_pathResolver.OperationFile, o => o.OperationId == opId);
                bool isCompiling = (pollResp == "COMPILING" || pollResp == "UPDATING") ||
                    (opState != null && (opState.Status == "Compiling" || opState.Status == "Reloading" || opState.Status == "Refreshing" || opState.Status == "Recompiling" || opState.Status == "WaitingForUnity"));

                if (isCompiling)
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = compileProgress,
                        Total = 100,
                        Message = "Compiling script assemblies..."
                    });

                    if (compileProgress < 80)
                    {
                        compileProgress = Math.Min(80, compileProgress + 10);
                    }
                }

                return Task.CompletedTask;
            }
        };

        return await PollOperationUntilTerminalAsync(spec, cancellationToken);
    }

    public Task<UnityEvalResult> EvalAsync(string code, CancellationToken cancellationToken) =>
        EvalAsync(code, null, cancellationToken);

    /// <summary>
    /// Evaluates dynamic C# snippet in-memory against active Editor/Play Mode.
    /// </summary>
    public virtual async Task<UnityEvalResult> EvalAsync(
        string code,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = 100,
            Message = "Refreshing AssetDatabase prior to evaluation..."
        });

        IProgress<ProgressNotificationValue>? refreshProgress = progress == null ? null : new ProgressRelay(p =>
        {
            int scaled = (int)Math.Round((p.Progress / (double)(p.Total ?? 100)) * 40);
            progress.Report(new ProgressNotificationValue
            {
                Progress = scaled,
                Total = 100,
                Message = "Refreshing AssetDatabase prior to evaluation..."
            });
        });

        var refreshResult = await RefreshAsync(isRecompile: false, refreshProgress, cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityEvalResult
            {
                Success = false,
                Interrupted = refreshResult.Interrupted,
                Message = refreshResult.Message
            };
        }

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 50,
            Total = 100,
            Message = "Evaluating C# snippet..."
        });

        string opId = Guid.NewGuid().ToString("N");
        string escapedCode = ProtocolCodec.EscapeLine(code);
        string command = $"EVAL {opId} {escapedCode}";

        _logger.LogInformation("Sending EVAL operation {OpId}...", opId);
        string? initialResponse = await SendCommandAsync(command, 10, cancellationToken);

        // Check if result already available
        var immediateResult = TryReadJsonFile<UnityEvalResult>(_pathResolver.EvalResultFile, r => r.OperationId == opId);
        if (immediateResult != null) return immediateResult;

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityEvalResult { OperationId = opId, Success = false, Message = $"Unity is busy: {initialResponse}" };
        }
        if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
        {
            return new UnityEvalResult { OperationId = opId, Success = false, Message = ProtocolCodec.UnescapeLine(initialResponse) };
        }

        return await PollOperationResultAsync<UnityEvalResult>(
            opId: opId,
            kind: "eval",
            operationDisplayName: "evaluation",
            resultFilePath: _pathResolver.EvalResultFile,
            pollCommand: $"POLL_EVAL {opId}",
            cancellationToken: cancellationToken);
    }

    public Task<UnityExecuteResult> ExecuteMethodAsync(string methodName, string[]? args, CancellationToken cancellationToken) =>
        ExecuteMethodAsync(methodName, args, null, cancellationToken);

    /// <summary>
    /// Invokes static C# method with arguments in Unity Editor.
    /// </summary>
    public virtual async Task<UnityExecuteResult> ExecuteMethodAsync(
        string methodName,
        string[]? args,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = 100,
            Message = "Refreshing AssetDatabase prior to execution..."
        });

        IProgress<ProgressNotificationValue>? refreshProgress = progress == null ? null : new ProgressRelay(p =>
        {
            int scaled = (int)Math.Round((p.Progress / (double)(p.Total ?? 100)) * 40);
            progress.Report(new ProgressNotificationValue
            {
                Progress = scaled,
                Total = 100,
                Message = "Refreshing AssetDatabase prior to execution..."
            });
        });

        var refreshResult = await RefreshAsync(isRecompile: false, refreshProgress, cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityExecuteResult
            {
                Success = false,
                Interrupted = refreshResult.Interrupted,
                Message = refreshResult.Message
            };
        }

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 50,
            Total = 100,
            Message = $"Executing static method {methodName}..."
        });

        string opId = Guid.NewGuid().ToString("N");
        var sb = new StringBuilder($"EXECUTE_METHOD {opId} {methodName}");
        if (args != null)
        {
            foreach (var arg in args)
            {
                string escaped = ProtocolCodec.EscapeParam(arg ?? "");
                sb.Append(" \"").Append(escaped).Append('"');
            }
        }

        _logger.LogInformation("Sending EXECUTE_METHOD operation {OpId} for {MethodName}...", opId, methodName);
        string? initialResponse = await SendCommandAsync(sb.ToString(), 10, cancellationToken);

        var immediateResult = TryReadJsonFile<UnityExecuteResult>(_pathResolver.ExecuteResultFile, r => r.OperationId == opId);
        if (immediateResult != null)
        {
            if (immediateResult.Success) ReportExecuteCompleted(progress);
            return immediateResult;
        }

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityExecuteResult { OperationId = opId, Success = false, Message = $"Unity is busy: {initialResponse}" };
        }
        if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
        {
            return new UnityExecuteResult { OperationId = opId, Success = false, Message = ProtocolCodec.UnescapeLine(initialResponse) };
        }

        return await PollOperationResultAsync<UnityExecuteResult>(
            opId: opId,
            kind: "execute",
            operationDisplayName: "method execution",
            resultFilePath: _pathResolver.ExecuteResultFile,
            pollCommand: $"POLL_EXECUTE {opId}",
            onResultFound: res =>
            {
                if (res.Success) ReportExecuteCompleted(progress);
                return res;
            },
            cancellationToken: cancellationToken);
    }

    public Task<UnityTestRunResult> RunTestsAsync(
        string? filter,
        string? category,
        string? mode,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken = default) =>
        RunTestsAsync(filter, category, mode, false, progress, cancellationToken);

    public virtual async Task<UnityTestRunResult> RunTestsAsync(
        string? filter,
        string? category,
        string? mode,
        bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Message = "Checking compilation and refreshing AssetDatabase..."
        });

        var refreshResult = await RefreshAsync(isRecompile: false, cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityTestRunResult
            {
                Success = false,
                ResultState = refreshResult.Interrupted ? "Interrupted" : "CompileError",
                Message = refreshResult.Message
            };
        }

        string opId = Guid.NewGuid().ToString("N");
        string testMode = mode?.Trim().ToLowerInvariant() switch
        {
            "playmode" => "playmode",
            "editmode" => "editmode",
            "all" => "all",
            _ => "all"
        };

        var sb = new StringBuilder($"RUN_TESTS {opId} {testMode}");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sb.Append(" --filter \"").Append(ProtocolCodec.EscapeParam(filter)).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            sb.Append(" --category \"").Append(ProtocolCodec.EscapeParam(category)).Append('"');
        }
        if (failedOnly)
        {
            sb.Append(" --failed-only");
        }

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Message = $"Initializing {testMode} test run..."
        });

        _logger.LogInformation("Sending RUN_TESTS operation {OpId} (mode: {Mode})...", opId, testMode);
        string? initialResponse = await SendCommandAsync(sb.ToString(), 10, cancellationToken);

        var immediateResult = TryReadJsonFile<UnityTestRunResult>(_pathResolver.TestResultsFile, r => r.RunId == opId);
        if (immediateResult != null)
        {
            ReportFinalProgress(progress, immediateResult);
            return immediateResult;
        }

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityTestRunResult { RunId = opId, Success = false, Message = $"Unity is busy: {initialResponse}" };
        }
        if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
        {
            return new UnityTestRunResult { RunId = opId, Success = false, Message = initialResponse };
        }
        if (initialResponse != null && initialResponse.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            var res = TryReadJsonFile<UnityTestRunResult>(_pathResolver.TestResultsFile, r => r.RunId == opId);
            if (res != null)
            {
                ReportFinalProgress(progress, res);
                return res;
            }

            return new UnityTestRunResult { RunId = opId, Success = true, Message = initialResponse };
        }

        int lastCompleted = -1;
        string? lastTestName = null;
        string? lastStatus = null;

        var spec = new OperationPollingSpec<UnityTestRunResult>
        {
            OperationId = opId,
            Kind = "test",
            OperationDisplayName = "test run",
            ResultFilePath = _pathResolver.TestResultsFile,
            IsMatch = r => r.RunId == opId,
            PollCommand = $"POLL_TESTS {opId}",
            PollTimeoutSeconds = 5,
            PollIntervalMs = PollIntervalMs,
            OnResultFound = res =>
            {
                ReportFinalProgress(progress, res);
                return res;
            },
            OnPollTick = _ =>
            {
                var runningState = TryReadJsonFile<UnityTestRunState>(_pathResolver.TestRunningFile, s => s.RunId == opId);
                if (runningState != null && progress != null)
                {
                    if (runningState.CompletedTests != lastCompleted ||
                        runningState.CurrentTestName != lastTestName ||
                        runningState.Status != lastStatus)
                    {
                        lastCompleted = runningState.CompletedTests;
                        lastTestName = runningState.CurrentTestName;
                        lastStatus = runningState.Status;

                        string msg;
                        if (runningState.TotalTests > 0)
                        {
                            if (!string.IsNullOrEmpty(runningState.CurrentTestName))
                            {
                                msg = $"[{runningState.CompletedTests}/{runningState.TotalTests}] Running {runningState.CurrentTestName} (Passed: {runningState.PassCount}, Failed: {runningState.FailCount})";
                            }
                            else
                            {
                                msg = $"[{runningState.CompletedTests}/{runningState.TotalTests}] Running tests... (Passed: {runningState.PassCount}, Failed: {runningState.FailCount})";
                            }
                        }
                        else
                        {
                            msg = !string.IsNullOrEmpty(runningState.CurrentTestName)
                                ? $"Running {runningState.CurrentTestName}..."
                                : "Running tests...";
                        }

                        progress.Report(new ProgressNotificationValue
                        {
                            Progress = runningState.CompletedTests,
                            Total = runningState.TotalTests > 0 ? runningState.TotalTests : null,
                            Message = msg
                        });
                    }
                }
                return Task.CompletedTask;
            }
        };

        return await PollOperationUntilTerminalAsync(spec, cancellationToken);
    }

    private sealed class OperationPollingSpec<TResult> where TResult : class, IOperationResult, new()
    {
        public required string OperationId { get; init; }
        public string? Kind { get; init; }
        public string OperationDisplayName { get; init; } = "operation";
        public required string ResultFilePath { get; init; }
        public required Func<TResult, bool> IsMatch { get; init; }
        public required string PollCommand { get; init; }
        public int PollTimeoutSeconds { get; init; } = 5;
        public int PollIntervalMs { get; init; } = 500;
        public bool CheckOperationStoreForInterruption { get; init; } = true;
        public bool ShouldCancelOnAborted { get; init; } = true;

        public Func<TResult, TResult>? OnResultFound { get; init; }
        public Func<CancellationToken, Task>? OnPollTick { get; init; }
        public Func<string?, CancellationToken, Task>? OnAfterPoll { get; init; }
        public Func<string, CancellationToken, Task<TResult?>>? CustomResponseHandler { get; init; }
    }

    private async Task<TResult> PollOperationUntilTerminalAsync<TResult>(
        OperationPollingSpec<TResult> spec,
        CancellationToken cancellationToken) where TResult : class, IOperationResult, new()
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Poll tick (e.g. running state progress updates)
                if (spec.OnPollTick != null)
                {
                    await spec.OnPollTick(cancellationToken);
                }

                // 2. Authoritative check: terminal result file
                var result = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                if (result != null)
                {
                    return spec.OnResultFound != null ? spec.OnResultFound(result) : result;
                }

                // 3. Process liveness check
                if (!_processManager.IsUnityRunning(out _))
                {
                    // Brief grace period in case result was written as process exited
                    await Task.Delay(300, cancellationToken);
                    var finalCheck = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                    if (finalCheck != null)
                    {
                        return spec.OnResultFound != null ? spec.OnResultFound(finalCheck) : finalCheck;
                    }

                    return new TResult
                    {
                        OperationId = spec.OperationId,
                        Success = false,
                        Message = $"Unity background process exited unexpectedly during {spec.OperationDisplayName}."
                    };
                }

                // 4. Operation store check for interruption
                if (spec.CheckOperationStoreForInterruption)
                {
                    var opState = TryReadJsonFile<UnityCliOperationState>(_pathResolver.OperationFile, o => o.OperationId == spec.OperationId);
                    if (opState != null && opState.Status == "Interrupted")
                    {
                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Interrupted = true,
                            Message = "Unity operation was interrupted by domain reload or editor restart."
                        };
                    }
                }

                // 5. Poll socket
                string? pollResp = await SendCommandAsync(spec.PollCommand, spec.PollTimeoutSeconds, cancellationToken);
                if (pollResp != null)
                {
                    if (spec.CustomResponseHandler != null)
                    {
                        var custom = await spec.CustomResponseHandler(pollResp, cancellationToken);
                        if (custom != null)
                        {
                            return custom;
                        }
                    }

                    if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Operation interrupted.";
                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Interrupted = true,
                            Message = ProtocolCodec.UnescapeLine(msg)
                        };
                    }

                    if (pollResp.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Message = $"Lost ownership of {spec.OperationDisplayName}: {pollResp}"
                        };
                    }

                    if (string.Equals(pollResp, "IDLE", StringComparison.OrdinalIgnoreCase))
                    {
                        // Always re-check terminal result file before declaring idle failure (Rule 26 & Issue #71)
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Message = $"{spec.OperationDisplayName} is no longer recognized by the Editor (Editor is idle)."
                        };
                    }

                    if (pollResp.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Message = ProtocolCodec.UnescapeLine(pollResp)
                        };
                    }

                    if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        string msg = pollResp.Length > 7 ? pollResp[7..].Trim() : "Operation failed.";
                        return new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = false,
                            Message = ProtocolCodec.UnescapeLine(msg)
                        };
                    }

                    if (pollResp.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRes = TryReadJsonFile(spec.ResultFilePath, spec.IsMatch);
                        if (fileRes != null)
                        {
                            return spec.OnResultFound != null ? spec.OnResultFound(fileRes) : fileRes;
                        }

                        string payload = pollResp.Length > 7 ? pollResp[7..].Trim() : "";
                        var successRes = new TResult
                        {
                            OperationId = spec.OperationId,
                            Success = true,
                            Message = pollResp
                        };
                        if (successRes is UnityOperationResult opRes)
                        {
                            opRes.Payload = ProtocolCodec.UnescapeLine(payload);
                        }
                        return spec.OnResultFound != null ? spec.OnResultFound(successRes) : successRes;
                    }
                }

                // 6. After poll hook (e.g. refresh compilation progress)
                if (spec.OnAfterPoll != null)
                {
                    await spec.OnAfterPoll(pollResp, cancellationToken);
                }

                await Task.Delay(spec.PollIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            if (spec.ShouldCancelOnAborted && !string.IsNullOrEmpty(spec.Kind))
            {
                await CancelOperationAsync(spec.OperationId, spec.Kind);
            }
            throw;
        }
    }

    private Task<TResult> PollOperationResultAsync<TResult>(
        string opId,
        string kind,
        string operationDisplayName,
        string resultFilePath,
        string pollCommand,
        Func<TResult, TResult>? onResultFound = null,
        CancellationToken cancellationToken = default) where TResult : UnityOperationResult, new()
    {
        var spec = new OperationPollingSpec<TResult>
        {
            OperationId = opId,
            Kind = kind,
            OperationDisplayName = operationDisplayName,
            ResultFilePath = resultFilePath,
            IsMatch = r => r.OperationId == opId,
            PollCommand = pollCommand,
            PollTimeoutSeconds = 5,
            PollIntervalMs = PollIntervalMs,
            OnResultFound = onResultFound
        };

        return PollOperationUntilTerminalAsync(spec, cancellationToken);
    }

    private static void ReportFinalProgress(IProgress<ProgressNotificationValue>? progress, UnityTestRunResult result)
    {
        if (progress == null) return;
        int total = result.PassCount + result.FailCount + result.SkipCount;
        string msg = result.Success
            ? $"Tests finished: {result.PassCount} passed, {result.SkipCount} skipped."
            : $"Tests finished: {result.FailCount} failed, {result.PassCount} passed, {result.SkipCount} skipped.";
        progress.Report(new ProgressNotificationValue
        {
            Progress = total,
            Total = total > 0 ? total : null,
            Message = msg
        });
    }


    private async Task<string?> SendCommandAsync(string command, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        int port = _processManager.ReadPortFile();
        if (port <= 0 || port > 65535)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            client.ReceiveTimeout = timeoutSeconds * 1000;
            client.SendTimeout = timeoutSeconds * 1000;

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            await writer.WriteLineAsync(command.AsMemory(), cts.Token);
            string? line = await reader.ReadLineAsync(cts.Token);
            return line?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Socket command failed: {Command}", command);
            return null;
        }
    }

    private string ReadCompilationErrors()
    {
        if (File.Exists(_pathResolver.CompilationErrorsFile))
        {
            try
            {
                return UnityProcessManager.ReadFileWithRetry(_pathResolver.CompilationErrorsFile);
            }
            catch { }
        }
        return "";
    }

    private void EnrichRefreshResultWithDiagnostics(UnityRefreshResult result)
    {
        string errors = ReadCompilationErrors();
        if (!string.IsNullOrWhiteSpace(errors))
        {
            result.Message = errors;
        }
    }

    private static T? TryReadJsonFile<T>(string filePath, Func<T, bool> predicate) where T : class
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            string json = UnityProcessManager.ReadFileWithRetry(filePath, maxRetries: 3, delayMs: 50);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var result = JsonSerializer.Deserialize<T>(json, s_JsonOptions);
            if (result != null && predicate(result))
            {
                return result;
            }
        }
        catch
        {
            // Partially written file or transient read error during operation
        }

        return null;
    }

    private static void ReportExecuteCompleted(IProgress<ProgressNotificationValue>? progress)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 100,
            Total = 100,
            Message = "Method execution completed."
        });
    }

    private sealed class ProgressRelay : IProgress<ProgressNotificationValue>
    {
        private readonly Action<ProgressNotificationValue> _handler;

        public ProgressRelay(Action<ProgressNotificationValue> handler)
        {
            _handler = handler;
        }

        public void Report(ProgressNotificationValue value) => _handler(value);
    }
}

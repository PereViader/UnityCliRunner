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

public class UnityClient
{
    private readonly UnityProcessManager _processManager;
    private readonly ILogger<UnityClient> _logger;

    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public UnityClient(UnityProcessManager processManager, ILogger<UnityClient> logger)
    {
        _processManager = processManager;
        _logger = logger;
    }

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
            var op = TryReadJsonFile<UnityCliOperationState>(_processManager.OperationFile, _ => true);
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

            var op = TryReadJsonFile<UnityCliOperationState>(_processManager.OperationFile, _ => true);
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

        var activeOp = TryReadJsonFile<UnityCliOperationState>(_processManager.OperationFile, _ => true);
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

        try
        {
            // Poll until completion with domain reload resilience
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Authoritative check: Temp/unity_refresh_result.json
                var cachedResult = TryReadJsonFile<UnityRefreshResult>(_processManager.RefreshResultFile, r => r.OperationId == opId);
                if (cachedResult != null)
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = 90,
                        Total = 100,
                        Message = "Compilation finished, waiting for Editor to settle..."
                    });

                    EnrichRefreshResultWithDiagnostics(cachedResult);

                    if (cachedResult.Success)
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

                    return cachedResult;
                }

                // 2. Check if Unity process is still alive
                if (!_processManager.IsUnityRunning(out _))
                {
                    // Brief grace period in case result was written as process exited
                    await Task.Delay(300, cancellationToken);
                    var finalCheck = TryReadJsonFile<UnityRefreshResult>(_processManager.RefreshResultFile, r => r.OperationId == opId);
                    if (finalCheck != null)
                    {
                        progress?.Report(new ProgressNotificationValue
                        {
                            Progress = 90,
                            Total = 100,
                            Message = "Compilation finished, waiting for Editor to settle..."
                        });

                        EnrichRefreshResultWithDiagnostics(finalCheck);

                        if (finalCheck.Success)
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

                        return finalCheck;
                    }

                    return new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = "Unity background process exited unexpectedly during refresh/compilation."
                    };
                }

                // 3. Check operation store
                var opState = TryReadJsonFile<UnityCliOperationState>(_processManager.OperationFile, o => o.OperationId == opId);
                if (opState != null && opState.Status == "Interrupted")
                {
                    return new UnityRefreshResult
                    {
                        OperationId = opId,
                        Success = false,
                        Interrupted = true,
                        Message = "Unity operation was interrupted by domain reload or editor restart."
                    };
                }

                // 4. Poll socket
                string? pollResp = await SendCommandAsync($"POLL_REFRESH {opId}", 2, cancellationToken);
                if (pollResp != null)
                {
                    if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Operation interrupted.";
                        return new UnityRefreshResult
                        {
                            OperationId = opId,
                            Success = false,
                            Interrupted = true,
                            Message = UnescapeLine(msg)
                        };
                    }

                    if (pollResp.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        return new UnityRefreshResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = $"Lost ownership of refresh operation: {pollResp}"
                        };
                    }

                    if (pollResp == "READY")
                    {
                        progress?.Report(new ProgressNotificationValue
                        {
                            Progress = 90,
                            Total = 100,
                            Message = "Compilation finished, waiting for Editor to settle..."
                        });

                        var result = TryReadJsonFile<UnityRefreshResult>(_processManager.RefreshResultFile, r => r.OperationId == opId)
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
                        await Task.Delay(200, cancellationToken);
                        string diag = ReadCompilationErrors();
                        return new UnityRefreshResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = !string.IsNullOrWhiteSpace(diag) ? diag : "Unity script compilation failed."
                        };
                    }
                }

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

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// Evaluates dynamic C# snippet in-memory against active Editor/Play Mode.
    /// </summary>
    public virtual async Task<UnityEvalResult> EvalAsync(string code, CancellationToken cancellationToken = default)
    {
        await _processManager.EnsureUnityRunningAsync(cancellationToken);

        string opId = Guid.NewGuid().ToString("N");
        string escapedCode = EscapeCode(code);
        string command = $"EVAL {opId} {escapedCode}";

        _logger.LogInformation("Sending EVAL operation {OpId}...", opId);
        string? initialResponse = await SendCommandAsync(command, 10, cancellationToken);

        // Check if result already available
        var immediateResult = TryReadJsonFile<UnityEvalResult>(_processManager.EvalResultFile, r => r.OperationId == opId);
        if (immediateResult != null) return immediateResult;

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityEvalResult { OperationId = opId, Success = false, Message = $"Unity is busy: {initialResponse}" };
        }
        if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
        {
            return new UnityEvalResult { OperationId = opId, Success = false, Message = UnescapeLine(initialResponse) };
        }

        try
        {
            // Poll until completion
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = TryReadJsonFile<UnityEvalResult>(_processManager.EvalResultFile, r => r.OperationId == opId);
                if (result != null) return result;

                if (!_processManager.IsUnityRunning(out _))
                {
                    await Task.Delay(300, cancellationToken);
                    var final = TryReadJsonFile<UnityEvalResult>(_processManager.EvalResultFile, r => r.OperationId == opId);
                    if (final != null) return final;

                    return new UnityEvalResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = "Unity background process exited unexpectedly during evaluation."
                    };
                }

                string? pollResp = await SendCommandAsync($"POLL_EVAL {opId}", 5, cancellationToken);
                if (pollResp != null)
                {
                    var fileRes = TryReadJsonFile<UnityEvalResult>(_processManager.EvalResultFile, r => r.OperationId == opId);
                    if (fileRes != null) return fileRes;

                    if (pollResp.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        string payload = pollResp.Length > 7 ? pollResp[7..].Trim() : "";
                        return new UnityEvalResult { OperationId = opId, Success = true, Payload = UnescapeLine(payload) };
                    }
                    if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 7 ? pollResp[7..].Trim() : "Evaluation failed.";
                        return new UnityEvalResult { OperationId = opId, Success = false, Message = UnescapeLine(msg) };
                    }
                    if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Evaluation interrupted.";
                        return new UnityEvalResult { OperationId = opId, Success = false, Interrupted = true, Message = UnescapeLine(msg) };
                    }
                    if (pollResp.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        return new UnityEvalResult { OperationId = opId, Success = false, Message = UnescapeLine(pollResp) };
                    }
                    if (string.Equals(pollResp, "IDLE", StringComparison.OrdinalIgnoreCase))
                    {
                        return new UnityEvalResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = "Evaluation is no longer recognized by the Editor (Editor is idle)."
                        };
                    }
                    if (pollResp.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        return new UnityEvalResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = $"Lost ownership of evaluation: {pollResp}"
                        };
                    }
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelOperationAsync(opId, "eval");
            throw;
        }
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
                string escaped = EscapeParam(arg ?? "");
                sb.Append(" \"").Append(escaped).Append('"');
            }
        }

        _logger.LogInformation("Sending EXECUTE_METHOD operation {OpId} for {MethodName}...", opId, methodName);
        string? initialResponse = await SendCommandAsync(sb.ToString(), 10, cancellationToken);

        var immediateResult = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
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
            return new UnityExecuteResult { OperationId = opId, Success = false, Message = UnescapeLine(initialResponse) };
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
                if (result != null)
                {
                    if (result.Success) ReportExecuteCompleted(progress);
                    return result;
                }

                if (!_processManager.IsUnityRunning(out _))
                {
                    await Task.Delay(300, cancellationToken);
                    var final = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
                    if (final != null)
                    {
                        if (final.Success) ReportExecuteCompleted(progress);
                        return final;
                    }

                    return new UnityExecuteResult
                    {
                        OperationId = opId,
                        Success = false,
                        Message = "Unity background process exited unexpectedly during method execution."
                    };
                }

                string? pollResp = await SendCommandAsync($"POLL_EXECUTE {opId}", 5, cancellationToken);
                if (pollResp != null)
                {
                    var fileRes = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
                    if (fileRes != null)
                    {
                        if (fileRes.Success) ReportExecuteCompleted(progress);
                        return fileRes;
                    }

                    if (pollResp.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        ReportExecuteCompleted(progress);
                        string payload = pollResp.Length > 7 ? pollResp[7..].Trim() : "";
                        return new UnityExecuteResult { OperationId = opId, Success = true, Payload = UnescapeLine(payload) };
                    }
                    if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 7 ? pollResp[7..].Trim() : "Method execution failed.";
                        return new UnityExecuteResult { OperationId = opId, Success = false, Message = UnescapeLine(msg) };
                    }
                    if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Method execution interrupted.";
                        return new UnityExecuteResult { OperationId = opId, Success = false, Interrupted = true, Message = UnescapeLine(msg) };
                    }
                    if (pollResp.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        return new UnityExecuteResult { OperationId = opId, Success = false, Message = UnescapeLine(pollResp) };
                    }
                    if (string.Equals(pollResp, "IDLE", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
                        if (res != null)
                        {
                            if (res.Success) ReportExecuteCompleted(progress);
                            return res;
                        }

                        return new UnityExecuteResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = "Method execution is no longer recognized by the Editor (Editor is idle)."
                        };
                    }
                    if (pollResp.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
                        if (res != null)
                        {
                            if (res.Success) ReportExecuteCompleted(progress);
                            return res;
                        }

                        return new UnityExecuteResult
                        {
                            OperationId = opId,
                            Success = false,
                            Message = $"Lost ownership of method execution: {pollResp}"
                        };
                    }
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelOperationAsync(opId, "execute");
            throw;
        }
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
            sb.Append(" --filter \"").Append(EscapeParam(filter)).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            sb.Append(" --category \"").Append(EscapeParam(category)).Append('"');
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

        var immediateResult = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
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
            var res = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
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

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var runningState = TryReadJsonFile<UnityTestRunState>(_processManager.TestRunningFile, s => s.RunId == opId);
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

                var result = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                if (result != null)
                {
                    ReportFinalProgress(progress, result);
                    return result;
                }

                if (!_processManager.IsUnityRunning(out _))
                {
                    await Task.Delay(300, cancellationToken);
                    var final = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                    if (final != null)
                    {
                        ReportFinalProgress(progress, final);
                        return final;
                    }

                    return new UnityTestRunResult
                    {
                        RunId = opId,
                        Success = false,
                        Message = "Unity background process exited unexpectedly during test run."
                    };
                }

                string? pollResp = await SendCommandAsync($"POLL_TESTS {opId}", 5, cancellationToken);
                if (pollResp != null)
                {
                    if (pollResp.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                        if (res != null)
                        {
                            ReportFinalProgress(progress, res);
                            return res;
                        }

                        return new UnityTestRunResult { RunId = opId, Success = true, Message = pollResp };
                    }
                    if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                        if (res != null)
                        {
                            ReportFinalProgress(progress, res);
                            return res;
                        }

                        return new UnityTestRunResult { RunId = opId, Success = false, Message = UnescapeLine(pollResp) };
                    }
                    if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Test run interrupted.";
                        return new UnityTestRunResult { RunId = opId, Success = false, ResultState = "Interrupted", Message = UnescapeLine(msg) };
                    }
                    if (pollResp.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        return new UnityTestRunResult { RunId = opId, Success = false, Message = UnescapeLine(pollResp) };
                    }
                    if (string.Equals(pollResp, "IDLE", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                        if (res != null)
                        {
                            ReportFinalProgress(progress, res);
                            return res;
                        }

                        return new UnityTestRunResult
                        {
                            RunId = opId,
                            Success = false,
                            Message = "Test run is no longer recognized by the Editor (Editor is idle)."
                        };
                    }
                    if (pollResp.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                        if (res != null)
                        {
                            ReportFinalProgress(progress, res);
                            return res;
                        }

                        return new UnityTestRunResult
                        {
                            RunId = opId,
                            Success = false,
                            Message = $"Lost ownership of test run: {pollResp}"
                        };
                    }
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelOperationAsync(opId, "test");
            throw;
        }
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

    private static string EscapeCode(string code)
    {
        return code
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string EscapeParam(string param)
    {
        return param
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string UnescapeLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("\\r", "\r").Replace("\\n", "\n");
    }

    private string ReadCompilationErrors()
    {
        if (File.Exists(_processManager.CompilationErrorsFile))
        {
            try
            {
                return UnityProcessManager.ReadFileWithRetry(_processManager.CompilationErrorsFile);
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

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
    /// Returns current Editor connection state: Ready, Not Running, Compiling, Running Unreachable.
    /// </summary>
    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_processManager.IsUnityRunning(out _))
        {
            return "Not Running";
        }

        int port = _processManager.ReadPortFile();
        if (port <= 0)
        {
            var op = TryReadJsonFile<UnityCliOperationState>(_processManager.OperationFile, _ => true);
            if (op != null && (op.Status == "Compiling" || op.Status == "Reloading" || op.Status == "Refreshing" || op.Status == "Recompiling"))
            {
                return "Compiling";
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
            return "Ready";
        }

        var activeOp = TryReadJsonFile<UnityCliOperationState>(_processManager.OperationFile, _ => true);
        if (activeOp != null && (activeOp.Status == "Compiling" || activeOp.Status == "Reloading" || activeOp.Status == "Refreshing" || activeOp.Status == "Recompiling"))
        {
            return "Compiling";
        }

        return "Running Unreachable";
    }

    /// <summary>
    /// Refreshes AssetDatabase, waits for compilation, and returns diagnostics.
    /// If isRecompile is true, triggers clean script recompilation.
    /// </summary>
    public async Task<UnityRefreshResult> RefreshAsync(bool isRecompile = false, CancellationToken cancellationToken = default)
    {
        string opId = Guid.NewGuid().ToString("N");
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

        // Poll until completion with domain reload resilience
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Authoritative check: Temp/unity_refresh_result.json
            var cachedResult = TryReadJsonFile<UnityRefreshResult>(_processManager.RefreshResultFile, r => r.OperationId == opId);
            if (cachedResult != null)
            {
                EnrichRefreshResultWithDiagnostics(cachedResult);
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
                    EnrichRefreshResultWithDiagnostics(finalCheck);
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
                        Message = msg
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
                    var result = TryReadJsonFile<UnityRefreshResult>(_processManager.RefreshResultFile, r => r.OperationId == opId)
                        ?? new UnityRefreshResult
                        {
                            OperationId = opId,
                            Success = true,
                            Message = "AssetDatabase refresh completed successfully."
                        };

                    EnrichRefreshResultWithDiagnostics(result);
                    return result;
                }

                if (pollResp == "COMPILATION_ERROR")
                {
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

            await Task.Delay(500, cancellationToken);
        }

        return new UnityRefreshResult
        {
            OperationId = opId,
            Success = false,
            Message = "Timed out waiting for AssetDatabase refresh / compilation to finish (120s)."
        };
    }

    /// <summary>
    /// Evaluates dynamic C# snippet in-memory against active Editor/Play Mode.
    /// </summary>
    public async Task<UnityEvalResult> EvalAsync(string code, CancellationToken cancellationToken = default)
    {
        await _processManager.EnsureUnityRunningAsync(cancellationToken);

        string opId = Guid.NewGuid().ToString("N");
        string escapedCode = EscapeCode(code);
        string command = $"EVAL {opId} {escapedCode}";

        _logger.LogInformation("Sending EVAL operation {OpId}...", opId);
        string? initialResponse = await SendCommandAsync(command, 30, cancellationToken);

        // Check if result already available
        var immediateResult = TryReadJsonFile<UnityEvalResult>(_processManager.EvalResultFile, r => r.OperationId == opId);
        if (immediateResult != null) return immediateResult;

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityEvalResult { OperationId = opId, Success = false, Message = $"Unity is busy: {initialResponse}" };
        }

        // Poll until completion
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
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
                    return new UnityEvalResult { OperationId = opId, Success = true, Payload = payload };
                }
                if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                {
                    string msg = pollResp.Length > 7 ? pollResp[7..].Trim() : "Evaluation failed.";
                    return new UnityEvalResult { OperationId = opId, Success = false, Message = msg };
                }
                if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                {
                    string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Evaluation interrupted.";
                    return new UnityEvalResult { OperationId = opId, Success = false, Interrupted = true, Message = msg };
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        var finalTimeoutResult = TryReadJsonFile<UnityEvalResult>(_processManager.EvalResultFile, r => r.OperationId == opId);
        if (finalTimeoutResult != null) return finalTimeoutResult;

        return new UnityEvalResult
        {
            OperationId = opId,
            Success = false,
            Message = "Timed out waiting for evaluation result (60s)."
        };
    }

    /// <summary>
    /// Invokes static C# method with arguments in Unity Editor.
    /// </summary>
    public async Task<UnityExecuteResult> ExecuteMethodAsync(string methodName, string[]? args, CancellationToken cancellationToken = default)
    {
        var refreshResult = await RefreshAsync(isRecompile: false, cancellationToken);
        if (!refreshResult.Success)
        {
            return new UnityExecuteResult
            {
                Success = false,
                Interrupted = refreshResult.Interrupted,
                Message = refreshResult.Message
            };
        }

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
        if (immediateResult != null) return immediateResult;

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityExecuteResult { OperationId = opId, Success = false, Message = $"Unity is busy: {initialResponse}" };
        }
        if (initialResponse != null && (initialResponse.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || initialResponse.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase)))
        {
            return new UnityExecuteResult { OperationId = opId, Success = false, Message = initialResponse };
        }

        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
            if (result != null) return result;

            if (!_processManager.IsUnityRunning(out _))
            {
                await Task.Delay(300, cancellationToken);
                var final = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
                if (final != null) return final;

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
                if (fileRes != null) return fileRes;

                if (pollResp.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = pollResp.Length > 7 ? pollResp[7..].Trim() : "";
                    return new UnityExecuteResult { OperationId = opId, Success = true, Payload = payload };
                }
                if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                {
                    string msg = pollResp.Length > 7 ? pollResp[7..].Trim() : "Method execution failed.";
                    return new UnityExecuteResult { OperationId = opId, Success = false, Message = msg };
                }
                if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                {
                    string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Method execution interrupted.";
                    return new UnityExecuteResult { OperationId = opId, Success = false, Interrupted = true, Message = msg };
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        var finalTimeoutResult = TryReadJsonFile<UnityExecuteResult>(_processManager.ExecuteResultFile, r => r.OperationId == opId);
        if (finalTimeoutResult != null) return finalTimeoutResult;

        return new UnityExecuteResult
        {
            OperationId = opId,
            Success = false,
            Message = "Timed out waiting for method execution result (120s)."
        };
    }

    /// <summary>
    /// Runs EditMode or PlayMode tests in Unity.
    /// </summary>
    public async Task<UnityTestRunResult> RunTestsAsync(string? filter, string? category, string? mode, CancellationToken cancellationToken = default)
    {
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
        string testMode = string.Equals(mode, "playmode", StringComparison.OrdinalIgnoreCase) ? "playmode" : "editmode";

        var sb = new StringBuilder($"RUN_TESTS {opId} {testMode}");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sb.Append(" --filter \"").Append(EscapeParam(filter)).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            sb.Append(" --category \"").Append(EscapeParam(category)).Append('"');
        }

        _logger.LogInformation("Sending RUN_TESTS operation {OpId} (mode: {Mode})...", opId, testMode);
        string? initialResponse = await SendCommandAsync(sb.ToString(), 10, cancellationToken);

        var immediateResult = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
        if (immediateResult != null) return immediateResult;

        if (initialResponse != null && initialResponse.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return new UnityTestRunResult { RunId = opId, Success = false, Message = $"Unity is busy: {initialResponse}" };
        }

        var deadline = DateTime.UtcNow.AddSeconds(300);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                if (result != null) return result;

                if (!_processManager.IsUnityRunning(out _))
                {
                    await Task.Delay(300, cancellationToken);
                    var final = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                    if (final != null) return final;

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
                        if (res != null) return res;

                        return new UnityTestRunResult { RunId = opId, Success = true, Message = pollResp };
                    }
                    if (pollResp.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
                        if (res != null) return res;

                        return new UnityTestRunResult { RunId = opId, Success = false, Message = pollResp };
                    }
                    if (pollResp.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = pollResp.Length > 12 ? pollResp[12..].Trim() : "Test run interrupted.";
                        return new UnityTestRunResult { RunId = opId, Success = false, ResultState = "Interrupted", Message = msg };
                    }
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cancellation requested. Sending CANCEL_TESTS for {OpId}...", opId);
            try
            {
                using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await SendCommandAsync($"CANCEL_TESTS {opId}", 3, cancelCts.Token);
            }
            catch { }
            throw;
        }

        var finalTimeout = TryReadJsonFile<UnityTestRunResult>(_processManager.TestResultsFile, r => r.RunId == opId);
        if (finalTimeout != null) return finalTimeout;

        return new UnityTestRunResult
        {
            RunId = opId,
            Success = false,
            Message = "Timed out waiting for test run to finish (300s)."
        };
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
}

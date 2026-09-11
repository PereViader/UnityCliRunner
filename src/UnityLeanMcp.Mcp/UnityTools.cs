using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnityLeanMcp.Mcp;

[McpServerToolType]
public class UnityTools
{
    private readonly IUnityClient _client;
    private readonly IUnityProcessManager _processManager;
    private readonly IUnityPathResolver _pathResolver;
    private readonly IDiagnosticFormatter _diagnosticFormatter;

    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        WriteIndented = true
    };

    public UnityTools(
        IUnityClient client,
        IUnityProcessManager processManager,
        IUnityPathResolver? pathResolver = null,
        IDiagnosticFormatter? diagnosticFormatter = null)
    {
        _client = client;
        _processManager = processManager;
        _pathResolver = pathResolver ?? processManager.PathResolver;
        _diagnosticFormatter = diagnosticFormatter ?? DiagnosticFormatter.Default;
    }

    [McpServerTool(Name = "unity_status", ReadOnly = true)]
    [Description("Returns current Editor connection state: Ready, Not Running, Compiling, Running Unreachable, or Busy (<operation>). Note: Unity automatically starts on demand when action tools are called.")]
    public async Task<CallToolResult> UnityStatusAsync(CancellationToken cancellationToken = default)
    {
        string status = await _client.GetStatusAsync(cancellationToken);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = status }],
            IsError = false
        };
    }

    [McpServerTool(Name = "unity_refresh")]
    [Description("Refreshes AssetDatabase and returns compiler diagnostics. Fast (<200ms) when unchanged. All tools auto-refresh pending changes before executing; do not call unity_refresh beforehand.")]
    public async Task<CallToolResult> UnityRefreshAsync(
        [Description("Optional. If true, forces a full clean rebuild by clearing the assembly compiler cache. Defaults to false; use only when recovering from corrupted cache or stale errors.")]
        bool clean = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.RefreshAsync(isRecompile: clean, progress, cancellationToken);
        var sb = new StringBuilder();

        string successMessage = clean
            ? "Clean script recompilation completed with 0 errors."
            : "AssetDatabase refresh completed with 0 errors.";
        string interruptedMessage = clean
            ? "Unity recompilation interrupted by domain reload or restart."
            : "Unity compilation interrupted by domain reload or restart.";
        string failedMessage = clean
            ? "Error: Unity recompilation failed."
            : "Error: Unity compilation failed.";

        if (result.Success)
        {
            if (string.IsNullOrWhiteSpace(result.Message) || result.Message == "AssetDatabase refresh completed successfully.")
            {
                sb.Append(successMessage);
            }
            else
            {
                sb.AppendLine(result.Message.TrimEnd());
                sb.Append(successMessage);
            }
        }
        else if (result.Interrupted)
        {
            string msg = !string.IsNullOrWhiteSpace(result.Message)
                ? result.Message
                : interruptedMessage;
            sb.Append(msg);
        }
        else if (result.Message?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
        {
            sb.Append(result.Message);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                sb.AppendLine(result.Message.TrimEnd());
            }
            if (result.Message == null || !result.Message.Contains(failedMessage))
            {
                sb.Append(failedMessage);
            }
        }

        var diagnostics = _diagnosticFormatter.ParseCompilerDiagnostics(result.Message);
        var structured = new StructuredRefreshResult
        {
            Success = result.Success,
            Interrupted = result.Interrupted,
            Message = result.Message ?? "",
            Diagnostics = diagnostics
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = sb.ToString().TrimEnd() },
                new TextContentBlock { Text = JsonSerializer.Serialize(structured, s_JsonOptions) }
            ],
            IsError = !result.Success
        };
    }

    [McpServerTool(Name = "unity_eval")]
    [Description("Evaluates C# snippet in-memory to query scene, GameObjects, and component state.")]
    public async Task<CallToolResult> UnityEvalAsync(
        [Description("C# expression, statement, or multi-statement snippet to evaluate in Unity Editor. Common imports (UnityEngine, UnityEditor, SceneManagement, UI, EventSystems, Animations, System.IO, Linq) are included by default.")] string code,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.EvalAsync(code, progress, cancellationToken);

        string logsText = "";
        if (result.Logs.Count > 0)
        {
            var logSb = new StringBuilder();
            foreach (var log in result.Logs)
            {
                if (log.LogType == "Warning")
                {
                    logSb.AppendLine($"[Warning] {log.Message}");
                }
                else if (log.LogType is "Error" or "Assert" or "Exception")
                {
                    logSb.AppendLine($"[{log.LogType}] {log.Message}");
                }
                else
                {
                    logSb.AppendLine(log.Message);
                }
            }
            logsText = logSb.ToString().TrimEnd();
        }

        bool hasLogs = !string.IsNullOrEmpty(logsText);
        string humanText;

        if (result.Success)
        {
            bool hasPayload = !string.IsNullOrEmpty(result.Payload);
            if (hasLogs && hasPayload)
            {
                humanText = $"Logs:{Environment.NewLine}{logsText}{Environment.NewLine}{Environment.NewLine}Result:{Environment.NewLine}{result.Payload!.TrimEnd()}";
            }
            else if (hasPayload)
            {
                humanText = result.Payload!.TrimEnd();
            }
            else if (hasLogs)
            {
                humanText = logsText;
            }
            else
            {
                humanText = "(Evaluation succeeded with no output)";
            }
        }
        else
        {
            string errorMsg;
            if (result.Interrupted)
            {
                errorMsg = string.IsNullOrWhiteSpace(result.Message)
                    ? "Command interrupted by Unity recompilation outside the UnityLeanMcp workflow."
                    : result.Message;
            }
            else
            {
                errorMsg = string.IsNullOrWhiteSpace(result.Message) ? "Evaluation failed." : result.Message;
            }

            if (hasLogs)
            {
                humanText = $"Logs:{Environment.NewLine}{logsText}{Environment.NewLine}{Environment.NewLine}Error:{Environment.NewLine}{errorMsg.TrimEnd()}";
            }
            else
            {
                humanText = errorMsg.TrimEnd();
            }
        }

        var structured = new StructuredEvalResult
        {
            Success = result.Success,
            Interrupted = result.Interrupted,
            Message = result.Message ?? "",
            Duration = result.Duration,
            Payload = result.Payload,
            Logs = result.Logs ?? new()
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = humanText },
                new TextContentBlock { Text = JsonSerializer.Serialize(structured, s_JsonOptions) }
            ],
            IsError = !result.Success
        };
    }


    [McpServerTool(Name = "unity_run_tests")]
    [Description("Runs EditMode/PlayMode tests with failure diagnostics.")]
    public async Task<CallToolResult> UnityRunTestsAsync(
        [Description("Test filter string (wildcards and class/method names supported).")] string? filter = null,
        [Description("Test category filter.")] string? category = null,
        [Description("Test execution mode: 'all' (default), 'editmode', or 'playmode'.")] string? mode = "all",
        [Description("Only run tests that previously failed.")] bool failedOnly = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        mode = string.IsNullOrWhiteSpace(mode) ? "all" : mode;
        var result = await _client.RunTestsAsync(filter, category, mode, failedOnly, progress, cancellationToken);
        var sb = new StringBuilder();

        bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        bool hasCategory = !string.IsNullOrWhiteSpace(category);
        bool hasFilterOrCategory = hasFilter || hasCategory;
        int totalTests = result.PassCount + result.FailCount + result.SkipCount;

        bool success = result.Success && result.FailCount == 0;

        if (result.ResultState == "CompileError")
        {
            sb.AppendLine("Test execution aborted: Script compilation failed.");
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                sb.AppendLine(result.Message);
            }
        }
        else if (result.ResultState == "Interrupted")
        {
            sb.AppendLine($"Test run interrupted: {result.Message}");
        }
        else if (hasFilterOrCategory && totalTests == 0)
        {
            success = false;
            if (hasFilter && hasCategory)
            {
                sb.AppendLine($"No tests found matching filter '{filter}' and category '{category}' (mode: {mode}).");
            }
            else if (hasFilter)
            {
                sb.AppendLine($"No tests found matching filter '{filter}' (mode: {mode}).");
            }
            else
            {
                sb.AppendLine($"No tests found matching category '{category}' (mode: {mode}).");
            }
        }
        else if (result.Success)
        {
            if (totalTests == 0)
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    sb.AppendLine(result.Message);
                }
                else
                {
                    sb.AppendLine("Tests Passed: 0 passed, 0 skipped (no tests found in suite).");
                }
            }
            else
            {
                sb.AppendLine($"Tests Passed: {result.PassCount} passed, {result.SkipCount} skipped.");
            }
        }
        else
        {
            sb.AppendLine($"Tests Failed: {result.FailCount} failed, {result.PassCount} passed, {result.SkipCount} skipped.");
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                sb.AppendLine(result.Message);
            }
        }

        var structuredFailures = new List<StructuredTestFailure>();
        for (int i = 0; i < result.FailedTests.Count; i++)
        {
            var fail = result.FailedTests[i];
            var (filePath, lineNumber, fileUri) = _diagnosticFormatter.ExtractSourceLocation(fail.StackTrace, _processManager.ProjectRoot);
            structuredFailures.Add(new StructuredTestFailure
            {
                Name = fail.Name,
                FullName = fail.FullName,
                Duration = fail.Duration,
                Message = fail.Message,
                StackTrace = fail.StackTrace,
                FilePath = filePath,
                LineNumber = lineNumber,
                FileUri = fileUri
            });
        }

        if (result.FailedTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            const int maxDetailedFailures = 25;
            int countToReport = Math.Min(result.FailedTests.Count, maxDetailedFailures);
            for (int i = 0; i < countToReport; i++)
            {
                var fail = structuredFailures[i];
                sb.AppendLine($"• {fail.FullName ?? fail.Name} ({fail.Duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}s)");
                if (!string.IsNullOrWhiteSpace(fail.FilePath) && fail.LineNumber.HasValue)
                {
                    string link = !string.IsNullOrWhiteSpace(fail.FileUri)
                        ? $"[{fail.FilePath}:{fail.LineNumber}]({fail.FileUri})"
                        : $"{fail.FilePath}:{fail.LineNumber}";
                    sb.AppendLine($"  Location: {link}");
                }
                if (!string.IsNullOrWhiteSpace(fail.Message))
                {
                    sb.AppendLine($"  Message: {fail.Message}");
                }
                if (!string.IsNullOrWhiteSpace(fail.StackTrace))
                {
                    sb.AppendLine($"  Stack trace:\n{fail.StackTrace}");
                }
            }

            if (result.FailedTests.Count > maxDetailedFailures)
            {
                int remaining = result.FailedTests.Count - maxDetailedFailures;
                sb.AppendLine($"... and {remaining} more failed test(s).");
            }
        }

        double totalDuration = result.Duration > 0
            ? result.Duration
            : structuredFailures.Sum(f => f.Duration);

        var structuredRunResult = new StructuredTestRunResult
        {
            Success = success,
            PassCount = result.PassCount,
            FailCount = result.FailCount,
            SkipCount = result.SkipCount,
            TotalCount = totalTests,
            Duration = totalDuration,
            ResultState = result.ResultState,
            Message = result.Message ?? "",
            Failures = structuredFailures
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = sb.ToString().TrimEnd() },
                new TextContentBlock { Text = JsonSerializer.Serialize(structuredRunResult, s_JsonOptions) }
            ],
            IsError = !success
        };
    }

    internal static (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
        => DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, projectRoot);

    internal static List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
        => DiagnosticFormatter.Default.ParseCompilerDiagnostics(diagnosticText);


    [McpServerTool(Name = "unity_stop")]
    [Description("Safely stops the running Unity background instance. Do NOT call this automatically after operations; keep the instance warm for speed. Only use when explicitly requested by the user, to recover from a freeze/hang, or to release project locks so the user can open the Unity GUI.")]
    public async Task<CallToolResult> UnityStopAsync(CancellationToken cancellationToken = default)
    {
        if (!_processManager.IsUnityRunning(out _))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Unity background instance is not running." }],
                IsError = false
            };
        }

        bool stopped = await _processManager.StopUnityAsync(cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = stopped ? "Stopped." : "Error: Unity background instance could not be stopped." }],
            IsError = !stopped
        };
    }
}

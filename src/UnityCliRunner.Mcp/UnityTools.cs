using System;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnityCliRunner.Mcp;

[McpServerToolType]
public class UnityTools
{
    private readonly UnityClient _client;
    private readonly UnityProcessManager _processManager;

    public UnityTools(UnityClient client, UnityProcessManager processManager)
    {
        _client = client;
        _processManager = processManager;
    }

    [McpServerTool(Name = "unity_status", ReadOnly = true)]
    [Description("Returns the current Unity Editor connection state (Ready, Not Running, Compiling, Running Unreachable). Note: Unity automatically starts on demand when action tools are called.")]
    public async Task<CallToolResult> UnityStatusAsync(CancellationToken cancellationToken = default)
    {
        string status = await _client.GetStatusAsync(cancellationToken);
        if (status == "Not Running")
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Status: Not Running (will auto-start on demand)" }],
                IsError = false
            };
        }

        if (status == "Ready")
        {
            var sb = new StringBuilder();
            sb.AppendLine("Status: Ready");
            string? editorVersion = _processManager.GetProjectEditorVersion();
            sb.AppendLine($"Editor Version: {editorVersion ?? "Unknown"}");
            sb.AppendLine($"Project Root: {_processManager.ProjectRoot}");
            _processManager.IsUnityRunning(out int? pid);
            sb.AppendLine($"PID: {(pid.HasValue ? pid.Value.ToString() : "Unknown")}");
            string mode = _processManager.GetUnityMode(pid);
            sb.AppendLine($"Mode: {mode}");
            int port = _processManager.ReadPortFile();
            sb.AppendLine($"Port: {(port > 0 ? port.ToString() : "Unknown")}");

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
                IsError = false
            };
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Status: {status}" }],
            IsError = false
        };
    }

    [McpServerTool(Name = "unity_refresh")]
    [Description("Refreshes the Unity AssetDatabase, triggers script compilation, waits for completion, and returns diagnostics.")]
    public async Task<CallToolResult> UnityRefreshAsync(
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.RefreshAsync(isRecompile: false, progress, cancellationToken);
        var sb = new StringBuilder();

        if (result.Success)
        {
            if (string.IsNullOrWhiteSpace(result.Message) || result.Message == "AssetDatabase refresh completed successfully.")
            {
                sb.Append("AssetDatabase refresh completed with 0 errors.");
            }
            else
            {
                sb.AppendLine(result.Message.TrimEnd());
                sb.Append("AssetDatabase refresh completed with 0 errors.");
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString() }],
                IsError = false
            };
        }

        if (result.Interrupted)
        {
            string msg = !string.IsNullOrWhiteSpace(result.Message)
                ? result.Message
                : "Unity compilation interrupted by domain reload or restart.";
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
            if (result.Message == null || !result.Message.Contains("Error: Unity compilation failed"))
            {
                sb.Append("Error: Unity compilation failed.");
            }
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            IsError = true
        };
    }

    [McpServerTool(Name = "unity_recompile")]
    [Description("Forces a clean script recompilation in the Unity Editor, waits for completion, and returns compilation diagnostics.")]
    public async Task<CallToolResult> UnityRecompileAsync(
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.RefreshAsync(isRecompile: true, progress, cancellationToken);
        var sb = new StringBuilder();

        if (result.Success)
        {
            if (string.IsNullOrWhiteSpace(result.Message) || result.Message == "AssetDatabase refresh completed successfully.")
            {
                sb.Append("Clean script recompilation completed with 0 errors.");
            }
            else
            {
                sb.AppendLine(result.Message.TrimEnd());
                sb.Append("Clean script recompilation completed with 0 errors.");
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString() }],
                IsError = false
            };
        }

        if (result.Interrupted)
        {
            string msg = !string.IsNullOrWhiteSpace(result.Message)
                ? result.Message
                : "Unity recompilation interrupted by domain reload or restart.";
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
            if (result.Message == null || !result.Message.Contains("Error: Unity recompilation failed"))
            {
                sb.Append("Error: Unity recompilation failed.");
            }
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            IsError = true
        };
    }

    [McpServerTool(Name = "unity_eval")]
    [Description("Evaluates dynamic C# snippet in-memory against active Editor/Play Mode session.")]
    public async Task<CallToolResult> UnityEvalAsync(
        [Description("C# expression, statement, or multi-statement snippet to evaluate in Unity Editor.")] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.EvalAsync(code, cancellationToken);
        var sb = new StringBuilder();

        if (result.Logs.Count > 0)
        {
            foreach (var log in result.Logs)
            {
                if (log.LogType == "Warning")
                {
                    sb.AppendLine($"[Warning] {log.Message}");
                }
                else if (log.LogType is "Error" or "Assert" or "Exception")
                {
                    sb.AppendLine($"[{log.LogType}] {log.Message}");
                }
                else
                {
                    sb.AppendLine(log.Message);
                }
            }
        }

        if (result.Success)
        {
            if (!string.IsNullOrEmpty(result.Payload))
            {
                sb.Append(result.Payload);
            }

            string text = sb.ToString().TrimEnd();
            if (string.IsNullOrEmpty(text))
            {
                text = "(Evaluation succeeded with no output)";
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }],
                IsError = false
            };
        }

        if (result.Interrupted)
        {
            sb.Append(string.IsNullOrWhiteSpace(result.Message)
                ? "Command interrupted by Unity recompilation outside the Unity CLI workflow."
                : result.Message);
        }
        else
        {
            sb.Append(string.IsNullOrWhiteSpace(result.Message) ? "Evaluation failed." : result.Message);
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            IsError = true
        };
    }

    [McpServerTool(Name = "unity_execute_method")]
    [Description("Invokes static C# method with arguments in Unity Editor.")]
    public async Task<CallToolResult> UnityExecuteMethodAsync(
        [Description("Fully qualified method name in format 'Namespace.Type.Method' or 'Type.Method'.")] string methodName,
        [Description("Optional array of string arguments passed to the method.")] string[]? args = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.ExecuteMethodAsync(methodName, args, progress, cancellationToken);
        var sb = new StringBuilder();

        if (result.Logs.Count > 0)
        {
            foreach (var log in result.Logs)
            {
                if (log.LogType == "Warning")
                {
                    sb.AppendLine($"[Warning] {log.Message}");
                }
                else if (log.LogType is "Error" or "Assert" or "Exception")
                {
                    sb.AppendLine($"[{log.LogType}] {log.Message}");
                }
                else
                {
                    sb.AppendLine(log.Message);
                }
            }
        }

        if (result.Success)
        {
            if (!string.IsNullOrEmpty(result.Payload))
            {
                sb.Append(result.Payload);
            }
            else
            {
                sb.Append("Method execution succeeded.");
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
                IsError = false
            };
        }

        if (result.Interrupted)
        {
            sb.Append($"Method execution interrupted: {result.Message}");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                sb.AppendLine(result.Message);
            }
            sb.Append("Method execution failed.");
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            IsError = true
        };
    }

    [McpServerTool(Name = "unity_run_tests")]
    [Description("Runs EditMode, PlayMode, or all tests in Unity (with optional rerun of previously failed tests) and returns pass/fail counts and failure diagnostics.")]
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

        if (result.FailedTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            const int maxDetailedFailures = 25;
            int countToReport = Math.Min(result.FailedTests.Count, maxDetailedFailures);
            for (int i = 0; i < countToReport; i++)
            {
                var fail = result.FailedTests[i];
                sb.AppendLine($"• {fail.FullName ?? fail.Name} ({fail.Duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}s)");
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

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            IsError = !success
        };
    }

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

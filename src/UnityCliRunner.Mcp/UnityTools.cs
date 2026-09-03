using System;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    [Description("Returns the current Unity Editor connection state (Ready, Not Running, Compiling, Running Unreachable).")]
    public async Task<CallToolResult> UnityStatusAsync(CancellationToken cancellationToken = default)
    {
        string status = await _client.GetStatusAsync(cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Status: {status}" }],
            IsError = false
        };
    }

    [McpServerTool(Name = "unity_refresh")]
    [Description("Refreshes the Unity AssetDatabase, triggers script compilation, waits for completion, and returns diagnostics.")]
    public async Task<CallToolResult> UnityRefreshAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.RefreshAsync(isRecompile: false, cancellationToken);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            sb.AppendLine(result.Message);
        }

        if (result.Success)
        {
            sb.Append("Unity is ready!");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString() }],
                IsError = false
            };
        }

        if (result.Interrupted)
        {
            sb.Append("Unity compilation interrupted by domain reload or restart.");
        }
        else
        {
            sb.Append("Error: Unity compilation failed.");
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }],
            IsError = true
        };
    }

    [McpServerTool(Name = "unity_recompile")]
    [Description("Forces a clean script recompilation in the Unity Editor, waits for completion, and returns compilation diagnostics.")]
    public async Task<CallToolResult> UnityRecompileAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.RefreshAsync(isRecompile: true, cancellationToken);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            sb.AppendLine(result.Message);
        }

        if (result.Success)
        {
            sb.Append("Unity is ready!");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString() }],
                IsError = false
            };
        }

        if (result.Interrupted)
        {
            sb.Append("Unity recompilation interrupted by domain reload or restart.");
        }
        else
        {
            sb.Append("Error: Unity recompilation failed.");
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }],
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
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
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
        CancellationToken cancellationToken = default)
    {
        var result = await _client.ExecuteMethodAsync(methodName, args, cancellationToken);
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
                sb.AppendLine(result.Payload);
            }
            sb.AppendLine("Unity Response: SUCCESS");
            sb.Append("Method execution succeeded.");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = sb.ToString() }],
                IsError = false
            };
        }

        if (result.Interrupted)
        {
            sb.Append($"Method execution interrupted: {result.Message}");
        }
        else
        {
            sb.AppendLine(result.Message);
            sb.Append("Method execution failed.");
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }],
            IsError = true
        };
    }

    [McpServerTool(Name = "unity_run_tests")]
    [Description("Runs EditMode or PlayMode tests in Unity and returns pass/fail counts and failure diagnostics.")]
    public async Task<CallToolResult> UnityRunTestsAsync(
        [Description("Test filter string (wildcards and class/method names supported).")] string? filter = null,
        [Description("Test category filter.")] string? category = null,
        [Description("Test execution mode: 'editmode' (default) or 'playmode'.")] string? mode = "editmode",
        CancellationToken cancellationToken = default)
    {
        var result = await _client.RunTestsAsync(filter, category, mode, cancellationToken);
        var sb = new StringBuilder();

        bool success = result.Success && result.FailCount == 0 && (result.PassCount > 0 || result.SkipCount > 0);

        if (result.Success)
        {
            sb.AppendLine($"Tests Passed: {result.PassCount} passed, {result.SkipCount} skipped.");
        }
        else if (result.ResultState == "Interrupted")
        {
            sb.AppendLine($"Test run interrupted: {result.Message}");
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
            foreach (var fail in result.FailedTests)
            {
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
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            IsError = !success
        };
    }

    [McpServerTool(Name = "unity_stop")]
    [Description("Safely stops the running Unity background instance.")]
    public async Task<CallToolResult> UnityStopAsync(CancellationToken cancellationToken = default)
    {
        bool stopped = await _processManager.StopUnityAsync(cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = stopped ? "Stopped." : "Error: Unity background instance could not be stopped." }],
            IsError = !stopped
        };
    }

    [McpServerTool(Name = "unity_start")]
    [Description("Starts a background Unity instance in batchmode and waits until ready.")]
    public async Task<CallToolResult> UnityStartAsync(CancellationToken cancellationToken = default)
    {
        if (_processManager.IsUnityRunning(out _))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Unity is already running." }],
                IsError = false
            };
        }

        await _processManager.EnsureUnityRunningAsync(cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = "Starting Unity background instance...\nStarted successfully!" }],
            IsError = false
        };
    }
}


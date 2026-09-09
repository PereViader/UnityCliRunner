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

namespace UnityCliRunner.Mcp;

[McpServerToolType]
public class UnityTools
{
    private readonly UnityClient _client;
    private readonly UnityProcessManager _processManager;

    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Regex s_StackTraceRegex = new(
        @"(?:(?:in|\bat\b|\()\s*)?(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-]+[\\/])[^:\r\n()]+):(?:line\s+)?(?<line>\d+)\)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_CompilerDiagnosticRegex = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<code>[A-Z0-9]+):\s*(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        string? editorVersion = _processManager.GetProjectEditorVersion();
        _processManager.IsUnityRunning(out int? pid);
        int port = _processManager.ReadPortFile();
        string? mode = pid.HasValue ? _processManager.GetUnityMode(pid) : null;
        string? activeOperation = ExtractActiveOperation(status);

        if (status == "Not Running")
        {
            var structuredNotRunning = new StructuredStatusResult
            {
                Status = "Not Running",
                EditorVersion = editorVersion,
                ProjectRoot = _processManager.ProjectRoot,
                Pid = null,
                Mode = null,
                Port = null,
                ActiveOperation = null
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = "Status: Not Running (will auto-start on demand)" },
                    new TextContentBlock { Text = JsonSerializer.Serialize(structuredNotRunning, s_JsonOptions) }
                ],
                IsError = false
            };
        }

        if (status == "Ready")
        {
            var sb = new StringBuilder();
            sb.AppendLine("Status: Ready");
            sb.AppendLine($"Editor Version: {editorVersion ?? "Unknown"}");
            sb.AppendLine($"Project Root: {_processManager.ProjectRoot}");
            sb.AppendLine($"PID: {(pid.HasValue ? pid.Value.ToString() : "Unknown")}");
            sb.AppendLine($"Mode: {mode ?? "Unknown"}");
            sb.AppendLine($"Port: {(port > 0 ? port.ToString() : "Unknown")}");

            var structuredReady = new StructuredStatusResult
            {
                Status = "Ready",
                EditorVersion = editorVersion,
                ProjectRoot = _processManager.ProjectRoot,
                Pid = pid,
                Mode = mode,
                Port = port > 0 ? port : null,
                ActiveOperation = null
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = sb.ToString().TrimEnd() },
                    new TextContentBlock { Text = JsonSerializer.Serialize(structuredReady, s_JsonOptions) }
                ],
                IsError = false
            };
        }

        var structuredOther = new StructuredStatusResult
        {
            Status = status,
            EditorVersion = editorVersion,
            ProjectRoot = _processManager.ProjectRoot,
            Pid = pid,
            Mode = mode,
            Port = port > 0 ? port : null,
            ActiveOperation = activeOperation
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = $"Status: {status}" },
                new TextContentBlock { Text = JsonSerializer.Serialize(structuredOther, s_JsonOptions) }
            ],
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
        }
        else if (result.Interrupted)
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

        var diagnostics = ParseCompilerDiagnostics(result.Message);
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
        }
        else if (result.Interrupted)
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

        var diagnostics = ParseCompilerDiagnostics(result.Message);
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
    [Description("Evaluates dynamic C# snippet in-memory (<80ms, no domain reload) against active Editor/Play Mode session. Primary data-gathering channel for inspecting Unity state.\nQuick patterns:\n- Active scene: SceneManager.GetActiveScene()\n- Find GameObjects: GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None)\n- Find components: GameObject.FindObjectsByType<Camera>(FindObjectsSortMode.None)\n- Inspect hierarchy: Selection.activeTransform or GameObject.Find(\"Player\")?.transform\n- Serialized properties: new SerializedObject(Selection.activeObject).FindProperty(\"m_Name\")\n- Asset database: AssetDatabase.FindAssets(\"t:Prefab\")")]
    public async Task<CallToolResult> UnityEvalAsync(
        [Description("C# expression, statement, or multi-statement snippet to evaluate in Unity Editor. Common imports (UnityEngine, UnityEditor, SceneManagement, UI, EventSystems, Animations, System.IO, Linq) are included by default.")] string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.EvalAsync(code, cancellationToken);

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
                    ? "Command interrupted by Unity recompilation outside the Unity CLI workflow."
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

        var structuredFailures = new List<StructuredTestFailure>();
        for (int i = 0; i < result.FailedTests.Count; i++)
        {
            var fail = result.FailedTests[i];
            var (filePath, lineNumber, fileUri) = ExtractSourceLocation(fail.StackTrace, _processManager.ProjectRoot);
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
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return (null, null, null);

        using var reader = new StringReader(stackTrace);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Contains("<filename unknown>", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = s_StackTraceRegex.Match(line);
            if (match.Success)
            {
                string rawFile = match.Groups["file"].Value.Trim();
                if (int.TryParse(match.Groups["line"].Value, out int lineNum))
                {
                    string resolvedPath = rawFile;
                    if (!Path.IsPathRooted(resolvedPath) && !string.IsNullOrEmpty(projectRoot))
                    {
                        resolvedPath = Path.Combine(projectRoot, resolvedPath);
                    }

                    string normalizedPath = resolvedPath.Replace('\\', '/');
                    string fileUri = normalizedPath.StartsWith('/')
                        ? $"file://{normalizedPath}#L{lineNum}"
                        : $"file:///{normalizedPath}#L{lineNum}";

                    return (rawFile, lineNum, fileUri);
                }
            }
        }

        return (null, null, null);
    }

    internal static List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
    {
        var diagnostics = new List<StructuredCompilerDiagnostic>();
        if (string.IsNullOrWhiteSpace(diagnosticText))
            return diagnostics;

        using var reader = new StringReader(diagnosticText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = s_CompilerDiagnosticRegex.Match(line.Trim());
            if (match.Success)
            {
                diagnostics.Add(new StructuredCompilerDiagnostic
                {
                    File = match.Groups["file"].Value,
                    Line = int.Parse(match.Groups["line"].Value),
                    Column = int.Parse(match.Groups["col"].Value),
                    Severity = match.Groups["severity"].Value.ToLowerInvariant(),
                    Code = match.Groups["code"].Value,
                    Message = match.Groups["msg"].Value.Trim(),
                    Assembly = null
                });
            }
        }

        return diagnostics;
    }

    private string? ExtractActiveOperation(string status)
    {
        if (File.Exists(_processManager.OperationFile))
        {
            try
            {
                string json = UnityProcessManager.ReadFileWithRetry(_processManager.OperationFile, maxRetries: 2, delayMs: 20);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var op = JsonSerializer.Deserialize<UnityCliOperationState>(json);
                    if (op != null && !string.IsNullOrEmpty(op.Kind))
                    {
                        return op.Kind;
                    }
                }
            }
            catch { }
        }

        if (status.StartsWith("Busy (", StringComparison.OrdinalIgnoreCase) && status.EndsWith(")"))
        {
            string inner = status.Substring(6, status.Length - 7).Trim();
            int commaIdx = inner.IndexOf(',');
            return commaIdx >= 0 ? inner.Substring(0, commaIdx).Trim() : inner;
        }

        return null;
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

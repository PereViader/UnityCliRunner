using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

public class ToolFormattingTests
{
    private sealed class FakeUnityProcessManager : UnityProcessManager
    {
        public bool Running { get; set; }
        public int? Pid { get; set; }
        public string Mode { get; set; } = "Batchmode";
        public bool StopSuccess { get; set; } = true;

        public FakeUnityProcessManager(string projectRoot)
            : base(projectRoot, NullLogger<UnityProcessManager>.Instance)
        {
        }

        public override bool IsUnityRunning(out int? processId)
        {
            processId = Running ? Pid : null;
            return Running;
        }

        public override string GetUnityMode(int? pid = null) => Mode;

        public override Task<bool> StopUnityAsync(CancellationToken cancellationToken = default)
        {
            if (StopSuccess)
            {
                Running = false;
            }
            return Task.FromResult(StopSuccess);
        }
    }

    private sealed class FakeUnityClient : UnityClient
    {
        public string StatusToReturn { get; set; } = "Ready";
        public UnityRefreshResult RefreshResultToReturn { get; set; } = new();
        public UnityEvalResult EvalResultToReturn { get; set; } = new();
        public UnityExecuteResult ExecuteResultToReturn { get; set; } = new();
        public UnityTestRunResult TestRunResultToReturn { get; set; } = new();

        public FakeUnityClient(UnityProcessManager pm)
            : base(pm, NullLogger<UnityClient>.Instance)
        {
        }

        public override Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StatusToReturn);

        public override Task<UnityRefreshResult> RefreshAsync(
            bool isRecompile = false,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshResultToReturn);

        public override Task<UnityEvalResult> EvalAsync(string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(EvalResultToReturn);

        public override Task<UnityExecuteResult> ExecuteMethodAsync(
            string methodName,
            string[]? args,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExecuteResultToReturn);

        public override Task<UnityTestRunResult> RunTestsAsync(
            string? filter,
            string? category,
            string? mode,
            bool failedOnly = false,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TestRunResultToReturn);
    }

    private static (string tempDir, FakeUnityProcessManager pm, FakeUnityClient client, UnityTools tools) CreateTestContext()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_fmt_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        Directory.CreateDirectory(Path.Combine(tempDir, "ProjectSettings"));

        var pm = new FakeUnityProcessManager(tempDir);
        var client = new FakeUnityClient(pm);
        var tools = new UnityTools(client, pm);

        return (tempDir, pm, client, tools);
    }

    private static string GetResultText(CallToolResult result)
    {
        return result.Content.Count > 0 && result.Content[0] is TextContentBlock textBlock
            ? textBlock.Text
            : "";
    }

    // ==========================================
    // 1. unity_status tests
    // ==========================================

    [Fact]
    public async Task UnityStatus_WhenReady_IncludesKeyDiagnosticContext()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.32f1\n");
            File.WriteAllText(Path.Combine(tempDir, "Temp", "unity_cli_port.txt"), "54321");

            pm.Running = true;
            pm.Pid = 9988;
            pm.Mode = "Batchmode";
            client.StatusToReturn = "Ready";

            var result = await tools.UnityStatusAsync();

            Assert.False(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("Status: Ready", text);
            Assert.Contains("Editor Version: 6000.0.32f1", text);
            Assert.Contains($"Project Root: {pm.ProjectRoot}", text);
            Assert.Contains("PID: 9988", text);
            Assert.Contains("Mode: Batchmode", text);
            Assert.Contains("Port: 54321", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStatus_WhenNotRunning_ReturnsAutoStartMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = false;
            client.StatusToReturn = "Not Running";

            var result = await tools.UnityStatusAsync();

            Assert.False(result.IsError);
            Assert.Equal("Status: Not Running (will auto-start on demand)", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStatus_WhenBusy_ReportsActiveOperationAndStartTime()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.StatusToReturn = "Busy (execute, started 2026-09-08T12:00:00.0000000Z)";

            var result = await tools.UnityStatusAsync();

            Assert.False(result.IsError);
            Assert.Equal("Status: Busy (execute, started 2026-09-08T12:00:00.0000000Z)", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStatus_WhenCompiling_ReportsCompilingStatus()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.StatusToReturn = "Compiling";

            var result = await tools.UnityStatusAsync();

            Assert.False(result.IsError);
            Assert.Equal("Status: Compiling", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void UnityProcessManager_GetUnityMode_DistinguishesBatchmodeAndGui()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_mode_test_" + Guid.NewGuid().ToString("N"));
        string tempSubDir = Path.Combine(tempDir, "Temp");
        Directory.CreateDirectory(tempSubDir);
        try
        {
            var realPm = new UnityProcessManager(tempDir, NullLogger<UnityProcessManager>.Instance);
            int testPid = Environment.ProcessId;

            // When PidFile exists with matching PID -> Batchmode
            File.WriteAllText(realPm.PidFile, testPid.ToString());
            Assert.Equal("Batchmode", realPm.GetUnityMode(testPid));

            // When PidFile does not exist -> GUI
            File.Delete(realPm.PidFile);
            Assert.Equal("GUI", realPm.GetUnityMode(testPid));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 2. unity_refresh & unity_recompile tests
    // ==========================================

    [Fact]
    public async Task UnityRefresh_WhenClean_ReturnsZeroErrorsMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = ""
            };

            var result = await tools.UnityRefreshAsync();

            Assert.False(result.IsError);
            Assert.Equal("AssetDatabase refresh completed with 0 errors.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRecompile_WhenClean_ReturnsZeroErrorsMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = true,
                Message = ""
            };

            var result = await tools.UnityRecompileAsync();

            Assert.False(result.IsError);
            Assert.Equal("Clean script recompilation completed with 0 errors.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenBusy_ReportsBusyWithoutClaimingCompilationFailed()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Unity is busy with another operation: BUSY test"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Unity is busy with another operation: BUSY test", text);
            Assert.DoesNotContain("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRecompile_WhenBusy_ReportsBusyWithoutClaimingRecompilationFailed()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Message = "Unity is busy with another operation: BUSY test"
            };

            var result = await tools.UnityRecompileAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Unity is busy with another operation: BUSY test", text);
            Assert.DoesNotContain("Error: Unity recompilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenInterrupted_ReportsInterruptionCleanly()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Interrupted = true,
                Message = "Unity operation was interrupted by domain reload or editor restart."
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Unity operation was interrupted by domain reload or editor restart.", text);
            Assert.DoesNotContain("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRefresh_WhenCompilationFails_ReportsDiagnosticsAndError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.RefreshResultToReturn = new UnityRefreshResult
            {
                Success = false,
                Interrupted = false,
                Message = "Assets/Scripts/Foo.cs(10,5): error CS0103: The name 'bar' does not exist in the current context"
            };

            var result = await tools.UnityRefreshAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("error CS0103", text);
            Assert.Contains("Error: Unity compilation failed.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 3. unity_execute_method tests
    // ==========================================

    [Fact]
    public async Task UnityExecuteMethod_WithPayload_ReturnsOnlyPayloadWithoutTrailingBoilerplate()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.ExecuteResultToReturn = new UnityExecuteResult
            {
                Success = true,
                Payload = "{\"id\":42,\"status\":\"ok\"}",
                Logs = []
            };

            var result = await tools.UnityExecuteMethodAsync("MyNamespace.MyClass.GetJson");

            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("{\"id\":42,\"status\":\"ok\"}", text);
            Assert.DoesNotContain("Unity Response: SUCCESS", text);
            Assert.DoesNotContain("Method execution succeeded.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityExecuteMethod_WithPayloadAndLogs_ReturnsLogsAndPayload()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.ExecuteResultToReturn = new UnityExecuteResult
            {
                Success = true,
                Payload = "hello world",
                Logs =
                [
                    new ConsoleLogEntry { LogType = "Log", Message = "Executing method..." },
                    new ConsoleLogEntry { LogType = "Warning", Message = "Sample warning" }
                ]
            };

            var result = await tools.UnityExecuteMethodAsync("MyNamespace.MyClass.LogAndReturn");

            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Executing method...", text);
            Assert.Contains("[Warning] Sample warning", text);
            Assert.Contains("hello world", text);
            Assert.DoesNotContain("Method execution succeeded.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityExecuteMethod_VoidWithNoPayload_ReturnsMethodExecutionSucceeded()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.ExecuteResultToReturn = new UnityExecuteResult
            {
                Success = true,
                Payload = null,
                Logs = []
            };

            var result = await tools.UnityExecuteMethodAsync("MyNamespace.MyClass.VoidMethod");

            Assert.False(result.IsError);
            Assert.Equal("Method execution succeeded.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 4. unity_eval tests
    // ==========================================

    [Fact]
    public async Task UnityEval_VoidWithNoLogs_ReturnsPlaceholderMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = null,
                Logs = []
            };

            var result = await tools.UnityEvalAsync("Time.timeScale = 1.0f;");

            Assert.False(result.IsError);
            Assert.Equal("(Evaluation succeeded with no output)", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WithPayload_ReturnsPayload()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = "100",
                Logs = []
            };

            var result = await tools.UnityEvalAsync("50 * 2");

            Assert.False(result.IsError);
            Assert.Equal("100", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityEval_WithLogs_ReturnsLogs()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.EvalResultToReturn = new UnityEvalResult
            {
                Success = true,
                Payload = null,
                Logs = [new ConsoleLogEntry { LogType = "Log", Message = "Logged message from snippet" }]
            };

            var result = await tools.UnityEvalAsync("Debug.Log(\"Logged message from snippet\");");

            Assert.False(result.IsError);
            Assert.Equal("Logged message from snippet", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 5. unity_run_tests tests
    // ==========================================

    [Fact]
    public async Task UnityRunTests_CompileError_FormatsAsTestExecutionAborted()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "CompileError",
                FailCount = 0,
                PassCount = 0,
                SkipCount = 0,
                Message = "Assets/Scripts/Test.cs(12,8): error CS1002: ; expected"
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.StartsWith("Test execution aborted: Script compilation failed.", text);
            Assert.Contains("error CS1002", text);
            Assert.DoesNotContain("Tests Failed: 0 failed", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_MoreThan25Failures_CapsDetailedOutputAndSummarizesRemainder()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var failedTests = new List<FailedTestInfo>();
            for (int i = 0; i < 30; i++)
            {
                failedTests.Add(new FailedTestInfo
                {
                    Name = $"Test_Method_{i}",
                    FullName = $"MySuite.Test_Method_{i}",
                    Message = $"Assertion failed in test {i}",
                    StackTrace = $"at MySuite.Test_Method_{i}() line {i}",
                    Duration = 0.05
                });
            }

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "Failed",
                FailCount = 30,
                PassCount = 10,
                SkipCount = 2,
                FailedTests = failedTests
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("Tests Failed: 30 failed, 10 passed, 2 skipped.", text);
            Assert.Contains("• MySuite.Test_Method_0", text);
            Assert.Contains("• MySuite.Test_Method_24", text);
            Assert.DoesNotContain("• MySuite.Test_Method_25", text);
            Assert.Contains("... and 5 more failed test(s).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityRunTests_Under25Failures_ShowsAllWithoutSummaryLine()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var failedTests = new List<FailedTestInfo>();
            for (int i = 0; i < 3; i++)
            {
                failedTests.Add(new FailedTestInfo
                {
                    Name = $"Test_{i}",
                    FullName = $"Suite.Test_{i}",
                    Message = $"Fail {i}",
                    Duration = 0.01
                });
            }

            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = false,
                ResultState = "Failed",
                FailCount = 3,
                PassCount = 5,
                SkipCount = 0,
                FailedTests = failedTests
            };

            var result = await tools.UnityRunTestsAsync();

            Assert.True(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("• Suite.Test_0", text);
            Assert.Contains("• Suite.Test_1", text);
            Assert.Contains("• Suite.Test_2", text);
            Assert.DoesNotContain("more failed test(s).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ==========================================
    // 6. unity_stop tests
    // ==========================================

    [Fact]
    public async Task UnityStop_WhenNotRunning_ReturnsNotRunningMessageWithNoError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = false;

            var result = await tools.UnityStopAsync();

            Assert.False(result.IsError);
            Assert.Equal("Unity background instance is not running.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStop_WhenRunningAndStopped_ReturnsStopped()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = true;
            pm.StopSuccess = true;

            var result = await tools.UnityStopAsync();

            Assert.False(result.IsError);
            Assert.Equal("Stopped.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityStop_WhenRunningAndFailsToStop_ReturnsErrorMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            pm.Running = true;
            pm.StopSuccess = false;

            var result = await tools.UnityStopAsync();

            Assert.True(result.IsError);
            Assert.Equal("Error: Unity background instance could not be stopped.", GetResultText(result));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

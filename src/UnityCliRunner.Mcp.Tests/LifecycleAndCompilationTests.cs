using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

[Collection("UnityIntegration")]
public class LifecycleAndCompilationTests
{
    private readonly UnityIntegrationFixture _fixture;

    public LifecycleAndCompilationTests(UnityIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestBackgroundStatusOnline_ReturnsReadyWhenEditorIsRunning()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_status");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Status: Ready", result.Text);
    }

    [Fact]
    public async Task TestRefresh_TriggersAssetDatabaseRefreshSuccessfully()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestRefresh");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_refresh");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("AssetDatabase refresh completed with 0 errors.", result.Text);
    }

    [Fact]
    public async Task TestRecompile_TriggersCleanScriptRecompilationSuccessfully()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestRecompile");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_recompile");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Clean script recompilation completed with 0 errors.", result.Text);
    }

    [Fact]
    public async Task TestPollRefreshNonBlocking_PollsRefreshStateWithoutBlocking()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestPollRefreshNonBlocking");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.PollRefreshWhileBusy"
        });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestPollExecuteNonBlocking_PollsExecuteStateWithoutBlocking()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestPollExecuteNonBlocking");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.PollHandlersWhileBusy"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("OK|EXECUTE:RUNNING", result.Text);
        Assert.Contains("BUSY_EXECUTE:BUSY execute", result.Text);
        Assert.Contains("EVAL:BUSY execute", result.Text);
        Assert.Contains("TESTS:BUSY execute", result.Text);
    }

    [Fact]
    public async Task TestBusyDetectionBeforeRefresh_RejectsConcurrentMutatingOperations()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestBusyDetectionBeforeRefresh");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.TestBusyDetection"
        });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestStatusReportsBusy_WhenOperationIsRecorded()
    {
        string operationFile = Path.Combine(_fixture.UnityRoot, "Temp", "unity_cli_operation.json");
        string opId = Guid.NewGuid().ToString("N");
        string testOperationJson = $"{{\"operationId\":\"{opId}\",\"kind\":\"execute\",\"status\":\"Running\",\"editorSessionId\":\"test\",\"startedUtc\":\"2026-09-08T12:00:00.0000000Z\",\"updatedUtc\":\"2026-09-08T12:00:00.0000000Z\"}}";

        try
        {
            await File.WriteAllTextAsync(operationFile, testOperationJson);

            await using (var client = new McpTestClient(_fixture.UnityRoot))
            {
                var result = await client.CallToolAsync("unity_status");

                Assert.False(result.IsError, result.Text);
                Assert.Contains("Status: Busy (execute, started 2026-09-08T12:00:00.0000000Z)", result.Text);
            }
        }
        finally
        {
            DeleteFileWithRetry(operationFile);
        }

        await using (var client = new McpTestClient(_fixture.UnityRoot))
        {
            var result = await client.CallToolAsync("unity_status");
            Assert.False(result.IsError, result.Text);
            Assert.Contains("Status: Ready", result.Text);
        }
    }

    [Fact]
    public async Task TestExecuteMethodException_DoesNotLeaveStoreBusy()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteFailure");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var failResult = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.FailMethod"
        });

        Assert.True(failResult.IsError);
        Assert.Contains("Method execution failed.", failResult.Text);

        string operationFile = Path.Combine(_fixture.UnityRoot, "Temp", "unity_cli_operation.json");
        Assert.False(File.Exists(operationFile), "unity_cli_operation.json should have been cleaned up after failure.");

        var statusResult = await client.CallToolAsync("unity_status");
        Assert.False(statusResult.IsError, statusResult.Text);
        Assert.Contains("Status: Ready", statusResult.Text);
    }

    [Fact]
    public async Task TestStopUnity_PurgesOperationAndMarkerFiles()
    {
        string tempProject = Path.Combine(Path.GetTempPath(), "UnityCliTestPurge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempProject, "Temp"));

        try
        {
            var pm = new UnityProcessManager(tempProject, NullLogger<UnityProcessManager>.Instance);

            await File.WriteAllTextAsync(pm.OperationFile, "{\"operationId\":\"test\"}");
            await File.WriteAllTextAsync(pm.ExecuteRunningFile, "{\"operationId\":\"test\"}");
            await File.WriteAllTextAsync(pm.EvalRunningFile, "{\"operationId\":\"test\"}");
            await File.WriteAllTextAsync(pm.TestRunningFile, "{\"operationId\":\"test\"}");

            Assert.True(File.Exists(pm.OperationFile));
            Assert.True(File.Exists(pm.ExecuteRunningFile));
            Assert.True(File.Exists(pm.EvalRunningFile));
            Assert.True(File.Exists(pm.TestRunningFile));

            pm.PurgeOperationState();

            Assert.False(File.Exists(pm.OperationFile), "OperationFile was not purged");
            Assert.False(File.Exists(pm.ExecuteRunningFile), "ExecuteRunningFile was not purged");
            Assert.False(File.Exists(pm.EvalRunningFile), "EvalRunningFile was not purged");
            Assert.False(File.Exists(pm.TestRunningFile), "TestRunningFile was not purged");
        }
        finally
        {
            try { Directory.Delete(tempProject, true); } catch { }
        }
    }

    [Fact]
    public async Task TestCancelOperation_NonCancelableOperation_ReturnsNotCancelableAndRemainsBusy()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string operationFile = pm.OperationFile;
        string opId = Guid.NewGuid().ToString("N");
        string testOperationJson = $"{{\"operationId\":\"{opId}\",\"kind\":\"recompile\",\"status\":\"Compiling\",\"editorSessionId\":\"test\",\"startedUtc\":\"{DateTime.UtcNow:o}\",\"updatedUtc\":\"{DateTime.UtcNow:o}\"}}";

        try
        {
            await File.WriteAllTextAsync(operationFile, testOperationJson);
            Assert.True(File.Exists(operationFile));

            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                using var stream = tcpClient.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);

                await writer.WriteLineAsync($"CANCEL_OPERATION {opId}");
                string? response = await reader.ReadLineAsync();

                Assert.Equal("NOT_CANCELABLE", response);
            }

            Assert.True(File.Exists(operationFile), "Non-cancelable operation file must remain busy in the store.");
        }
        finally
        {
            DeleteFileWithRetry(operationFile);
        }
    }

    [Fact]
    public async Task TestCancelOperation_EvalSnippetWithCancellationToken_CancelsSuccessfully()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        DeleteFileWithRetry(pm.OperationFile);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string opId = Guid.NewGuid().ToString("N");

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"EVAL {opId} await Task.Delay(15000, cancellationToken); return 123;");
            string? ack = await reader.ReadLineAsync();
            Assert.Equal("RUNNING", ack);
        }

        using (var cancelClient = new TcpClient())
        {
            await cancelClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = cancelClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"CANCEL_OPERATION {opId}");
            string? cancelResp = await reader.ReadLineAsync();
            Assert.Equal("CANCELLED", cancelResp);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(pm.OperationFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.False(File.Exists(pm.OperationFile), "Operation file should be cleared when eval cancels.");

        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        string status = await client.GetStatusAsync();
        Assert.Equal("Ready", status);
    }

    [Fact]
    public async Task TestCancelOperation_CancellableExecuteMethod_CancelsSuccessfully()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteCancellable");
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        await client.RefreshAsync();

        DeleteFileWithRetry(pm.OperationFile);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string opId = Guid.NewGuid().ToString("N");

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"EXECUTE_METHOD {opId} Tests.DummyExecuteClass.CancellableMethod");
            string? ack = await reader.ReadLineAsync();
            Assert.Equal("RUNNING", ack);
        }

        using (var cancelClient = new TcpClient())
        {
            await cancelClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = cancelClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"CANCEL_OPERATION {opId}");
            string? cancelResp = await reader.ReadLineAsync();
            Assert.Equal("CANCELLED", cancelResp);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(pm.OperationFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.False(File.Exists(pm.OperationFile), "Operation file should be cleared when method execution cancels.");

        string status = await client.GetStatusAsync();
        Assert.Equal("Ready", status);
    }

    [Fact]
    public async Task TestCancelOperation_NonCancellableExecuteMethod_ReturnsNotCancelable()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteCancellable");
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);
        await client.RefreshAsync();

        DeleteFileWithRetry(pm.OperationFile);
        int port = pm.ReadPortFile();
        Assert.True(port > 0, "Unity port should be valid.");

        string opId = Guid.NewGuid().ToString("N");

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"EXECUTE_METHOD {opId} Tests.DummyExecuteClass.NonCancellableMethod");
            string? ack = await reader.ReadLineAsync();
            Assert.Equal("RUNNING", ack);
        }

        using (var cancelClient = new TcpClient())
        {
            await cancelClient.ConnectAsync(IPAddress.Loopback, port);
            using var stream = cancelClient.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await writer.WriteLineAsync($"CANCEL_OPERATION {opId}");
            string? cancelResp = await reader.ReadLineAsync();
            Assert.Equal("NOT_CANCELABLE", cancelResp);
        }

        Assert.True(File.Exists(pm.OperationFile), "Operation file should NOT be cancelled prematurely.");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(pm.OperationFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.False(File.Exists(pm.OperationFile), "Operation file should be cleared once method completes naturally.");
    }

    [Fact]
    public async Task TestClientCancellation_EvalAsyncWithCancellationToken_ThrowsAndUnwinds()
    {
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        DeleteFileWithRetry(pm.OperationFile);

        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.EvalAsync("await Task.Delay(10000, cancellationToken); return 42;", cts.Token);
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(pm.OperationFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.False(File.Exists(pm.OperationFile), "Operation file should not remain after cancellation.");

        string status = await client.GetStatusAsync();
        Assert.Equal("Ready", status);
    }

    [Fact]
    public async Task TestClientCancellation_ExecuteMethodWithCancellationToken_ThrowsAndUnwinds()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteCancellable");
        var pm = new UnityProcessManager(_fixture.UnityRoot, NullLogger<UnityProcessManager>.Instance);
        DeleteFileWithRetry(pm.OperationFile);

        var client = new UnityClient(pm, NullLogger<UnityClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.ExecuteMethodAsync("Tests.DummyExecuteClass.CancellableWithArgMethod", new[] { "myArg" }, cts.Token);
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(pm.OperationFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.False(File.Exists(pm.OperationFile), "Operation file should not remain after cancellation.");

        string status = await client.GetStatusAsync();
        Assert.Equal("Ready", status);
    }

    private static void DeleteFileWithRetry(string path, int maxRetries = 10, int delayMs = 50)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }
}

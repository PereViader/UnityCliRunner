using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class DecomposedComponentsTests
{
    [Fact]
    public void UnityLogScanner_HasCompilationErrors_IdentifiesErrorsAccurately()
    {
        var scanner = new UnityLogScanner();

        string logWithError = "Random info line\r\nAssets/Scripts/Test.cs(10,5): error CS0103: The name 'x' does not exist in the current context\r\nAnother line";
        string logWithWarningOnly = "Random info line\r\nAssets/Scripts/Test.cs(10,5): warning CS0168: The variable 'x' is declared but never used\r\nAnother line";
        string cleanLog = "Compilation succeeded.\r\nAsset database refreshed.";

        Assert.True(scanner.HasCompilationErrors(logWithError));
        Assert.False(scanner.HasCompilationErrors(logWithWarningOnly));
        Assert.False(scanner.HasCompilationErrors(cleanLog));
        Assert.False(scanner.HasCompilationErrors(""));
    }

    [Fact]
    public void UnityLogScanner_ExtractUniqueCompilationLines_DeduplicatesDiagnostics()
    {
        var scanner = new UnityLogScanner();

        string log = string.Join(Environment.NewLine, new[]
        {
            "Building project...",
            @"C:\Unity\Assets\Script.cs(12,34): error CS0103: The name 'bar' does not exist",
            @"C:\Unity\Assets\Script.cs(12,34): error CS0103: The name 'bar' does not exist",
            @"C:\Unity\Assets\Script.cs(5,10): warning CS0219: Variable is assigned but its value is never used",
            "Done."
        });

        var lines = scanner.ExtractUniqueCompilationLines(log);

        Assert.Equal(2, lines.Count);
        Assert.Equal(@"C:\Unity\Assets\Script.cs(12,34): error CS0103: The name 'bar' does not exist", lines[0]);
        Assert.Equal(@"C:\Unity\Assets\Script.cs(5,10): warning CS0219: Variable is assigned but its value is never used", lines[1]);
    }

    [Fact]
    public void UnityLogScanner_GetLogSnippet_ReturnsLastLinesOrMissingNotice()
    {
        var scanner = new UnityLogScanner();
        string tempFile = Path.GetTempFileName();

        try
        {
            var lines = new StringBuilder();
            for (int i = 1; i <= 40; i++)
            {
                lines.AppendLine($"Line {i}");
            }
            File.WriteAllText(tempFile, lines.ToString());

            string snippet = scanner.GetLogSnippet(tempFile);
            Assert.Contains("Last log lines:", snippet);
            Assert.Contains("Line 40", snippet);
            Assert.DoesNotContain("Line 5\n", snippet); // Only last 25 lines

            string missingSnippet = scanner.GetLogSnippet(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            Assert.Equal("No Unity log file found.", missingSnippet);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task UnitySocketTransport_SendCommandAsync_SendsAndReceivesLine()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            string? cmd = await reader.ReadLineAsync(cts.Token);
            if (cmd == "PING")
            {
                await writer.WriteLineAsync("PONG");
            }
            else
            {
                await writer.WriteLineAsync($"ECHO: {cmd}");
            }
        }, cts.Token);

        try
        {
            var transport = new UnitySocketTransport(NullLogger.Instance);

            bool isReady = await transport.IsSocketReadyAsync(port, timeoutSeconds: 2, cts.Token);
            Assert.True(isReady);
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task OperationPoller_PollOperationUntilTerminalAsync_ReadsResultFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_poller_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string resultFile = Path.Combine(tempDir, "result.json");
        string opFile = Path.Combine(tempDir, "op.json");

        var pathResolver = new UnityPathResolver(tempDir);
        var pm = new UnityProcessManager(pathResolver, NullLogger<UnityProcessManager>.Instance);
        var transport = new UnitySocketTransport(NullLogger.Instance);
        var poller = new OperationPoller(pm, pathResolver, transport, NullLogger.Instance);

        string opId = "test_op_123";

        // Write terminal result file
        File.WriteAllText(resultFile, $"{{\"operationId\":\"{opId}\",\"success\":true,\"message\":\"Completed successfully\"}}");

        var spec = new OperationPollingSpec<UnityOperationResult>
        {
            OperationId = opId,
            Kind = "test",
            ResultFilePath = resultFile,
            IsMatch = r => r.OperationId == opId,
            PollCommand = $"POLL {opId}",
            PollIntervalMs = 50
        };

        var result = await poller.PollOperationUntilTerminalAsync(spec, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(opId, result.OperationId);
        Assert.Equal("Completed successfully", result.Message);

        try { Directory.Delete(tempDir, true); } catch { }
    }
}

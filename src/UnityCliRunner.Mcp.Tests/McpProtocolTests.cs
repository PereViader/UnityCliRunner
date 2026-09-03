using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

public class McpProtocolTests
{
    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "UnityCliRunner.Unity3d")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root");
    }

    private static string GetUnityProjectRoot()
    {
        return Path.Combine(GetRepoRoot(), "src", "UnityCliRunner.Unity3d");
    }

    private static string GetMcpServerDllPath()
    {
        string root = GetRepoRoot();
        string unityRoot = GetUnityProjectRoot();
        string dllPath = Path.Combine(unityRoot, "Packages", "com.pereviader.unityclirunner", "MCP~", "UnityCliRunner.Mcp.dll");
        if (File.Exists(dllPath)) return dllPath;

        string debugDll = Path.Combine(root, "src", "UnityCliRunner.Mcp", "bin", "Debug", "net8.0", "UnityCliRunner.Mcp.dll");
        if (File.Exists(debugDll)) return debugDll;

        throw new FileNotFoundException($"Could not find UnityCliRunner.Mcp.dll at {dllPath} or {debugDll}");
    }

    [Fact]
    public async Task StdioHandshake_And_ToolsList_ReturnsAllExpectedTools()
    {
        string dllPath = GetMcpServerDllPath();
        string projectRoot = GetUnityProjectRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --project \"{projectRoot}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        // Constantly drain stderr to avoid process deadlock on full pipe buffer
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();

        var writer = proc.StandardInput;
        var reader = proc.StandardOutput;

        // 1. Initialize
        string initMsg = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"xunit-test\",\"version\":\"1.0\"}}}";
        await writer.WriteLineAsync(initMsg);
        await writer.FlushAsync();

        string? initResponse = await reader.ReadLineAsync();
        Assert.NotNull(initResponse);
        using var initDoc = JsonDocument.Parse(initResponse);
        Assert.True(initDoc.RootElement.TryGetProperty("result", out var initResult));
        Assert.True(initResult.TryGetProperty("serverInfo", out var serverInfo));
        Assert.Equal("UnityCliRunner.Mcp", serverInfo.GetProperty("name").GetString());

        // 2. Initialized notification
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await writer.FlushAsync();

        // 3. tools/list
        string listMsg = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}";
        await writer.WriteLineAsync(listMsg);
        await writer.FlushAsync();

        string? listResponse = await reader.ReadLineAsync();
        Assert.NotNull(listResponse);
        using var listDoc = JsonDocument.Parse(listResponse);
        Assert.True(listDoc.RootElement.TryGetProperty("result", out var listResult));
        Assert.True(listResult.TryGetProperty("tools", out var toolsElem));

        var toolNames = new HashSet<string>();
        foreach (var tool in toolsElem.EnumerateArray())
        {
            toolNames.Add(tool.GetProperty("name").GetString()!);
        }

        Assert.Contains("unity_status", toolNames);
        Assert.Contains("unity_refresh", toolNames);
        Assert.Contains("unity_recompile", toolNames);
        Assert.Contains("unity_eval", toolNames);
        Assert.Contains("unity_execute_method", toolNames);
        Assert.Contains("unity_run_tests", toolNames);
        Assert.Contains("unity_stop", toolNames);
        Assert.Contains("unity_start", toolNames);

        // 4. tools/call unity_status
        string callMsg = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"unity_status\",\"arguments\":{}}}";
        await writer.WriteLineAsync(callMsg);
        await writer.FlushAsync();

        string? callResponse = await reader.ReadLineAsync();
        Assert.NotNull(callResponse);
        using var callDoc = JsonDocument.Parse(callResponse);
        Assert.True(callDoc.RootElement.TryGetProperty("result", out var callResult));
        Assert.False(callResult.TryGetProperty("isError", out var isErr) && isErr.GetBoolean());
        Assert.True(callResult.TryGetProperty("content", out var content));
        string statusText = content[0].GetProperty("text").GetString()!;
        Assert.StartsWith("Status:", statusText);

        writer.Close();
        if (!proc.WaitForExit(3000))
        {
            proc.Kill(true);
        }
    }

    [Fact]
    public async Task StdioCall_UnknownTool_ReturnsJsonRpcError()
    {
        string dllPath = GetMcpServerDllPath();
        string projectRoot = GetUnityProjectRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --project \"{projectRoot}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();

        var writer = proc.StandardInput;
        var reader = proc.StandardOutput;

        // Initialize
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"xunit-test\",\"version\":\"1.0\"}}}");
        await writer.FlushAsync();
        await reader.ReadLineAsync();

        // Initialized notification
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await writer.FlushAsync();

        // Call non-existent tool
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"non_existent_tool\",\"arguments\":{}}}");
        await writer.FlushAsync();

        string? callResponse = await reader.ReadLineAsync();
        Assert.NotNull(callResponse);
        using var callDoc = JsonDocument.Parse(callResponse);
        Assert.True(callDoc.RootElement.TryGetProperty("error", out _));

        writer.Close();
        if (!proc.WaitForExit(3000))
        {
            proc.Kill(true);
        }
    }
}

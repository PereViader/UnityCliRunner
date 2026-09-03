using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

public class CliValidationTests
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

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string arguments)
    {
        string dllPath = GetMcpServerDllPath();
        string projectRoot = GetUnityProjectRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --project \"{projectRoot}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();

        return (proc.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    [Fact]
    public async Task InvalidSubcommand_FailsWithExit1()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync("test-cli invalid_subcommand");
        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown command: invalid_subcommand", stderr + stdout);
    }

    [Fact]
    public async Task Refresh_WithExtraArgs_FailsWithExit1()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync("test-cli refresh unexpected_arg");
        Assert.Equal(1, exitCode);
        Assert.Contains("Error: refresh does not accept extra arguments", stderr + stdout);
    }

    [Fact]
    public async Task Test_WithMissingFilter_FailsWithExit1()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync("test-cli test --filter");
        Assert.Equal(1, exitCode);
        Assert.Contains("Error: --filter requires an argument", stderr + stdout);
    }

    [Fact]
    public async Task ExecuteMethod_WithMissingMethod_FailsWithExit1()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync("test-cli executemethod");
        Assert.Equal(1, exitCode);
        Assert.Contains("Error: executemethod requires a method name", stderr + stdout);
    }

    [Fact]
    public async Task Eval_WithMissingCode_FailsWithExit1()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync("test-cli eval");
        Assert.Equal(1, exitCode);
        Assert.Contains("Error: eval requires a C# code snippet", stderr + stdout);
    }

    [Fact]
    public async Task Status_WhenOffline_ReturnsNotRunningWithExit0()
    {
        var (exitCode, stdout, _) = await RunCliAsync("test-cli status");
        Assert.Equal(0, exitCode);
        Assert.Contains("Status:", stdout);
    }
}

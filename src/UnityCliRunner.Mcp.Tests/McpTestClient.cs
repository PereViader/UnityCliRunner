using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCliRunner.Mcp.Tests;

public record McpToolResult(bool IsError, string Text, JsonElement RawResult);

public class McpTestClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private int _nextId = 1;
    private bool _initialized;

    public McpTestClient(string? projectRoot = null)
    {
        string root = GetRepoRoot();
        string unityRoot = projectRoot ?? Path.Combine(root, "src", "UnityCliRunner.Unity3d");
        string dllPath = GetMcpServerDllPath();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --project \"{unityRoot}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start UnityCliRunner.Mcp process.");
        
        // Drain stderr to avoid pipe deadlock
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();

        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;
    }

    public static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "UnityCliRunner.Unity3d")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root");
    }

    public static string GetUnityProjectRoot()
    {
        return Path.Combine(GetRepoRoot(), "src", "UnityCliRunner.Unity3d");
    }

    public static string GetMcpServerDllPath()
    {
        string root = GetRepoRoot();
        string unityRoot = GetUnityProjectRoot();
        string publishedDll = Path.Combine(unityRoot, "Packages", "com.pereviader.unityclirunner", "MCP~", "UnityCliRunner.Mcp.dll");
        if (File.Exists(publishedDll)) return publishedDll;

        string debugDll = Path.Combine(root, "src", "UnityCliRunner.Mcp", "bin", "Debug", "net8.0", "UnityCliRunner.Mcp.dll");
        if (File.Exists(debugDll)) return debugDll;

        throw new FileNotFoundException($"Could not find UnityCliRunner.Mcp.dll at {publishedDll} or {debugDll}");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        int id = Interlocked.Increment(ref _nextId);
        string initMsg = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id,
            method = "initialize",
            paramsObj = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "xunit-test-client", version = "1.0.0" }
            }
        }).Replace("paramsObj", "params");

        await _writer.WriteLineAsync(initMsg.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);

        string? response = await _reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrEmpty(response))
        {
            throw new InvalidOperationException("MCP server closed connection during initialization.");
        }

        // Send notifications/initialized
        string initializedNotif = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });

        await _writer.WriteLineAsync(initializedNotif.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
        _initialized = true;
    }

    public async Task<McpToolResult> CallToolAsync(string toolName, object? arguments = null, TimeSpan? timeout = null)
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }

        int id = Interlocked.Increment(ref _nextId);
        string callMsg = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id,
            method = "tools/call",
            paramsObj = new
            {
                name = toolName,
                arguments = arguments ?? new { }
            }
        }).Replace("paramsObj", "params");

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(180));

        await _writer.WriteLineAsync(callMsg.AsMemory(), cts.Token);
        await _writer.FlushAsync(cts.Token);

        string? responseLine = await _reader.ReadLineAsync(cts.Token);
        if (string.IsNullOrEmpty(responseLine))
        {
            throw new InvalidOperationException($"MCP server closed connection without response for tool '{toolName}'.");
        }

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement.Clone();

        if (root.TryGetProperty("error", out var errorElem))
        {
            return new McpToolResult(true, $"JSON-RPC Error: {errorElem.GetRawText()}", root);
        }

        if (root.TryGetProperty("result", out var resultElem))
        {
            bool isError = resultElem.TryGetProperty("isError", out var isErrProp) && isErrProp.GetBoolean();
            string text = "";
            if (resultElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentElem.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textProp))
                    {
                        text += textProp.GetString();
                    }
                }
            }

            return new McpToolResult(isError, text, resultElem);
        }

        return new McpToolResult(true, $"Unexpected response: {responseLine}", root);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _writer.Close();
        }
        catch { }

        try
        {
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(true);
            }
        }
        catch { }

        _process.Dispose();
        await Task.CompletedTask;
    }
}

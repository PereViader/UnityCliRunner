using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UnityCliRunner.Mcp;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("UnityCliRunner MCP Server");
    Console.WriteLine("Exposes Unity Editor control and testing tools via Model Context Protocol (MCP).");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  UnityCliRunner.Mcp [options]");
    Console.WriteLine("  UnityCliRunner.Mcp call <toolName> [jsonArgs]");
    Console.WriteLine("  UnityCliRunner.Mcp test-rpc <toolName> [jsonArgs]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -p, --project <path>    Path to Unity project root directory.");
    Console.WriteLine("  -h, --help              Show command line help.");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  UNITY_CLI_PROJECT_ROOT  Path to Unity project root directory.");
    Console.WriteLine("  UNITY_PATH              Path to Unity Editor executable.");
    Console.WriteLine("  UNITY_EDITOR            Path to Unity Editor executable.");
    return 0;
}

string? projectPath = null;
var remainingArgs = new System.Collections.Generic.List<string>();
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--project" || args[i] == "-p") && i + 1 < args.Length)
    {
        projectPath = args[++i];
    }
    else
    {
        remainingArgs.Add(args[i]);
    }
}

if (string.IsNullOrWhiteSpace(projectPath))
{
    projectPath = Environment.GetEnvironmentVariable("UNITY_CLI_PROJECT_ROOT");
}

if (string.IsNullOrWhiteSpace(projectPath))
{
    projectPath = ResolveProjectRoot();
}

string resolvedProjectRoot = Path.GetFullPath(projectPath);

if (remainingArgs.Count > 0 && remainingArgs[0] == "test-rpc")
{
    return await RunTestRpcClientAsync(remainingArgs.Skip(1).ToArray(), resolvedProjectRoot);
}

if (remainingArgs.Count > 0 && remainingArgs[0] == "test-cli")
{
    return await RunTestCliAsync(remainingArgs.Skip(1).ToArray(), resolvedProjectRoot);
}

if (remainingArgs.Count > 0 && remainingArgs[0] == "call")
{
    return await RunDirectCallAsync(remainingArgs.Skip(1).ToArray(), resolvedProjectRoot);
}

var builder = Host.CreateApplicationBuilder(args);

// Direct all console logging to stderr to prevent log pollution on stdout (used by MCP stdio transport)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<UnityProcessManager>>();
    return new UnityProcessManager(resolvedProjectRoot, logger);
});

builder.Services.AddSingleton<UnityClient>();
builder.Services.AddSingleton<UnityTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<UnityTools>();

var app = builder.Build();
await app.RunAsync();
return 0;

static string ResolveProjectRoot()
{
    string current = Directory.GetCurrentDirectory();
    var dir = new DirectoryInfo(current);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) &&
            Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return current;
}

static async Task<int> RunDirectCallAsync(string[] callArgs, string projectRoot)
{
    if (callArgs.Length == 0)
    {
        Console.Error.WriteLine("Error: call requires a tool name (e.g. unity_status, unity_eval, unity_refresh).");
        return 1;
    }

    string toolName = callArgs[0];
    string? jsonArgs = callArgs.Length > 1 ? string.Join(" ", callArgs.Skip(1)) : null;

    var services = new ServiceCollection();
    services.AddLogging(b =>
    {
        b.ClearProviders();
        b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    });
    services.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<UnityProcessManager>>();
        return new UnityProcessManager(projectRoot, logger);
    });
    services.AddSingleton<UnityClient>();
    services.AddSingleton<UnityTools>();

    using var provider = services.BuildServiceProvider();
    var tools = provider.GetRequiredService<UnityTools>();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        try { cts.Cancel(); } catch { }
    };

    CallToolResult result;
    try
    {
        using var doc = !string.IsNullOrWhiteSpace(jsonArgs) ? JsonDocument.Parse(jsonArgs) : null;
        var root = doc?.RootElement;

        switch (toolName)
        {
            case "unity_status":
                result = await tools.UnityStatusAsync(cts.Token);
                break;
            case "unity_refresh":
                result = await tools.UnityRefreshAsync(cts.Token);
                break;
            case "unity_recompile":
                result = await tools.UnityRecompileAsync(cts.Token);
                break;
            case "unity_eval":
                string code = root.HasValue && root.Value.TryGetProperty("code", out var codeElem)
                    ? codeElem.GetString() ?? ""
                    : jsonArgs ?? "";
                result = await tools.UnityEvalAsync(code, cts.Token);
                break;
            case "unity_execute_method":
                string methodName = root.HasValue && root.Value.TryGetProperty("methodName", out var mElem)
                    ? mElem.GetString() ?? ""
                    : "";
                string[]? methodArgs = null;
                if (root.HasValue && root.Value.TryGetProperty("args", out var argsElem) && argsElem.ValueKind == JsonValueKind.Array)
                {
                    methodArgs = argsElem.EnumerateArray().Select(a => a.ToString()).ToArray();
                }
                result = await tools.UnityExecuteMethodAsync(methodName, methodArgs, cts.Token);
                break;
            case "unity_run_tests":
                string? filter = root.HasValue && root.Value.TryGetProperty("filter", out var fElem) ? fElem.GetString() : null;
                string? category = root.HasValue && root.Value.TryGetProperty("category", out var catElem) ? catElem.GetString() : null;
                string? mode = root.HasValue && root.Value.TryGetProperty("mode", out var modElem) ? modElem.GetString() : "editmode";
                result = await tools.UnityRunTestsAsync(filter, category, mode, cts.Token);
                break;
            case "unity_stop":
                result = await tools.UnityStopAsync(cts.Token);
                break;
            case "unity_start":
                result = await tools.UnityStartAsync(cancellationToken: cts.Token);
                break;
            default:
                Console.Error.WriteLine($"Error: Unknown tool name '{toolName}'.");
                return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error executing tool {toolName}: {ex.Message}");
        return 1;
    }

    string text = "";
    if (result.Content != null && result.Content.Count > 0)
    {
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock textBlock)
            {
                text += textBlock.Text;
            }
        }
    }

    if (!string.IsNullOrEmpty(text))
    {
        Console.WriteLine(text);
    }

    return (result.IsError == true) ? 1 : 0;
}

static async Task<int> RunTestRpcClientAsync(string[] clientArgs, string projectRoot)
{
    if (clientArgs.Length == 0)
    {
        Console.Error.WriteLine("Error: test-rpc requires a tool name (e.g. unity_status, unity_eval, unity_refresh).");
        return 1;
    }

    string toolName = clientArgs[0];
    string? jsonArgs = clientArgs.Length > 1 ? string.Join(" ", clientArgs.Skip(1)) : "{}";
    if (string.IsNullOrWhiteSpace(jsonArgs))
    {
        jsonArgs = "{}";
    }

    string dllPath = typeof(Program).Assembly.Location;

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
    if (proc == null)
    {
        Console.Error.WriteLine("Error: Failed to spawn MCP server process.");
        return 1;
    }

    // Forward stderr to console error if DEBUG is enabled
    proc.ErrorDataReceived += (_, e) =>
    {
        if (e.Data != null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEBUG")))
        {
            Console.Error.WriteLine(e.Data);
        }
    };
    proc.BeginErrorReadLine();

    var writer = proc.StandardInput;
    var reader = proc.StandardOutput;

    // 1. Send initialize
    string initMsg = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        paramsObj = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "mcp-test-client", version = "1.0.0" }
        }
    }).Replace("paramsObj", "params");

    await writer.WriteLineAsync(initMsg);
    await writer.FlushAsync();

    string? initResponse = await reader.ReadLineAsync();
    if (string.IsNullOrEmpty(initResponse))
    {
        Console.Error.WriteLine("Error: MCP server closed connection before initialization response.");
        return 1;
    }

    // 2. Send initialized notification
    string initializedNotif = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        method = "notifications/initialized"
    });
    await writer.WriteLineAsync(initializedNotif);
    await writer.FlushAsync();

    // 3. Send tools/call
    object parsedArgs;
    try
    {
        parsedArgs = JsonSerializer.Deserialize<JsonElement>(jsonArgs);
    }
    catch
    {
        parsedArgs = new { };
    }

    string callMsg = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = 2,
        method = "tools/call",
        paramsObj = new
        {
            name = toolName,
            arguments = parsedArgs
        }
    }).Replace("paramsObj", "params");

    await writer.WriteLineAsync(callMsg);
    await writer.FlushAsync();

    string? callResponse = await reader.ReadLineAsync();
    if (string.IsNullOrEmpty(callResponse))
    {
        Console.Error.WriteLine("Error: MCP server closed connection without tool response.");
        return 1;
    }

    // Close stdin so server can exit
    writer.Close();
    if (!proc.WaitForExit(3000))
    {
        proc.Kill(true);
    }

    // Parse tool response
    using var doc = JsonDocument.Parse(callResponse);
    var root = doc.RootElement;

    if (root.TryGetProperty("error", out var errElem))
    {
        Console.Error.WriteLine($"JSON-RPC Error: {errElem.GetRawText()}");
        return 1;
    }

    if (!root.TryGetProperty("result", out var resElem))
    {
        Console.Error.WriteLine($"Unexpected response: {callResponse}");
        return 1;
    }

    bool isError = resElem.TryGetProperty("isError", out var isErrProp) && isErrProp.GetBoolean();

    string text = "";
    if (resElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in contentElem.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textProp))
            {
                text += textProp.GetString();
            }
        }
    }

    if (!string.IsNullOrEmpty(text))
    {
        Console.WriteLine(text);
    }

    return isError ? 1 : 0;
}

static async Task<int> RunTestCliAsync(string[] cliArgs, string projectRoot)
{
    if (cliArgs.Length == 0)
    {
        Console.Error.WriteLine("Error: Missing command");
        return 1;
    }

    string subCommand = cliArgs[0].ToLowerInvariant();
    string toolName;
    object argsObj;

    switch (subCommand)
    {
        case "status":
            if (cliArgs.Length > 1)
            {
                Console.Error.WriteLine("Error: status command does not accept extra arguments");
                return 1;
            }
            toolName = "unity_status";
            argsObj = new { };
            break;

        case "start":
            if (cliArgs.Length < 2)
            {
                Console.Error.WriteLine("Error: start command requires a mode (batchmode|interactive)");
                return 1;
            }
            if (cliArgs.Length > 2)
            {
                Console.Error.WriteLine("Error: start command does not accept extra arguments");
                return 1;
            }
            toolName = "unity_start";
            argsObj = new { mode = cliArgs[1] };
            break;

        case "stop":
            if (cliArgs.Length > 1)
            {
                Console.Error.WriteLine("Error: stop command does not accept extra arguments");
                return 1;
            }
            toolName = "unity_stop";
            argsObj = new { };
            break;

        case "refresh":
            if (cliArgs.Length > 1)
            {
                Console.Error.WriteLine("Error: refresh does not accept extra arguments");
                return 1;
            }
            toolName = "unity_refresh";
            argsObj = new { };
            break;

        case "recompile":
            if (cliArgs.Length > 1)
            {
                Console.Error.WriteLine("Error: recompile command does not accept extra arguments");
                return 1;
            }
            toolName = "unity_recompile";
            argsObj = new { };
            break;

        case "eval":
            if (cliArgs.Length < 2)
            {
                Console.Error.WriteLine("Error: eval requires a C# code snippet or expression (e.g., Application.unityVersion)");
                return 1;
            }
            toolName = "unity_eval";
            string code = string.Join(" ", cliArgs.Skip(1));
            argsObj = new { code };
            break;

        case "executemethod":
            if (cliArgs.Length < 2)
            {
                Console.Error.WriteLine("Error: executemethod requires a method name argument (e.g., Namespace.Class.Method)");
                return 1;
            }
            toolName = "unity_execute_method";
            string methodName = cliArgs[1];
            string[] methodArgs = cliArgs.Skip(2).ToArray();
            argsObj = new { methodName, args = methodArgs };
            break;

        case "test":
            string? filter = null;
            string? category = null;
            string mode = "editmode";
            bool hasEdit = false;
            bool hasPlay = false;

            for (int i = 1; i < cliArgs.Length; i++)
            {
                string arg = cliArgs[i];
                if (arg == "--editmode") { hasEdit = true; }
                else if (arg == "--playmode") { hasPlay = true; }
                else if (arg == "--filter")
                {
                    if (i + 1 >= cliArgs.Length)
                    {
                        Console.Error.WriteLine("Error: --filter requires an argument");
                        return 1;
                    }
                    filter = cliArgs[++i];
                }
                else if (arg.StartsWith("--filter="))
                {
                    filter = arg.Substring("--filter=".Length);
                }
                else if (arg == "--category")
                {
                    if (i + 1 >= cliArgs.Length)
                    {
                        Console.Error.WriteLine("Error: --category requires an argument");
                        return 1;
                    }
                    category = cliArgs[++i];
                }
                else if (arg.StartsWith("--category="))
                {
                    category = arg.Substring("--category=".Length);
                }
                else
                {
                    Console.Error.WriteLine($"Unknown option for test subcommand: {arg}");
                    return 1;
                }
            }

            if (hasEdit && hasPlay) mode = "all";
            else if (hasPlay) mode = "playmode";
            else mode = "editmode";

            toolName = "unity_run_tests";
            argsObj = new { filter, category, mode };
            break;

        default:
            Console.Error.WriteLine($"Unknown command: {subCommand}");
            return 1;
    }

    string jsonArgs = JsonSerializer.Serialize(argsObj);
    return await RunTestRpcClientAsync(new[] { toolName, jsonArgs }, projectRoot);
}


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UnityCliRunner.Mcp;

public class UnityProcessManager
{
    private readonly ILogger<UnityProcessManager> _logger;
    private static readonly Regex s_CompileErrorRegex = new(
        @"^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): error [a-zA-Z0-9]+:",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_CompileDiagRegex = new(
        @"^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): (error|warning) [a-zA-Z0-9]+:.*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public string ProjectRoot { get; }
    public string TempDir => Path.Combine(ProjectRoot, "Temp");
    public string PidFile => Path.Combine(TempDir, "unity_cli_process.pid");
    public string PortFile => Path.Combine(TempDir, "unity_cli_port.txt");
    public string LogFile => Path.Combine(ProjectRoot, "unity_background_log.txt");
    public string CompilationErrorsFile => Path.Combine(TempDir, "unity_compilation_errors.txt");
    public string OperationFile => Path.Combine(TempDir, "unity_cli_operation.json");
    public string RefreshResultFile => Path.Combine(TempDir, "unity_refresh_result.json");
    public string EvalResultFile => Path.Combine(TempDir, "unity_eval_result.json");
    public string ExecuteResultFile => Path.Combine(TempDir, "unity_execute_result.json");
    public string TestResultsFile => Path.Combine(TempDir, "unity_test_results.json");
    public string TestFailuresFile => Path.Combine(TempDir, "unity_test_failures.txt");

    public UnityProcessManager(string projectRoot, ILogger<UnityProcessManager> logger)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        _logger = logger;
    }

    /// <summary>
    /// Detects Unity process liveness (checking Temp/unity_cli_process.pid, Temp/UnityLockfile, and system processes)
    /// on Windows, macOS, and Linux.
    /// </summary>
    public bool IsUnityRunning(out int? processId)
    {
        processId = null;

        // 1. Check Temp/unity_cli_process.pid
        if (File.Exists(PidFile))
        {
            try
            {
                string pidText = ReadFileWithRetry(PidFile).Trim();
                if (int.TryParse(pidText, out int pid) && pid > 0)
                {
                    if (IsProcessAlive(pid))
                    {
                        processId = pid;
                        return true;
                    }
                }
                // PID is dead, delete stale file
                try { File.Delete(PidFile); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading pid file {PidFile}", PidFile);
            }
        }

        // 2. Check Temp/UnityLockfile or Temp/UnityLockFile
        string lockFilePath = Path.Combine(TempDir, "UnityLockfile");
        if (!File.Exists(lockFilePath))
        {
            lockFilePath = Path.Combine(TempDir, "UnityLockFile");
        }

        if (File.Exists(lockFilePath))
        {
            // Try reading PID from lockfile (4-byte binary or text)
            try
            {
                var fileInfo = new FileInfo(lockFilePath);
                int lockPid = 0;
                if (fileInfo.Length == 4)
                {
                    byte[] bytes = File.ReadAllBytes(lockFilePath);
                    lockPid = BitConverter.ToInt32(bytes, 0);
                }
                else
                {
                    string text = ReadFileWithRetry(lockFilePath).Trim();
                    int.TryParse(text, out lockPid);
                }

                if (lockPid > 0 && IsProcessAlive(lockPid))
                {
                    processId = lockPid;
                    return true;
                }
            }
            catch { }

            // Check if file is actively locked by an operating system handle
            bool isLocked = IsFileLocked(lockFilePath);
            if (isLocked)
            {
                // File is held open by an active process
                processId = FindProjectUnityPid();
                return true;
            }

            // Lockfile exists but is not locked and has no live process
            try { File.Delete(lockFilePath); } catch { }
        }

        // 3. Fallback: check system processes for Unity instance targeting this project
        int? sysPid = FindProjectUnityPid();
        if (sysPid.HasValue)
        {
            processId = sysPid;
            return true;
        }

        return false;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileLocked(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int? FindProjectUnityPid()
    {
        try
        {
            var processes = Process.GetProcessesByName("Unity");
            if (processes.Length == 0 && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                processes = Process.GetProcessesByName("unity-editor");
            }

            if (processes.Length == 1)
            {
                return processes[0].Id;
            }

            // If multiple Unity processes exist, check command line where possible
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var proc in processes)
                {
                    try
                    {
                        // On Windows, if port file exists and responds, match it
                        if (File.Exists(PortFile))
                        {
                            return proc.Id;
                        }
                    }
                    catch { }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string normalizedProject = ProjectRoot.TrimEnd('/', '\\');
                foreach (var proc in processes)
                {
                    try
                    {
                        string cmdlinePath = $"/proc/{proc.Id}/cmdline";
                        if (File.Exists(cmdlinePath))
                        {
                            string cmdline = File.ReadAllText(cmdlinePath);
                            if (cmdline.Contains(normalizedProject, StringComparison.OrdinalIgnoreCase))
                            {
                                return proc.Id;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (processes.Length > 0)
            {
                return processes[0].Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query system Unity processes.");
        }

        return null;
    }

    /// <summary>
    /// Locates the Unity executable for this project:
    /// - Checks UNITY_PATH and UNITY_EDITOR environment variables.
    /// - Reads ProjectSettings/ProjectVersion.txt (extracts m_EditorVersion:).
    /// - Checks standard Unity Hub paths on Windows, macOS, and Linux.
    /// - Checks PATH (unity-editor, Unity, Unity.exe, unity).
    /// </summary>
    public string? FindUnityExecutable()
    {
        // 1. Environment variables
        string? configuredPath = Environment.GetEnvironmentVariable("UNITY_PATH")
            ?? Environment.GetEnvironmentVariable("UNITY_EDITOR");

        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        // 2. Read editor version from ProjectSettings/ProjectVersion.txt
        string? editorVersion = GetProjectEditorVersion();

        // 3. Check standard Unity Hub paths
        if (!string.IsNullOrWhiteSpace(editorVersion))
        {
            var hubPaths = GetStandardHubCandidatePaths(editorVersion);
            foreach (var candidate in hubPaths)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        // 4. Search in PATH
        string[] binaryNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "Unity.exe", "unity-editor.exe", "unity.exe" }
            : new[] { "unity-editor", "Unity", "unity" };

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            char sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            var directories = pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in directories)
            {
                foreach (var binary in binaryNames)
                {
                    string candidate = Path.Combine(dir, binary);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        return null;
    }

    private string? GetProjectEditorVersion()
    {
        string versionFilePath = Path.Combine(ProjectRoot, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFilePath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadAllLines(versionFilePath))
            {
                if (line.StartsWith("m_EditorVersion:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        return parts[1].Trim();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read editor version from {Path}", versionFilePath);
        }

        return null;
    }

    private static List<string> GetStandardHubCandidatePaths(string version)
    {
        var paths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            string? programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            if (!string.IsNullOrWhiteSpace(programFiles))
                paths.Add(Path.Combine(programFiles, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));
            if (!string.IsNullOrWhiteSpace(programW6432))
                paths.Add(Path.Combine(programW6432, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));
            if (!string.IsNullOrWhiteSpace(localAppData))
                paths.Add(Path.Combine(localAppData, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));

            paths.Add($@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Unity.exe");
            paths.Add($@"C:\Program Files (x86)\Unity\Hub\Editor\{version}\Editor\Unity.exe");
            paths.Add($@"C:\Unity\Hub\Editor\{version}\Editor\Unity.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            paths.Add($"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity");
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                paths.Add(Path.Combine(home, "Unity", "Hub", "Editor", version, "Unity.app", "Contents", "MacOS", "Unity"));
            }
        }
        else // Linux
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                paths.Add(Path.Combine(home, "Unity", "Hub", "Editor", version, "Editor", "Unity"));
            }
            paths.Add($"/opt/unity/Editor/{version}/Editor/Unity");
            paths.Add($"/opt/Unity/Editor/{version}/Editor/Unity");
            paths.Add("/opt/unity/Editor/Unity");
            paths.Add("/opt/Unity/Editor/Unity");
        }

        return paths;
    }

    /// <summary>
    /// Auto-starts Unity in headless batchmode if not already running, and waits up to 90 seconds for socket readiness.
    /// </summary>
    public async Task EnsureUnityRunningAsync(CancellationToken cancellationToken = default)
    {
        if (IsUnityRunning(out int? existingPid))
        {
            if (await IsSocketReadyAsync(2, cancellationToken))
            {
                _logger.LogInformation("Unity is already running (PID {Pid}) and socket server is ready.", existingPid);
                return;
            }

            _logger.LogInformation("Unity is running (PID {Pid}) but socket is not ready yet. Waiting for readiness...", existingPid);
            await WaitForSocketReadinessAsync(null, 90, cancellationToken);
            return;
        }

        string? unityExe = FindUnityExecutable();
        if (string.IsNullOrWhiteSpace(unityExe))
        {
            string? version = GetProjectEditorVersion();
            throw new FileNotFoundException(
                $"Unity executable not found for project at '{ProjectRoot}' (version: {version ?? "unknown"}). " +
                "Set the UNITY_PATH or UNITY_EDITOR environment variable or install Unity via Unity Hub.");
        }

        _logger.LogInformation("Auto-starting Unity batchmode from '{UnityExe}'...", unityExe);

        Directory.CreateDirectory(TempDir);
        try { File.Delete(LogFile); } catch { }
        try { File.Delete(PidFile); } catch { }
        try { File.Delete(CompilationErrorsFile); } catch { }

        var psi = new ProcessStartInfo
        {
            FileName = unityExe,
            Arguments = $"-batchmode -nographics -projectPath \"{ProjectRoot}\" -logFile \"{LogFile}\"",
            WorkingDirectory = ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch Unity process.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch Unity process at '{unityExe}': {ex.Message}", ex);
        }

        try
        {
            File.WriteAllText(PidFile, proc.Id.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write PID to {PidFile}", PidFile);
        }

        _logger.LogInformation("Unity process started with PID {Pid}. Waiting up to 90s for socket server...", proc.Id);
        await WaitForSocketReadinessAsync(proc, 90, cancellationToken);
    }

    private async Task WaitForSocketReadinessAsync(Process? startedProcess, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Check if the started process exited unexpectedly
            if (startedProcess is { HasExited: true })
            {
                if (File.Exists(LogFile))
                {
                    string logText = ReadFileWithRetry(LogFile);
                    if (s_CompileErrorRegex.IsMatch(logText))
                    {
                        var errorLines = ExtractUniqueCompilationLines(logText);
                        try
                        {
                            Directory.CreateDirectory(TempDir);
                            File.WriteAllLines(CompilationErrorsFile, errorLines);
                        }
                        catch { }
                        throw new UnityCompilationException(
                            string.Join(Environment.NewLine, errorLines),
                            errorLines);
                    }
                }

                string logSnippet = GetLogSnippet();
                throw new InvalidOperationException(
                    $"Unity background process exited unexpectedly with exit code {startedProcess.ExitCode}.\n{logSnippet}");
            }

            // 2. Monitor unity_background_log.txt for compilation errors during startup
            if (File.Exists(LogFile))
            {
                string logText = ReadFileWithRetry(LogFile);
                if (s_CompileErrorRegex.IsMatch(logText))
                {
                    _logger.LogError("Compilation errors detected in Unity background log during startup.");
                    var errorLines = ExtractUniqueCompilationLines(logText);
                    try
                    {
                        Directory.CreateDirectory(TempDir);
                        File.WriteAllLines(CompilationErrorsFile, errorLines);
                    }
                    catch { }

                    if (startedProcess != null && !startedProcess.HasExited)
                    {
                        try { startedProcess.Kill(true); } catch { }
                    }

                    throw new UnityCompilationException(
                        string.Join(Environment.NewLine, errorLines),
                        errorLines);
                }
            }

            // 3. Check socket connection
            if (File.Exists(PortFile))
            {
                if (await IsSocketReadyAsync(2, cancellationToken))
                {
                    // Check if server is settled
                    string? refreshState = await ProbeSocketCommandAsync("POLL_REFRESH", 2, cancellationToken);
                    if (refreshState == "READY" || refreshState == "COMPILATION_ERROR")
                    {
                        _logger.LogInformation("Unity socket server is ready (state: {State}).", refreshState);
                        return;
                    }
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        if (startedProcess != null && !startedProcess.HasExited)
        {
            try { startedProcess.Kill(true); } catch { }
        }

        throw new TimeoutException($"Timed out waiting for Unity background instance to be ready ({timeoutSeconds}s).");
    }

    public async Task<bool> IsSocketReadyAsync(int timeoutSeconds = 2, CancellationToken cancellationToken = default)
    {
        string? response = await ProbeSocketCommandAsync("PING", timeoutSeconds, cancellationToken);
        return response == "PONG";
    }

    public async Task<string?> ProbeSocketCommandAsync(string command, int timeoutSeconds = 2, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PortFile))
        {
            return null;
        }

        int port = ReadPortFile();
        if (port <= 0 || port > 65535)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            client.ReceiveTimeout = timeoutSeconds * 1000;
            client.SendTimeout = timeoutSeconds * 1000;

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            await writer.WriteLineAsync(command.AsMemory(), cts.Token);
            string? line = await reader.ReadLineAsync(cts.Token);
            return line?.Trim();
        }
        catch
        {
            return null;
        }
    }

    public int ReadPortFile()
    {
        if (!File.Exists(PortFile)) return 0;
        try
        {
            string content = ReadFileWithRetry(PortFile).Trim();
            return int.TryParse(content, out int port) ? port : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string GetLogSnippet()
    {
        if (!File.Exists(LogFile)) return "No Unity log file found.";
        try
        {
            var lines = File.ReadAllLines(LogFile);
            int start = Math.Max(0, lines.Length - 25);
            return "Last log lines:\n" + string.Join(Environment.NewLine, lines[start..]);
        }
        catch (Exception ex)
        {
            return $"Error reading log file: {ex.Message}";
        }
    }

    private static List<string> ExtractUniqueCompilationLines(string logText)
    {
        var matches = s_CompileDiagRegex.Matches(logText);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match m in matches)
        {
            string line = m.Value.Trim();
            if (!string.IsNullOrEmpty(line) && seen.Add(line))
            {
                result.Add(line);
            }
        }
        return result;
    }

    public static string ReadFileWithRetry(string path, int maxRetries = 5, int delayMs = 100)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
        using var finalFs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var finalReader = new StreamReader(finalFs, Encoding.UTF8);
        return finalReader.ReadToEnd();
    }

    public async Task<bool> StopUnityAsync(CancellationToken cancellationToken = default)
    {
        if (!IsUnityRunning(out int? pid))
        {
            return true;
        }

        // Try sending EXIT to socket first
        try
        {
            await ProbeSocketCommandAsync("EXIT", 2, cancellationToken);
        }
        catch { }

        // Wait up to 5 seconds for process to exit
        for (int i = 0; i < 25; i++)
        {
            if (!IsUnityRunning(out pid))
            {
                return true;
            }
            await Task.Delay(200, cancellationToken);
        }

        // Force kill if still running
        if (pid.HasValue && pid.Value > 0)
        {
            try
            {
                var proc = Process.GetProcessById(pid.Value);
                proc.Kill(true);
                proc.WaitForExit(2000);
            }
            catch { }
        }

        if (File.Exists(PidFile))
        {
            try { File.Delete(PidFile); } catch { }
        }

        return !IsUnityRunning(out _);
    }
}

using System;
using System.IO;

namespace UnityLeanMcp.Mcp;

public class UnityPathResolver : IUnityPathResolver
{
    public string ProjectRoot { get; }
    public string TempDir => Path.Combine(ProjectRoot, "Temp");
    public string OperationFile => Path.Combine(TempDir, "unity_lean_mcp_operation.json");
    public string CompilationErrorsFile => Path.Combine(TempDir, "unity_compilation_errors.txt");
    public string PortFile => Path.Combine(TempDir, "unity_lean_mcp_port.txt");
    public string LogFile => Path.Combine(ProjectRoot, "unity_background_log.txt");
    public string PidFile => Path.Combine(TempDir, "unity_lean_mcp_process.pid");
    public string RefreshResultFile => Path.Combine(TempDir, "unity_refresh_result.json");
    public string EvalResultFile => Path.Combine(TempDir, "unity_eval_result.json");
    public string ExecuteResultFile => Path.Combine(TempDir, "unity_execute_result.json");
    public string TestRunningFile => Path.Combine(TempDir, "unity_test_running.txt");
    public string TestResultsFile => Path.Combine(TempDir, "unity_test_results.json");

    public UnityPathResolver(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root cannot be null or empty.", nameof(projectRoot));
        }

        ProjectRoot = Path.GetFullPath(projectRoot);
    }
}

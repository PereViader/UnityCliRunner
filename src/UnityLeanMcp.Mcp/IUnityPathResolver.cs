namespace UnityLeanMcp.Mcp;

public interface IUnityPathResolver
{
    string ProjectRoot { get; }
    string TempDir { get; }
    string OperationFile { get; }
    string CompilationErrorsFile { get; }
    string PortFile { get; }
    string LogFile { get; }
    string PidFile { get; }
    string RefreshResultFile { get; }
    string EvalResultFile { get; }
    string ExecuteResultFile { get; }
    string TestRunningFile { get; }
    string TestResultsFile { get; }
    string GetEvalResultFile(string operationId);
    string GetExecuteResultFile(string operationId);
    string GetTestResultsFile(string operationId);
}

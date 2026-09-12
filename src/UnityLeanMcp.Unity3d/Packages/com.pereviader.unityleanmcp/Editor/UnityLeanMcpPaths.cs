using System.IO;

namespace UnityLeanMcp
{
    internal static class UnityLeanMcpPaths
    {
        private static string s_TempDir;
        private static string s_PortFile;
        private static string s_OperationFile;
        private static string s_DiagnosticsFile;
        private static string s_RefreshResultFile;
        private static string s_TestRunningFile;
        private static string s_TestResultsFile;
        private static string s_ExecuteResultFile;
        private static string s_EvalResultFile;

        public static string TempDir
        {
            get
            {
                if (string.IsNullOrEmpty(s_TempDir)) EnsureInitialized();
                return s_TempDir;
            }
        }
        public static string PortFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_PortFile)) EnsureInitialized();
                return s_PortFile;
            }
        }
        public static string OperationFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_OperationFile)) EnsureInitialized();
                return s_OperationFile;
            }
        }
        public static string DiagnosticsFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_DiagnosticsFile)) EnsureInitialized();
                return s_DiagnosticsFile;
            }
        }
        public static string RefreshResultFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_RefreshResultFile)) EnsureInitialized();
                return s_RefreshResultFile;
            }
        }
        public static string TestRunningFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_TestRunningFile)) EnsureInitialized();
                return s_TestRunningFile;
            }
        }
        public static string TestResultsFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_TestResultsFile)) EnsureInitialized();
                return s_TestResultsFile;
            }
        }
        public static string ExecuteResultFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_ExecuteResultFile)) EnsureInitialized();
                return s_ExecuteResultFile;
            }
        }
        public static string EvalResultFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_EvalResultFile)) EnsureInitialized();
                return s_EvalResultFile;
            }
        }

        public static string GetEvalResultFile(string operationId) =>
            string.IsNullOrEmpty(operationId) ? EvalResultFile : Path.Combine(TempDir, $"unity_eval_{operationId}.json");

        public static string GetExecuteResultFile(string operationId) =>
            string.IsNullOrEmpty(operationId) ? ExecuteResultFile : Path.Combine(TempDir, $"unity_execute_{operationId}.json");

        public static string GetTestResultsFile(string operationId) =>
            string.IsNullOrEmpty(operationId) ? TestResultsFile : Path.Combine(TempDir, $"unity_test_{operationId}.json");

        public static void EnsureInitialized()
        {
            CommandHelper.EnsureInitialized();
            string root = CommandHelper.ProjectRoot;
            s_TempDir = Path.Combine(root, "Temp");
            s_PortFile = Path.Combine(s_TempDir, "unity_lean_mcp_port.txt");
            s_OperationFile = Path.Combine(s_TempDir, "unity_lean_mcp_operation.json");
            s_DiagnosticsFile = Path.Combine(s_TempDir, "unity_compilation_errors.txt");
            s_RefreshResultFile = Path.Combine(s_TempDir, "unity_refresh_result.json");
            s_TestRunningFile = Path.Combine(s_TempDir, "unity_test_running.txt");
            s_TestResultsFile = Path.Combine(s_TempDir, "unity_test_results.json");
            s_ExecuteResultFile = Path.Combine(s_TempDir, "unity_execute_result.json");
            s_EvalResultFile = Path.Combine(s_TempDir, "unity_eval_result.json");
        }
    }
}

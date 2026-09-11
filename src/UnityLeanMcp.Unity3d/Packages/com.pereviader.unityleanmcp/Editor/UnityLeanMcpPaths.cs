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

        public static string TempDir => s_TempDir;
        public static string PortFile => s_PortFile;
        public static string OperationFile => s_OperationFile;
        public static string DiagnosticsFile => s_DiagnosticsFile;
        public static string RefreshResultFile => s_RefreshResultFile;
        public static string TestRunningFile => s_TestRunningFile;
        public static string TestResultsFile => s_TestResultsFile;
        public static string ExecuteResultFile => s_ExecuteResultFile;
        public static string EvalResultFile => s_EvalResultFile;

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

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RunTestsHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;

        private const string ActiveTestFilterSessionKey = "UnityCliRunner.HasActiveTestFilter";
        private const string CallbackOwnerName = "UnityCliRunner.CallbackOwner";
        private static MyTestCallbacks s_Callbacks;
        private static TestRunnerApi s_RunnerApi;
        private static MethodInfo s_IsRunActiveMethod;
        internal static string s_CurrentTestJobGuid;

        internal static bool HasActiveTestFilter
        {
            get => SessionState.GetBool(ActiveTestFilterSessionKey, false);
            set => SessionState.SetBool(ActiveTestFilterSessionKey, value);
        }

        internal static string TempDirectory => UnityCliPaths.TempDir;
        internal static string RunningFilePath => UnityCliPaths.TestRunningFile;
        internal static string ResultsFilePath => UnityCliPaths.TestResultsFile;
        internal static string FailuresFilePath => UnityCliPaths.TestFailuresFile;

        internal static void MarkTransportInterruption(string status)
        {
            var state = ReadRunningState();
            if (state == null)
            {
                return;
            }

            state.status = status;
            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), state.runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to mark test run interruption: {ex.Message}");
            }
        }

        public static void RegisterCallbacks()
        {
            if (CommandHelper.IsAssetImportWorkerProcess())
            {
                return;
            }

            // Remove only callback owners created by this package. Destroying
            // every TestRunnerApi object breaks other editor tooling.
            foreach (var api in Resources.FindObjectsOfTypeAll<TestRunnerApi>())
            {
                if (api != null && api.name == CallbackOwnerName)
                {
                    try { UnityEngine.Object.DestroyImmediate(api); } catch { }
                }
            }

            s_Callbacks = new MyTestCallbacks();
            s_RunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            s_RunnerApi.name = CallbackOwnerName;
            s_RunnerApi.hideFlags = HideFlags.HideAndDontSave;
            s_RunnerApi.RegisterCallbacks(s_Callbacks);
            var runningState = ReadRunningState();
            if (runningState != null)
            {
                s_Callbacks.BindRun(runningState.runId);
            }
        }

        public static bool IsTestRunActive()
        {
            try
            {
                s_IsRunActiveMethod ??= typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return s_IsRunActiveMethod != null && (bool)s_IsRunActiveMethod.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to check IsRunActive: {ex}");
                return false;
            }
        }

        public void Handle(string payload, StreamWriter writer)
        {
            if (UnityCliCompilationTracker.ScriptCompilationFailed)
            {
                writer.WriteLine("FAILURE Compilation failed");
                return;
            }

            if (string.IsNullOrEmpty(payload))
            {
                writer.WriteLine("ERROR: Missing arguments");
                return;
            }

            string[] args = CommandHelper.SplitArguments(payload);
            if (args.Length < 2)
            {
                writer.WriteLine("ERROR: Missing operation id or test mode (playmode/editmode)");
                return;
            }

            string operationId = args[0];
            TestMode mode = args[1].ToLowerInvariant() switch
            {
                "playmode" => TestMode.PlayMode,
                "editmode" => TestMode.EditMode,
                _ => (TestMode)(-1)
            };

            if ((int)mode == -1)
            {
                writer.WriteLine("ERROR: Invalid test mode. Must be playmode or editmode");
                return;
            }

            string filter = "";
            string category = "";

            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length)
                {
                    filter = args[++i];
                }
                else if (args[i] == "--category" && i + 1 < args.Length)
                {
                    category = args[++i];
                }
            }

            var begin = UnityCliOperationStore.TryBegin(operationId, OperationKinds.Test, OperationStatus.Queued, out var existing);
            if (begin == BeginOperationResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }
            if (begin == BeginOperationResult.Busy)
            {
                writer.WriteLine($"BUSY {existing.kind} {existing.operationId}");
                return;
            }

            if (begin == BeginOperationResult.AlreadyStarted)
            {
                writer.WriteLine("RUNNING");
                writer.Flush();
                return;
            }

            // Persist the complete run identity before acknowledging the command.
            // The CLI can therefore recover if this socket is closed by a reload
            // immediately after the command is dispatched.
            string runId = WriteTestRunningState(operationId, mode, filter, category);
            if (string.IsNullOrEmpty(runId))
            {
                writer.WriteLine("ERROR: Could not persist test run state.");
                writer.Flush();
                UnityCliOperationStore.Complete(operationId);
                return;
            }

            writer.WriteLine("RUNNING");
            writer.Flush();

            RunTests(mode, filter, category, runId);
        }

        private static string WriteTestRunningState(string runId, TestMode mode, string filter, string category)
        {
            try
            {
                if (!Directory.Exists(TempDirectory))
                {
                    Directory.CreateDirectory(TempDirectory);
                }

                var state = new UnityTestRunState
                {
                    runId = runId,
                    mode = mode.ToString(),
                    filter = filter ?? "",
                    category = category ?? "",
                    status = "Queued",
                    startedUtc = DateTime.UtcNow.ToString("o")
                };
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
                return runId;
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write test running state: {ex}");
                return null;
            }
        }

        private static void RunTests(TestMode mode, string filterText, string categoryText, string runId)
        {
            try
            {
                if (s_Callbacks == null)
                {
                    RegisterCallbacks();
                }

                var filter = new Filter
                {
                    testMode = mode,
                    groupNames = !string.IsNullOrEmpty(filterText) ? new[] { filterText } : null,
                    categoryNames = !string.IsNullOrEmpty(categoryText) ? new[] { categoryText } : null
                };

                HasActiveTestFilter = !string.IsNullOrEmpty(filterText) || !string.IsNullOrEmpty(categoryText);
                UpdateTestRunStatus(runId, "Executing");
                s_Callbacks.BindRun(runId);

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityCliRunner: Executing {mode} tests with filter '{filterText}' and category '{categoryText}'...");
                s_CurrentTestJobGuid = s_RunnerApi.Execute(settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to start tests: {ex}");
                HasActiveTestFilter = false;
                if (s_Callbacks != null)
                {
                    s_Callbacks.OnInfrastructureFailure("Failed to start test run: " + ex.Message);
                }
                else
                {
                    WriteInterruptedResult("Failed to start test run: " + ex.Message);
                }
            }
        }

        internal static UnityTestRunState ReadRunningState()
        {
            try
            {
                if (!File.Exists(RunningFilePath))
                {
                    return null;
                }

                return JsonUtility.FromJson<UnityTestRunState>(File.ReadAllText(RunningFilePath));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to read test run state. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
                return null;
            }
        }

        internal static void UpdateTestRunStatus(string runId, string status)
        {
            var state = ReadRunningState();
            if (state == null || state.runId != runId)
            {
                return;
            }

            state.status = status;
            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to update test run state: {ex.Message}");
            }
        }

        internal static void WriteInterruptedResult(string message)
        {
            var state = ReadRunningState();
            string runId = state?.runId ?? Guid.NewGuid().ToString("N");
            var result = new UnityTestRunResult
            {
                runId = runId,
                success = false,
                message = message,
                resultState = "Interrupted",
                failedTests = new List<FailedTestInfo>()
            };

            try
            {
                WriteAtomic(ResultsFilePath, JsonUtility.ToJson(result, true), runId);
                DeleteIfExists(RunningFilePath);
                UnityCliOperationStore.Complete(runId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to persist interrupted test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        public static void CancelActiveTestRun(string operationId, StreamWriter writer)
        {
            var operation = UnityCliOperationStore.Read();
            var runningState = ReadRunningState();

            // 1. If neither operation store nor running state is active
            if (operation == null && runningState == null)
            {
                if (!string.IsNullOrEmpty(operationId) && File.Exists(ResultsFilePath))
                {
                    try
                    {
                        var existing = JsonUtility.FromJson<UnityTestRunResult>(File.ReadAllText(ResultsFilePath));
                        if (existing != null && existing.runId == operationId)
                        {
                            writer.WriteLine("CANCELLED");
                            return;
                        }
                    }
                    catch { }
                }

                writer.WriteLine("IDLE");
                return;
            }

            // 2. If the operation belongs to another operation ID
            if (operation != null && !string.IsNullOrEmpty(operationId) && operation.operationId != operationId)
            {
                writer.WriteLine($"BUSY {operation.kind} {operation.operationId}");
                return;
            }

            if (runningState != null && !string.IsNullOrEmpty(operationId) && runningState.runId != operationId)
            {
                writer.WriteLine($"BUSY test {runningState.runId}");
                return;
            }

            string activeRunId = operationId;
            if (string.IsNullOrEmpty(activeRunId))
            {
                activeRunId = runningState?.runId ?? operation?.operationId;
            }

            if (!string.IsNullOrEmpty(s_CurrentTestJobGuid))
            {
                try
                {
                    TestRunnerApi.CancelTestRun(s_CurrentTestJobGuid);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityCliRunner: Exception while calling TestRunnerApi.CancelTestRun: {ex.Message}");
                }
            }

            TryCancelAnyActiveRunner();

            WriteCancelledResult(activeRunId);
            writer.WriteLine("CANCELLED");
        }

        private static bool TryCancelAnyActiveRunner()
        {
            try
            {
                var holderProp = typeof(TestRunnerApi).GetProperty("m_testJobDataHolder", BindingFlags.NonPublic | BindingFlags.Static);
                var holder = holderProp?.GetValue(null);
                var getAllRunnersMethod = holder?.GetType().GetMethod("GetAllRunners", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var runners = (System.Collections.IEnumerable)getAllRunnersMethod?.Invoke(holder, null);
                bool anyCancelled = false;
                if (runners != null)
                {
                    foreach (var r in runners)
                    {
                        var isRunningMethod = r.GetType().GetMethod("IsRunningJob", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        bool isRunning = isRunningMethod != null && (bool)isRunningMethod.Invoke(r, null);
                        if (isRunning)
                        {
                            var cancelMethod = r.GetType().GetMethod("CancelRun", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (cancelMethod != null)
                            {
                                var res = cancelMethod.Invoke(r, null);
                                if (res is bool b && b) anyCancelled = true;
                            }
                        }
                    }
                }
                return anyCancelled;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to cancel runners via reflection: {ex.Message}");
                return false;
            }
        }

        internal static void WriteCancelledResult(string targetRunId = null)
        {
            var state = ReadRunningState();
            string runId = targetRunId;
            if (string.IsNullOrEmpty(runId))
            {
                runId = state?.runId ?? Guid.NewGuid().ToString("N");
            }

            var result = new UnityTestRunResult
            {
                runId = runId,
                success = false,
                message = "Test run was cancelled or interrupted.",
                resultState = "Cancelled",
                failedTests = new List<FailedTestInfo>()
            };

            try
            {
                WriteAtomic(ResultsFilePath, JsonUtility.ToJson(result, true), runId);
                DeleteIfExists(RunningFilePath);
                UnityCliOperationStore.Complete(runId);
                HasActiveTestFilter = false;
                s_CurrentTestJobGuid = null;
                if (s_Callbacks != null)
                {
                    s_Callbacks.Reset();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to persist cancelled test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        internal static void WriteAtomic(string path, string content, string runId)
        {
            UnityCliOperationStore.WriteAtomic(path, content, runId);
        }
    }

    public class MyTestCallbacks : ICallbacks, IErrorCallbacks
    {
        private readonly List<FailedTestInfo> m_FailedTests = new List<FailedTestInfo>();
        private bool m_IsRunning = false;
        private string m_RunId;

        public bool IsRunning => m_IsRunning || File.Exists(RunTestsHandler.RunningFilePath);

        internal void Reset()
        {
            m_IsRunning = false;
            m_RunId = null;
            m_FailedTests.Clear();
        }

        internal void BindRun(string runId)
        {
            m_RunId = runId;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            if (string.IsNullOrEmpty(m_RunId) || !UnityCliOperationStore.IsOwnedBy(m_RunId, "test"))
            {
                m_IsRunning = false;
                return;
            }

            m_FailedTests.Clear();
            m_IsRunning = true;
            var state = RunTestsHandler.ReadRunningState();
            if (state != null)
            {
                m_RunId = state.runId;
                RunTestsHandler.UpdateTestRunStatus(state.runId, "Running");
            }
        }

        public void OnError(string message)
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, string.IsNullOrEmpty(message) ? "Test run failed with error." : message, "Failed");
        }

        public void OnRunCancelled(string reason = "Test run was cancelled or interrupted.")
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, reason, "Cancelled");
        }

        public void OnRunInterrupted(string reason)
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, reason, "Interrupted");
        }

        public void OnInfrastructureFailure(string reason)
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, reason, "InfrastructureFailure");
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                string resultState = result.ResultState ?? "";
                bool isCancelled = resultState == "Cancelled" ||
                                   resultState.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0;
                var runState = RunTestsHandler.ReadRunningState();
                bool transportInterrupted = runState != null &&
                    (runState.status == "Reloading" || runState.status == "ShuttingDown");
                bool didNotMatchAnyTests = RunTestsHandler.HasActiveTestFilter && result.FailCount == 0 && result.PassCount == 0 && result.SkipCount == 0;
                bool isFailed = result.FailCount > 0 || result.TestStatus == TestStatus.Failed || isCancelled || didNotMatchAnyTests;

                bool success = !isFailed;
                string message = isCancelled ? (!string.IsNullOrEmpty(result.Message) ? result.Message : "Test run was cancelled or interrupted.")
                               : didNotMatchAnyTests ? "No tests matched the supplied filter."
                               : "";

                if (transportInterrupted && isCancelled)
                {
                    message = "Test run was interrupted by Unity domain reload or shutdown.";
                    resultState = "Interrupted";
                }

                FinalizeTestRun(success, result.FailCount, result.PassCount, result.SkipCount, message, resultState);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Exception in RunFinished callback: {ex}");
            }
        }

        private void FinalizeTestRun(bool success, int failCount, int passCount, int skipCount, string message, string resultState)
        {
            string runId = null;
            try
            {
                string runningPath = RunTestsHandler.RunningFilePath;
                string resultsPath = RunTestsHandler.ResultsFilePath;
                string failuresPath = RunTestsHandler.FailuresFilePath;
                var state = RunTestsHandler.ReadRunningState();
                runId = m_RunId ?? state?.runId;

                if (state == null || string.IsNullOrEmpty(runId) || state.runId != runId ||
                    !UnityCliOperationStore.IsOwnedBy(runId, "test"))
                {
                    m_IsRunning = false;
                    return;
                }

                if (!m_IsRunning && !File.Exists(runningPath))
                {
                    return;
                }

                // A callback can arrive more than once (for example OnError
                // followed by RunFinished), and callbacks can straddle a domain
                // reload. Once a result exists for this run it is authoritative.
                if (!string.IsNullOrEmpty(runId) && File.Exists(resultsPath))
                {
                    var existing = JsonUtility.FromJson<UnityTestRunResult>(File.ReadAllText(resultsPath));
                    if (existing != null && existing.runId == runId)
                    {
                        m_IsRunning = false;
                        return;
                    }
                }

                m_IsRunning = false;

                Debug.Log($"UnityCliRunner: Finalizing test run. Success: {success}, ResultState: {resultState}, Message: {message}");

                if (File.Exists(failuresPath))
                {
                    File.Delete(failuresPath);
                }

                if (m_FailedTests.Count > 0)
                {
                    RunTestsHandler.WriteAtomic(failuresPath, FormatFailures(m_FailedTests), runId);
                }

                var runResult = new UnityTestRunResult
                {
                    runId = runId ?? Guid.NewGuid().ToString("N"),
                    success = success,
                    failCount = failCount,
                    passCount = passCount,
                    skipCount = skipCount,
                    message = message,
                    resultState = resultState,
                    failedTests = new List<FailedTestInfo>(m_FailedTests)
                };

                RunTestsHandler.HasActiveTestFilter = false;

                string json = JsonUtility.ToJson(runResult, true);
                RunTestsHandler.WriteAtomic(resultsPath, json, runResult.runId);
                if (File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }
                UnityCliOperationStore.Complete(runResult.runId);
                m_RunId = null;
                RunTestsHandler.s_CurrentTestJobGuid = null;
                Debug.Log($"UnityCliRunner: Playmode/Editmode tests completed. Success: {runResult.success}, Failed: {runResult.failCount}, Passed: {runResult.passCount}, Skipped: {runResult.skipCount}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Exception in FinalizeTestRun: {ex}");
            }
        }

        private static string FormatFailures(List<FailedTestInfo> failedTests)
        {
            var sb = new StringBuilder();
            foreach (var test in failedTests)
            {
                int durationMs = (int)Math.Round(test.duration * 1000);
                string durationStr = durationMs < 1 ? "< 1 ms" : $"{durationMs} ms";
                sb.AppendLine($"  \u001b[31mFailed\u001b[0m {test.fullName} [{durationStr}]");
                sb.AppendLine("  Error Message:");
                if (!string.IsNullOrEmpty(test.message))
                {
                    foreach (var line in test.message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
                    {
                        sb.AppendLine($"   {line}");
                    }
                }
                sb.AppendLine("  Stack Trace:");
                if (!string.IsNullOrEmpty(test.stackTrace))
                {
                    foreach (var line in test.stackTrace.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
                    {
                        sb.AppendLine($"   {line}");
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (!result.HasChildren && result.TestStatus == TestStatus.Failed)
            {
                m_FailedTests.Add(new FailedTestInfo
                {
                    name = result.Name,
                    fullName = result.FullName,
                    message = result.Message,
                    stackTrace = result.StackTrace,
                    duration = result.Duration
                });
            }
        }
    }
}

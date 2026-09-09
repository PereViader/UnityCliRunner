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

        private const string CallbackOwnerName = "UnityCliRunner.CallbackOwner";
        private static MyTestCallbacks s_Callbacks;
        private static TestRunnerApi s_RunnerApi;
        private static MethodInfo s_IsRunActiveMethod;
        internal static string s_CurrentTestJobGuid;
        private static readonly object s_RunStateLock = new object();
        private static UnityTestRunState s_CachedRunState;

        internal static string TempDirectory => UnityCliPaths.TempDir;
        internal static string RunningFilePath => UnityCliPaths.TestRunningFile;
        internal static string ResultsFilePath => UnityCliPaths.TestResultsFile;

        internal static void ClearCachedRunState()
        {
            lock (s_RunStateLock)
            {
                s_CachedRunState = null;
            }
        }

        internal static void MarkTransportInterruption(string status)
        {
            var state = ReadRunningState();
            if (state == null)
            {
                return;
            }

            state.status = status;
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }
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
                writer.WriteLine("ERROR: Missing operation id or test mode (all/playmode/editmode)");
                return;
            }

            string operationId = args[0];
            TestMode mode = args[1].ToLowerInvariant() switch
            {
                "playmode" => TestMode.PlayMode,
                "editmode" => TestMode.EditMode,
                "all" => TestMode.EditMode | TestMode.PlayMode,
                _ => (TestMode)(-1)
            };

            if ((int)mode == -1)
            {
                writer.WriteLine("ERROR: Invalid test mode. Must be all, playmode, or editmode");
                return;
            }

            string filter = "";
            string category = "";
            bool failedOnly = false;

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
                else if (args[i] == "--failed-only")
                {
                    failedOnly = true;
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

            try
            {
                List<string> failedTests = null;
                if (failedOnly)
                {
                    failedTests = GetPreviouslyFailedTestNames();
                    if (failedTests.Count == 0)
                    {
                        var emptyResult = new UnityTestRunResult
                        {
                            runId = operationId,
                            success = true,
                            failCount = 0,
                            passCount = 0,
                            skipCount = 0,
                            message = "No previously failed tests found.",
                            resultState = "Passed",
                            failedTests = new List<FailedTestInfo>()
                        };
                        WriteAtomic(ResultsFilePath, JsonUtility.ToJson(emptyResult, true), operationId);
                        UnityCliOperationStore.Complete(operationId);
                        writer.WriteLine("SUCCESS No previously failed tests found.");
                        writer.Flush();
                        return;
                    }
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

                RunTests(mode, filter, category, runId, failedTests?.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Unhandled exception during RunTests: {ex}");
                WriteInterruptedResult("Failed to start test run: " + ex.Message, operationId);
            }
        }

        private static List<string> GetPreviouslyFailedTestNames()
        {
            var failedNames = new List<string>();
            try
            {
                if (File.Exists(ResultsFilePath))
                {
                    string json = CommandHelper.ReadFileWithRetry(ResultsFilePath, maxRetries: 3, delayMs: 10);
                    var result = JsonUtility.FromJson<UnityTestRunResult>(json);
                    if (result?.failedTests != null)
                    {
                        foreach (var fail in result.failedTests)
                        {
                            string name = !string.IsNullOrEmpty(fail.fullName) ? fail.fullName : fail.name;
                            if (!string.IsNullOrEmpty(name) && !failedNames.Contains(name))
                            {
                                failedNames.Add(name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to read previous test results: {ex.Message}");
            }
            return failedNames;
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
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    totalTests = 0,
                    completedTests = 0,
                    passCount = 0,
                    failCount = 0,
                    skipCount = 0,
                    currentTestName = ""
                };
                lock (s_RunStateLock)
                {
                    s_CachedRunState = state;
                }
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
                return runId;
            }
            catch (Exception ex)
            {
                lock (s_RunStateLock)
                {
                    s_CachedRunState = null;
                }
                Debug.LogError($"UnityCliRunner: Failed to write test running state: {ex}");
                return null;
            }
        }

        private static void RunTests(TestMode mode, string filterText, string categoryText, string runId, string[] testNames = null)
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
                    testNames = testNames != null && testNames.Length > 0 ? testNames : null,
                    groupNames = !string.IsNullOrEmpty(filterText) ? new[] { filterText } : null,
                    categoryNames = !string.IsNullOrEmpty(categoryText) ? new[] { categoryText } : null
                };

                UpdateTestRunStatus(runId, "Executing");
                s_Callbacks.BindRun(runId);

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityCliRunner: Executing {mode} tests with filter '{filterText}', category '{categoryText}', testNames count '{(testNames?.Length ?? 0)}'...");
                s_CurrentTestJobGuid = s_RunnerApi.Execute(settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to start tests: {ex}");
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
            lock (s_RunStateLock)
            {
                if (s_CachedRunState != null)
                {
                    return s_CachedRunState; // Thread-safe in-memory fast path for worker threads
                }
            }
            try
            {
                if (!File.Exists(RunningFilePath))
                {
                    return null;
                }

                string text = CommandHelper.ReadFileWithRetry(RunningFilePath, maxRetries: 3, delayMs: 10);
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                var state = JsonUtility.FromJson<UnityTestRunState>(text);
                lock (s_RunStateLock)
                {
                    s_CachedRunState = state;
                }
                return state;
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to read test run state: {ex.Message}");
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
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }
            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to update test run state: {ex.Message}");
            }
        }

        internal static void UpdateTestRunProgress(string runId, int totalTests, int completedTests, int passCount, int failCount, int skipCount, string currentTestName, string status = null)
        {
            var state = ReadRunningState();
            if (state == null || state.runId != runId)
            {
                return;
            }

            if (!string.IsNullOrEmpty(status))
            {
                state.status = status;
            }
            state.totalTests = totalTests;
            state.completedTests = completedTests;
            state.passCount = passCount;
            state.failCount = failCount;
            state.skipCount = skipCount;
            if (currentTestName != null)
            {
                state.currentTestName = currentTestName;
            }
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }

            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to update test run progress: {ex.Message}");
            }
        }


        internal static void WriteInterruptedResult(string message, string targetRunId = null)
        {
            var state = ReadRunningState();
            string runId = targetRunId ?? state?.runId ?? Guid.NewGuid().ToString("N");
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
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to persist interrupted test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
            finally
            {
                DeleteRunningStateIfOwned(runId);
                ClearCachedRunState();
                UnityCliOperationStore.Complete(runId);
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
                        var existing = JsonUtility.FromJson<UnityTestRunResult>(CommandHelper.ReadFileWithRetry(ResultsFilePath, maxRetries: 3, delayMs: 10));
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

            string jobGuid = s_CurrentTestJobGuid;
            if (!string.IsNullOrEmpty(jobGuid))
            {
                UnityCliDispatcher.Enqueue(() =>
                {
                    try
                    {
                        TestRunnerApi.CancelTestRun(jobGuid);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"UnityCliRunner: Exception while calling TestRunnerApi.CancelTestRun: {ex.Message}");
                    }
                });
            }

            WriteCancelledResult(activeRunId);
            writer.WriteLine("CANCELLED");
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
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to persist cancelled test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
            finally
            {
                DeleteRunningStateIfOwned(runId);
                ClearCachedRunState();
                UnityCliOperationStore.Complete(runId);
                s_CurrentTestJobGuid = null;
                if (s_Callbacks != null)
                {
                    s_Callbacks.Reset();
                }
            }
        }

        internal static bool DeleteRunningStateIfOwned(string runId)
        {
            try
            {
                var state = ReadRunningState();
                if (state != null && (string.IsNullOrEmpty(runId) || state.runId == runId))
                {
                    lock (s_RunStateLock)
                    {
                        s_CachedRunState = null;
                    }
                    if (File.Exists(RunningFilePath))
                    {
                        File.Delete(RunningFilePath);
                    }
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to delete running state: {ex.Message}");
                return false;
            }
        }

        internal static void WriteAtomic(string path, string content, string runId)
        {
            UnityCliOperationStore.WriteAtomic(path, content, runId);
        }
    }
}

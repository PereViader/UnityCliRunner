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
        private static MyTestCallbacks s_Callbacks;
        private static MethodInfo s_IsRunActiveMethod;

        internal static bool HasActiveTestFilter { get; set; }

        internal static string TempDirectory => Path.Combine(Directory.GetCurrentDirectory(), "Temp");
        internal static string RunningFilePath => Path.Combine(TempDirectory, "unity_test_running.txt");
        internal static string ResultsFilePath => Path.Combine(TempDirectory, "unity_test_results.json");
        internal static string FailuresFilePath => Path.Combine(TempDirectory, "unity_test_failures.txt");

        public static void RegisterCallbacks()
        {
            if (CommandHelper.IsAssetImportWorkerProcess())
            {
                return;
            }

            foreach (var api in Resources.FindObjectsOfTypeAll<TestRunnerApi>())
            {
                try { UnityEngine.Object.DestroyImmediate(api); } catch { }
            }

            s_Callbacks = new MyTestCallbacks();
            var runnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            runnerApi.RegisterCallbacks(s_Callbacks);

            EditorApplication.update -= UpdateCheck;
            EditorApplication.update += UpdateCheck;
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

        private static void UpdateCheck()
        {
            CheckAndHandleInactiveRun();
        }

        internal static void CheckAndHandleInactiveRun()
        {
            if (!File.Exists(RunningFilePath))
            {
                return;
            }

            if (!IsTestRunActive() && !EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (s_Callbacks != null && s_Callbacks.IsRunning)
                {
                    s_Callbacks.OnRunCancelled("Test run was cancelled or interrupted.");
                }
                else
                {
                    try { File.Delete(RunningFilePath); } catch { }
                }
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
            if (args.Length < 1)
            {
                writer.WriteLine("ERROR: Missing test mode (playmode/editmode)");
                return;
            }

            TestMode mode = args[0].ToLowerInvariant() switch
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

            for (int i = 1; i < args.Length; i++)
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

            // Write running state files synchronously
            WriteTestRunningState();

            writer.WriteLine("RUNNING");
            writer.Flush();

            RunTests(mode, filter, category);
        }

        private static void WriteTestRunningState()
        {
            try
            {
                if (!Directory.Exists(TempDirectory))
                {
                    Directory.CreateDirectory(TempDirectory);
                }

                if (File.Exists(ResultsFilePath))
                {
                    File.Delete(ResultsFilePath);
                }
                File.WriteAllText(RunningFilePath, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write test running state: {ex}");
            }
        }

        private static void RunTests(TestMode mode, string filterText, string categoryText)
        {
            try
            {
                if (s_Callbacks == null)
                {
                    RegisterCallbacks();
                }

                var activeTestRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

                var filter = new Filter
                {
                    testMode = mode,
                    groupNames = !string.IsNullOrEmpty(filterText) ? new[] { filterText } : null,
                    categoryNames = !string.IsNullOrEmpty(categoryText) ? new[] { categoryText } : null
                };

                HasActiveTestFilter = !string.IsNullOrEmpty(filterText) || !string.IsNullOrEmpty(categoryText);

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityCliRunner: Executing {mode} tests with filter '{filterText}' and category '{categoryText}'...");
                activeTestRunnerApi.Execute(settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to start tests: {ex}");
                HasActiveTestFilter = false;
                if (File.Exists(RunningFilePath))
                {
                    File.Delete(RunningFilePath);
                }
            }
        }
    }

    public class MyTestCallbacks : ICallbacks, IErrorCallbacks
    {
        private readonly List<FailedTestInfo> m_FailedTests = new List<FailedTestInfo>();
        private bool m_IsRunning = false;

        public bool IsRunning => m_IsRunning || File.Exists(RunTestsHandler.RunningFilePath);

        public void RunStarted(ITestAdaptor testsToRun)
        {
            m_FailedTests.Clear();
            m_IsRunning = true;
        }

        public void OnError(string message)
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, string.IsNullOrEmpty(message) ? "Test run failed with error." : message, "Failed");
        }

        public void OnRunCancelled(string reason = "Test run was cancelled or interrupted.")
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, reason, "Cancelled");
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                bool isCancelled = result.ResultState == "Cancelled" ||
                                   result.ResultState.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0;
                bool didNotMatchAnyTests = RunTestsHandler.HasActiveTestFilter && result.FailCount == 0 && result.PassCount == 0 && result.SkipCount == 0;
                bool isFailed = result.FailCount > 0 || result.TestStatus == TestStatus.Failed || isCancelled || didNotMatchAnyTests;

                bool success = !isFailed;
                string message = isCancelled ? (!string.IsNullOrEmpty(result.Message) ? result.Message : "Test run was cancelled or interrupted.")
                               : didNotMatchAnyTests ? "No tests matched the supplied filter."
                               : "";

                FinalizeTestRun(success, result.FailCount, result.PassCount, result.SkipCount, message, result.ResultState);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Exception in RunFinished callback: {ex}");
            }
        }

        private void FinalizeTestRun(bool success, int failCount, int passCount, int skipCount, string message, string resultState)
        {
            try
            {
                string runningPath = RunTestsHandler.RunningFilePath;
                string resultsPath = RunTestsHandler.ResultsFilePath;
                string failuresPath = RunTestsHandler.FailuresFilePath;

                if (!m_IsRunning && !File.Exists(runningPath))
                {
                    return;
                }
                m_IsRunning = false;

                Debug.Log($"UnityCliRunner: Finalizing test run. Success: {success}, ResultState: {resultState}, Message: {message}");

                if (File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }
                if (File.Exists(failuresPath))
                {
                    File.Delete(failuresPath);
                }

                if (m_FailedTests.Count > 0)
                {
                    File.WriteAllText(failuresPath, FormatFailures(m_FailedTests), new UTF8Encoding(false));
                }

                var runResult = new UnityTestRunResult
                {
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
                File.WriteAllText(resultsPath, json);
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

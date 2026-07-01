using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RunTestsHandler : ICommandHandler
    {
        private static MyTestCallbacks s_Callbacks;
        internal static bool HasActiveTestFilter { get; set; }

        public static void RegisterCallbacks()
        {
            if (CommandHelper.IsAssetImportWorkerProcess())
            {
                return;
            }

            var existingApis = Resources.FindObjectsOfTypeAll<TestRunnerApi>();
            if (existingApis != null)
            {
                foreach (var api in existingApis)
                {
                    try { UnityEngine.Object.DestroyImmediate(api); } catch { }
                }
            }

            s_Callbacks = new MyTestCallbacks();
            var runnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            runnerApi.RegisterCallbacks(s_Callbacks);
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

            string modeStr = args[0].ToLowerInvariant();
            string filter = "";
            string category = "";

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length)
                {
                    filter = args[i + 1];
                    i++;
                }
                else if (args[i] == "--category" && i + 1 < args.Length)
                {
                    category = args[i + 1];
                    i++;
                }
            }

            TestMode mode;
            if (modeStr == "playmode")
            {
                mode = TestMode.PlayMode;
            }
            else if (modeStr == "editmode")
            {
                mode = TestMode.EditMode;
            }
            else
            {
                writer.WriteLine("ERROR: Invalid test mode. Must be playmode or editmode");
                return;
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
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string runningPath = Path.Combine(tempDir, "unity_test_running.txt");
                string resultsPath = Path.Combine(tempDir, "unity_test_results.json");

                if (File.Exists(resultsPath))
                {
                    File.Delete(resultsPath);
                }
                File.WriteAllText(runningPath, DateTime.UtcNow.ToString("o"));
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
                    s_Callbacks = new MyTestCallbacks();
                    var runnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                    runnerApi.RegisterCallbacks(s_Callbacks);
                }
                var activeTestRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

                var filter = new Filter();
                filter.testMode = mode;

                if (!string.IsNullOrEmpty(filterText))
                {
                    filter.groupNames = new[] { filterText };
                }

                if (!string.IsNullOrEmpty(categoryText))
                {
                    filter.categoryNames = new[] { categoryText };
                }

                HasActiveTestFilter = !string.IsNullOrEmpty(filterText) || !string.IsNullOrEmpty(categoryText);

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityCliRunner: Executing {mode} tests with filter '{filterText}' and category '{categoryText}'...");
                activeTestRunnerApi.Execute(settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to start tests: {ex}");
                HasActiveTestFilter = false;
                // Clean up state so we don't hang polling
                string runningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_running.txt");
                if (File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }
            }
        }
    }

    public class MyTestCallbacks : ICallbacks
    {
        private List<FailedTestInfo> m_FailedTests = new List<FailedTestInfo>();
        private bool m_IsRunning = false;

        public void RunStarted(ITestAdaptor testsToRun)
        {
            m_FailedTests.Clear();
            m_IsRunning = true;
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            try
            {
                if (!m_IsRunning)
                {
                    return;
                }
                m_IsRunning = false;

                Debug.Log($"UnityCliRunner: RunFinished called on callback instance {GetHashCode()}");
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                string runningPath = Path.Combine(tempDir, "unity_test_running.txt");
                string resultsPath = Path.Combine(tempDir, "unity_test_results.json");
                string failuresPath = Path.Combine(tempDir, "unity_test_failures.txt");

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
                    var sb = new StringBuilder();
                    foreach (var test in m_FailedTests)
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

                    File.WriteAllText(failuresPath, sb.ToString(), new UTF8Encoding(false));
                }

                bool didNotMatchAnyTests = RunTestsHandler.HasActiveTestFilter && result.FailCount == 0 && result.PassCount == 0 && result.SkipCount == 0;

                var runResult = new UnityTestRunResult
                {
                    success = result.FailCount == 0 && !didNotMatchAnyTests,
                    failCount = result.FailCount,
                    passCount = result.PassCount,
                    skipCount = result.SkipCount,
                    message = didNotMatchAnyTests ? "No tests matched the supplied filter." : "",
                    resultState = result.ResultState,
                    failedTests = new List<FailedTestInfo>(m_FailedTests)
                };

                RunTestsHandler.HasActiveTestFilter = false;

                string json = JsonUtility.ToJson(runResult, true);
                File.WriteAllText(resultsPath, json);
                Debug.Log($"UnityCliRunner: Playmode/Editmode tests completed. Success: {runResult.success}, Failed: {runResult.failCount}, Passed: {runResult.passCount}, Skipped: {runResult.skipCount}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Exception in RunFinished callback: {ex}");
            }
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

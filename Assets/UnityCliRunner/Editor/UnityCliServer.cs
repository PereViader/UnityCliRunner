using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCliRunner
{
    [InitializeOnLoad]
    public static class UnityCliServer
    {
        private static TcpListener s_Listener;
        private static Thread s_ServerThread;
        private static bool s_Running;
        
        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();
        private static MyTestCallbacks s_Callbacks;

        private static volatile bool s_IsCompiling;
        private static volatile bool s_IsUpdating;

        static UnityCliServer()
        {
            // Register callbacks for tests
            RegisterCallbacks();
            
            // Hook update to process main thread queue
            EditorApplication.update += OnEditorUpdate;
            
            // Start server
            StartServer();
            
            // Hook domain unload to stop server cleanly
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
        }

        [InitializeOnLoadMethod]
        private static void RegisterCallbacks()
        {
            s_Callbacks = ScriptableObject.CreateInstance<MyTestCallbacks>();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(s_Callbacks);
        }

        private static void StartServer()
        {
            if (s_Running) return;
            
            s_Running = true;
            s_ServerThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "UnityCliServerThread"
            };
            s_ServerThread.Start();
        }

        private static void StopServer()
        {
            if (!s_Running) return;
            
            s_Running = false;
            try
            {
                s_Listener?.Stop();
            }
            catch (Exception) { }

            if (s_ServerThread != null && s_ServerThread.IsAlive)
            {
                s_ServerThread.Join(500);
            }

            DeletePortFile();
            Debug.Log("UnityCliRunner: Socket server stopped.");
        }

        private static void OnEditorUpdate()
        {
            s_IsCompiling = EditorApplication.isCompiling;
            s_IsUpdating = EditorApplication.isUpdating;

            while (MainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        private static void ServerLoop()
        {
            try
            {
                // Bind to port 0 to let OS assign a random free port
                s_Listener = new TcpListener(IPAddress.Loopback, 0);
                s_Listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s_Listener.Start();

                int port = ((IPEndPoint)s_Listener.LocalEndpoint).Port;
                WritePortFile(port);
                Debug.Log($"UnityCliRunner: Socket server started on 127.0.0.1:{port}");

                while (s_Running)
                {
                    TcpClient client;
                    try
                    {
                        client = s_Listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        // listener stopped
                        break;
                    }

                    ThreadPool.QueueUserWorkItem(state => HandleClient((TcpClient)state), client);
                }
            }
            catch (Exception e)
            {
                if (s_Running)
                {
                    Debug.LogError($"UnityCliRunner: Exception in server loop: {e}");
                }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            NetworkStream stream = null;
            StreamReader reader = null;
            StreamWriter writer = null;

            try
            {
                stream = client.GetStream();
                reader = new StreamReader(stream, Encoding.UTF8);
                // We use new UTF8Encoding(false) to disable emitting a UTF-8 Byte Order Mark (BOM).
                // Emitting a BOM (\xEF\xBB\xBF in bytes) is non-standard for sockets and would be prepended
                // to our responses, breaking string comparisons (e.g. [ "$response" = "READY" ]) in the bash script.
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                string line = reader.ReadLine();
                if (string.IsNullOrEmpty(line))
                {
                    writer.WriteLine("ERROR: Empty command");
                    return;
                }

                line = line.Trim();
                string[] parts = line.Split(new[] { ' ' }, 3);
                string command = parts[0].ToUpperInvariant();

                switch (command)
                {
                    case "REFRESH":
                        MainThreadQueue.Enqueue(() => {
                            Debug.Log("UnityCliRunner: Triggering AssetDatabase.Refresh()");
                            AssetDatabase.Refresh();
                        });
                        writer.WriteLine("REFRESHING");
                        break;

                    case "POLL_REFRESH":
                        if (s_IsCompiling)
                        {
                            writer.WriteLine("COMPILING");
                        }
                        else if (s_IsUpdating)
                        {
                            writer.WriteLine("UPDATING");
                        }
                        else
                        {
                            writer.WriteLine("READY");
                        }
                        break;

                    case "RUN_TESTS":
                        if (parts.Length < 2)
                        {
                            writer.WriteLine("ERROR: Missing test mode (playmode/editmode)");
                            break;
                        }
                        string modeStr = parts[1].ToLowerInvariant();
                        string filter = parts.Length > 2 ? parts[2] : "";

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
                            break;
                        }

                        // Write running state files synchronously
                        WriteTestRunningState();

                        MainThreadQueue.Enqueue(() => {
                            RunTests(mode, filter);
                        });

                        writer.WriteLine("RUNNING");
                        break;

                    case "POLL_TESTS":
                        string runningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_running.txt");
                        string resultsPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_results.json");

                        if (File.Exists(runningPath))
                        {
                            writer.WriteLine("RUNNING");
                        }
                        else if (File.Exists(resultsPath))
                        {
                            try
                            {
                                string content = File.ReadAllText(resultsPath);
                                var res = JsonUtility.FromJson<UnityTestRunResult>(content);
                                if (res.success)
                                {
                                    writer.WriteLine($"SUCCESS {res.passCount} passed");
                                }
                                else
                                {
                                    writer.WriteLine($"FAILURE {res.failCount} failed, {res.passCount} passed");
                                }
                                File.Delete(resultsPath);
                            }
                            catch (Exception ex)
                            {
                                writer.WriteLine($"ERROR: {ex.Message}");
                            }
                        }
                        else
                        {
                            writer.WriteLine("IDLE");
                        }
                        break;

                    default:
                        writer.WriteLine($"ERROR: Unknown command: {command}");
                        break;
                }
            }
            catch (Exception e)
            {
                try
                {
                    writer?.WriteLine($"ERROR: {e.Message}");
                }
                catch { }
            }
            finally
            {
                reader?.Dispose();
                writer?.Dispose();
                stream?.Dispose();
                client.Close();
            }
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

        private static void RunTests(TestMode mode, string filterText)
        {
            try
            {
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                
                var filter = new Filter();
                filter.testMode = mode;

                if (!string.IsNullOrEmpty(filterText))
                {
                    filter.groupNames = new[] { filterText };
                }

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityCliRunner: Executing {mode} tests with filter '{filterText}'...");
                api.Execute(settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to start tests: {ex}");
                // Clean up state so we don't hang polling
                string runningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_running.txt");
                if (File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }
            }
        }

        private static void WritePortFile(int port)
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string portFilePath = Path.Combine(tempDir, "unity_cli_port.txt");
                File.WriteAllText(portFilePath, port.ToString());
            }
            catch (Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write port file: {e}");
            }
        }

        private static void DeletePortFile()
        {
            try
            {
                string portFilePath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_cli_port.txt");
                if (File.Exists(portFilePath))
                {
                    File.Delete(portFilePath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to delete port file: {e}");
            }
        }
    }

    [Serializable]
    public class UnityTestRunResult
    {
        public bool success;
        public int failCount;
        public int passCount;
        public string resultState;
    }

    public class MyTestCallbacks : ScriptableObject, ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                string runningPath = Path.Combine(tempDir, "unity_test_running.txt");
                string resultsPath = Path.Combine(tempDir, "unity_test_results.json");

                if (File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }

                var runResult = new UnityTestRunResult
                {
                    success = result.FailCount == 0,
                    failCount = result.FailCount,
                    passCount = result.PassCount,
                    resultState = result.ResultState
                };

                string json = JsonUtility.ToJson(runResult, true);
                File.WriteAllText(resultsPath, json);
                Debug.Log($"UnityCliRunner: Playmode/Editmode tests completed. Success: {runResult.success}, Failed: {runResult.failCount}, Passed: {runResult.passCount}");
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
        }
    }
}

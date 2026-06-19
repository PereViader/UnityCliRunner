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

        private static readonly object s_Lock = new object();
        private static TcpClient s_ActiveClient;
        private static NetworkStream s_ActiveStream;
        private static StreamWriter s_ActiveWriter;

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
            CleanupActiveClient();
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
            bool keepOpen = false;

            try
            {
                stream = client.GetStream();
                reader = new StreamReader(stream, Encoding.UTF8);
                writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

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

                        keepOpen = true; // Keep connection open for test results
                        
                        var capturedClient = client;
                        var capturedStream = stream;
                        var capturedWriter = writer;

                        MainThreadQueue.Enqueue(() => {
                            RunTests(mode, filter, capturedClient, capturedWriter, capturedStream);
                        });
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
                if (!keepOpen)
                {
                    reader?.Dispose();
                    writer?.Dispose();
                    stream?.Dispose();
                    client.Close();
                }
            }
        }

        private static void RunTests(TestMode mode, string filterText, TcpClient client, StreamWriter writer, NetworkStream stream)
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
                
                lock (s_Lock)
                {
                    CleanupActiveClient();
                    s_ActiveClient = client;
                    s_ActiveStream = stream;
                    s_ActiveWriter = writer;
                }

                Debug.Log($"UnityCliRunner: Executing {mode} tests with filter '{filterText}'...");
                api.Execute(settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to start tests: {ex}");
                try
                {
                    writer.WriteLine($"ERROR: Failed to start tests: {ex.Message}");
                    writer.Dispose();
                    stream.Dispose();
                    client.Close();
                }
                catch { }
                
                lock (s_Lock)
                {
                    if (s_ActiveClient == client)
                    {
                        s_ActiveClient = null;
                        s_ActiveStream = null;
                        s_ActiveWriter = null;
                    }
                }
            }
        }

        public static void ReportTestResults(bool success, int failCount, int passCount)
        {
            lock (s_Lock)
            {
                if (s_ActiveWriter != null)
                {
                    try
                    {
                        if (success)
                        {
                            s_ActiveWriter.WriteLine($"SUCCESS {passCount} passed");
                        }
                        else
                        {
                            s_ActiveWriter.WriteLine($"FAILURE {failCount} failed, {passCount} passed");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"UnityCliRunner: Failed to write test results to client: {ex}");
                    }
                    finally
                    {
                        CleanupActiveClient();
                    }
                }
            }
        }

        private static void CleanupActiveClient()
        {
            if (s_ActiveWriter != null)
            {
                try { s_ActiveWriter.Dispose(); } catch { }
                s_ActiveWriter = null;
            }
            if (s_ActiveStream != null)
            {
                try { s_ActiveStream.Dispose(); } catch { }
                s_ActiveStream = null;
            }
            if (s_ActiveClient != null)
            {
                try { s_ActiveClient.Close(); } catch { }
                s_ActiveClient = null;
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

    public class MyTestCallbacks : ScriptableObject, ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            UnityCliServer.ReportTestResults(result.FailCount == 0, result.FailCount, result.PassCount);
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    [InitializeOnLoad]
    public static class UnityCliServer
    {
        private const int AnyAvailablePort = 0;
        private const string PortFileName = "unity_cli_port.txt";

        private static TcpListener s_Listener;
        private static Thread s_ServerThread;
        private static bool s_Running;

        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        private static volatile bool s_IsCompiling;
        private static volatile bool s_IsUpdating;
        private static volatile bool s_RefreshPending;
        private static volatile bool s_ScriptCompilationFailed;
        private static volatile bool s_CompilationRequested;

        internal static bool HasActiveTestFilter { get; set; }

        internal static bool IsCompiling => s_IsCompiling;
        internal static bool IsUpdating => s_IsUpdating;
        internal static bool ScriptCompilationFailed => s_ScriptCompilationFailed;

        internal static bool RefreshPending
        {
            get => s_RefreshPending;
            set => s_RefreshPending = value;
        }

        internal static bool CompilationRequested
        {
            get => s_CompilationRequested;
            set => s_CompilationRequested = value;
        }

        private static readonly Dictionary<string, ICommandHandler> s_Handlers = new Dictionary<string, ICommandHandler>
        {
            { "PING", new PingHandler() },
            { "EXIT", new ExitHandler() },
            { "REFRESH", new RefreshHandler() },
            { "POLL_REFRESH", new PollRefreshHandler() },
            { "RECOMPILE", new RecompileHandler() },
            { "RUN_TESTS", new RunTestsHandler() },
            { "POLL_TESTS", new PollTestsHandler() },
            { "EXECUTE_METHOD", new ExecuteMethodHandler() },
            { "POLL_EXECUTE", new PollExecuteHandler() }
        };

        static UnityCliServer()
        {
            if(IsAssetImportWorkerProcess())
            {
                return;
            }

            // Register callbacks for tests
            RunTestsHandler.RegisterCallbacks();

            // Hook update to process main thread queue
            EditorApplication.update += OnEditorUpdate;

            // Start server
            StartServer();

            // Hook domain unload to stop server cleanly
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
        }

        internal static void EnqueueToMainThread(Action action)
        {
            MainThreadQueue.Enqueue(action);
        }

        internal static void UpdateCompilationState()
        {
            s_IsCompiling = EditorApplication.isCompiling;
            s_IsUpdating = EditorApplication.isUpdating;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;

            if (s_CompilationRequested && s_IsCompiling)
            {
                s_CompilationRequested = false;
            }
        }

        internal static bool IsAssetImportWorkerProcess()
        {
            string[] args = Environment.GetCommandLineArgs();
            for(int i = 0; i < args.Length; i++)
            {
                if(IsAssetImportWorkerName(args[i]))
                {
                    return true;
                }

                if(string.Equals(args[i], "-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && IsAssetImportWorkerName(args[i + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAssetImportWorkerName(string value)
        {
            return string.Equals(value, "AssetImport", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("AssetImportWorker", StringComparison.OrdinalIgnoreCase);
        }

        private static void StartServer()
        {
            if(s_Running)
                return;

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
            if(!s_Running)
                return;

            s_Running = false;
            try
            {
                s_Listener?.Stop();
            }
            catch(Exception) { }

            if(s_ServerThread != null && s_ServerThread.IsAlive)
            {
                s_ServerThread.Join(500);
            }

            Debug.Log("UnityCliRunner: Socket server stopped.");
        }

        private static void OnEditorUpdate()
        {
            UpdateCompilationState();

            while(MainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch(Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        private static void ServerLoop()
        {
            try
            {
                int stickyPort = ReadPortFile();
                s_Listener = CreateStartedListener(stickyPort);
                int port = ((IPEndPoint) s_Listener.LocalEndpoint).Port;

                WritePortFile(port);
                Debug.Log($"UnityCliRunner: Socket server started on 127.0.0.1:{port}");

                while(s_Running)
                {
                    TcpClient client;
                    try
                    {
                        client = s_Listener.AcceptTcpClient();
                    }
                    catch(SocketException)
                    {
                        // listener stopped
                        break;
                    }

                    ThreadPool.QueueUserWorkItem(state => HandleClient((TcpClient) state), client);
                }
            }
            catch(Exception e)
            {
                if(s_Running)
                {
                    Debug.LogError($"UnityCliRunner: Exception in server loop: {e}");
                }
            }
        }

        private static TcpListener CreateStartedListener(int preferredPort)
        {
            if(preferredPort > AnyAvailablePort)
            {
                try
                {
                    return CreateStartedListenerForPort(preferredPort);
                }
                catch(SocketException e)
                {
                    Debug.LogWarning($"UnityCliRunner: Sticky port {preferredPort} is unavailable ({e.SocketErrorCode}); selecting a new port.");
                }
            }

            return CreateStartedListenerForPort(AnyAvailablePort);
        }

        private static TcpListener CreateStartedListenerForPort(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                return listener;
            }
            catch
            {
                listener.Stop();
                throw;
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
                if(string.IsNullOrEmpty(line))
                {
                    writer.WriteLine("ERROR: Empty command");
                    return;
                }

                line = line.Trim();
                string[] parts = line.Split(new[] { ' ' }, 2);
                string command = parts[0].ToUpperInvariant();
                string payload = parts.Length > 1 ? parts[1].Trim() : "";

                if (s_Handlers.TryGetValue(command, out var handler))
                {
                    handler.Handle(payload, writer);
                }
                else
                {
                    writer.WriteLine($"ERROR: Unknown command: {command}");
                }
            }
            catch(Exception e)
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

        private static void WritePortFile(int port)
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if(!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string portFilePath = Path.Combine(tempDir, PortFileName);
                File.WriteAllText(portFilePath, port.ToString());
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write port file: {e}");
            }
        }

        private static int ReadPortFile()
        {
            try
            {
                string portFilePath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", PortFileName);
                if(!File.Exists(portFilePath))
                {
                    return AnyAvailablePort;
                }

                string portText = File.ReadAllText(portFilePath);
                return int.TryParse(portText, out int port)
                    ? port
                    : AnyAvailablePort;
            }
            catch(Exception e)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to read port file: {e}");
                return AnyAvailablePort;
            }
        }

        public static void RefreshFromCommandLine()
        {
            try
            {
                Debug.Log("UnityCliRunner: Triggering AssetDatabase.Refresh() from command line.");
                UnityCliCompilationTracker.DeleteDiagnosticsFile();
                UnityCliCompilationTracker.ClearActiveEntries();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                UnityCliCompilationTracker.WriteActiveErrorsToFile();
                EditorApplication.Exit(EditorUtility.scriptCompilationFailed ? 1 : 0);
            }
            catch(Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Exception in RefreshFromCommandLine: {ex}");
                EditorApplication.Exit(1);
            }
        }

        public static void ExecuteMethodFromCommandLine()
        {
            try
            {
                string[] args = System.Environment.GetCommandLineArgs();
                string targetMethodName = null;
                int targetIndex = -1;
                for(int i = 0; i < args.Length - 1; i++)
                {
                    if(args[i] == "-executeMethodName")
                    {
                        targetMethodName = args[i + 1];
                        targetIndex = i + 1;
                        break;
                    }
                }

                if(string.IsNullOrEmpty(targetMethodName))
                {
                    Debug.LogError("UnityCliRunner: Missing -executeMethodName argument.");
                    EditorApplication.Exit(1);
                    return;
                }

                int lastDot = targetMethodName.LastIndexOf('.');
                if(lastDot == -1)
                {
                    Debug.LogError($"UnityCliRunner: Invalid method format: '{targetMethodName}'. Expected FullyQualifiedType.Method");
                    EditorApplication.Exit(1);
                    return;
                }

                string typeName = targetMethodName.Substring(0, lastDot);
                string methodName = targetMethodName.Substring(lastDot + 1);

                var targetType = CommandHelper.FindType(typeName);
                if(targetType == null)
                {
                    Debug.LogError($"UnityCliRunner: Type not found: '{typeName}'");
                    EditorApplication.Exit(1);
                    return;
                }

                var methodParamsList = new List<string>();
                for(int i = targetIndex + 1; i < args.Length; i++)
                {
                    methodParamsList.Add(args[i]);
                }

                System.Reflection.MethodInfo targetMethod = null;
                try
                {
                    targetMethod = CommandHelper.FindStaticMethod(targetType, methodName, methodParamsList.Count);
                }
                catch(System.Reflection.AmbiguousMatchException ex)
                {
                    Debug.LogError($"UnityCliRunner: {ex.Message}");
                    EditorApplication.Exit(1);
                    return;
                }

                if(targetMethod == null)
                {
                    Debug.LogError($"UnityCliRunner: Static method '{methodName}' not found in type '{typeName}'");
                    EditorApplication.Exit(1);
                    return;
                }

                ExecuteMethodHandler.WriteExecuteRunningState();

                ExecuteMethodHandler.ExecuteMethod(targetMethod, methodParamsList.ToArray());

                string resultsPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_result.json");
                if(File.Exists(resultsPath))
                {
                    try
                    {
                        string content = File.ReadAllText(resultsPath);
                        var res = JsonUtility.FromJson<UnityExecuteResult>(content);
                        if(res.success)
                        {
                            EditorApplication.Exit(0);
                            return;
                        }
                    }
                    catch { }
                }

                EditorApplication.Exit(1);
            }
            catch(Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Exception in ExecuteMethodFromCommandLine: {ex}");
                EditorApplication.Exit(1);
            }
        }
    }
}

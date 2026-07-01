using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
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
        private static MyTestCallbacks s_Callbacks;
        private static TestRunnerApi s_TestRunnerApi;

        private static volatile bool s_IsCompiling;
        private static volatile bool s_IsUpdating;
        private static volatile bool s_RefreshPending;
        private static volatile bool s_ScriptCompilationFailed;
        private static volatile bool s_CompilationRequested;

        internal static bool HasActiveTestFilter { get; set; }

        static UnityCliServer()
        {
            if(IsAssetImportWorkerProcess())
            {
                return;
            }

            // Register callbacks for tests
            RegisterCallbacks();

            // Hook update to process main thread queue
            EditorApplication.update += OnEditorUpdate;

            // Start server
            StartServer();

            // Hook domain unload to stop server cleanly
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
        }

        private static bool IsAssetImportWorkerProcess()
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

        private static void RegisterCallbacks()
        {
            if(IsAssetImportWorkerProcess())
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
            s_IsCompiling = EditorApplication.isCompiling;
            s_IsUpdating = EditorApplication.isUpdating;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;

            if (s_CompilationRequested && s_IsCompiling)
            {
                s_CompilationRequested = false;
            }

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

                switch(command)
                {
                    case "REFRESH":
                        s_RefreshPending = true;
                        MainThreadQueue.Enqueue(() =>
                        {
                            try
                            {
                                Debug.Log("UnityCliRunner: Triggering AssetDatabase.Refresh()");
                                UnityCliCompilationTracker.DeleteDiagnosticsFile();
                                UnityCliCompilationTracker.ClearActiveEntries();
                                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                            }
                            finally
                            {
                                s_RefreshPending = false;
                            }
                        });
                        writer.WriteLine("REFRESHING");
                        break;

                    case "RECOMPILE":
                        s_RefreshPending = true;
                        s_CompilationRequested = true;
                        MainThreadQueue.Enqueue(() =>
                        {
                            try
                            {
                                Debug.Log("UnityCliRunner: Triggering force recompilation via CompilationPipeline.RequestScriptCompilation()");
                                UnityCliCompilationTracker.DeleteDiagnosticsFile();
                                UnityCliCompilationTracker.ClearActiveEntries();
                                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(UnityEditor.Compilation.RequestScriptCompilationOptions.CleanBuildCache);
                            }
                            finally
                            {
                                s_RefreshPending = false;
                            }
                        });
                        writer.WriteLine("RECOMPILING");
                        break;

                    case "EXIT":
                        writer.WriteLine("EXITING");
                        MainThreadQueue.Enqueue(() =>
                        {
                            Debug.Log("UnityCliRunner: Shutdown requested via socket.");
                            EditorApplication.Exit(0);
                        });
                        break;

                    case "POLL_REFRESH":
                        WriteRefreshPollResponse(writer);
                        break;

                    case "RUN_TESTS":
                        if(s_ScriptCompilationFailed)
                        {
                            writer.WriteLine("FAILURE Compilation failed");
                            break;
                        }

                        if(parts.Length < 2)
                        {
                            writer.WriteLine("ERROR: Missing arguments");
                            break;
                        }

                        string[] args = SplitArguments(parts[1]);
                        if(args.Length < 1)
                        {
                            writer.WriteLine("ERROR: Missing test mode (playmode/editmode)");
                            break;
                        }

                        string modeStr = args[0].ToLowerInvariant();
                        string filter = "";
                        string category = "";

                        for(int i = 1; i < args.Length; i++)
                        {
                            if(args[i] == "--filter" && i + 1 < args.Length)
                            {
                                filter = args[i + 1];
                                i++;
                            }
                            else if(args[i] == "--category" && i + 1 < args.Length)
                            {
                                category = args[i + 1];
                                i++;
                            }
                        }

                        TestMode mode;
                        if(modeStr == "playmode")
                        {
                            mode = TestMode.PlayMode;
                        }
                        else if(modeStr == "editmode")
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

                        MainThreadQueue.Enqueue(() =>
                        {
                            RunTests(mode, filter, category);
                        });

                        writer.WriteLine("RUNNING");
                        break;

                    case "POLL_TESTS":
                        string runningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_running.txt");
                        string resultsPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_results.json");

                        if(File.Exists(runningPath))
                        {
                            writer.WriteLine("RUNNING");
                        }
                        else if(File.Exists(resultsPath))
                        {
                            try
                            {
                                string content = File.ReadAllText(resultsPath);
                                var res = JsonUtility.FromJson<UnityTestRunResult>(content);
                                string skipStr = res.skipCount > 0 ? $", {res.skipCount} skipped" : "";
                                if(res.success)
                                {
                                    writer.WriteLine($"SUCCESS {res.passCount} passed{skipStr}");
                                }
                                else if(!string.IsNullOrEmpty(res.message))
                                {
                                    writer.WriteLine($"FAILURE {res.message}");
                                }
                                else
                                {
                                    writer.WriteLine($"FAILURE {res.failCount} failed, {res.passCount} passed{skipStr}");
                                }
                            }
                            catch(Exception ex)
                            {
                                writer.WriteLine($"ERROR: {ex.Message}");
                            }
                        }
                        else
                        {
                            writer.WriteLine("IDLE");
                        }
                        break;

                    case "PING":
                        writer.WriteLine("PONG");
                        break;

                    case "EXECUTE_METHOD":
                        if(s_ScriptCompilationFailed)
                        {
                            writer.WriteLine("FAILURE Compilation failed");
                            break;
                        }

                        string methodArgs = line.Length > 14 ? line.Substring(14).Trim() : "";
                        if(string.IsNullOrEmpty(methodArgs))
                        {
                            writer.WriteLine("ERROR: Missing method name");
                            break;
                        }

                        string[] execArgs = SplitArguments(methodArgs);
                        if(execArgs.Length == 0)
                        {
                            writer.WriteLine("ERROR: Missing method name");
                            break;
                        }

                        string targetMethodName = execArgs[0];
                        int lastDot = targetMethodName.LastIndexOf('.');
                        if(lastDot == -1)
                        {
                            writer.WriteLine($"ERROR: Invalid method format: '{targetMethodName}'. Expected FullyQualifiedType.Method");
                            break;
                        }

                        string typeName = targetMethodName.Substring(0, lastDot);
                        string methodName = targetMethodName.Substring(lastDot + 1);

                        var targetType = FindType(typeName);
                        if(targetType == null)
                        {
                            writer.WriteLine($"ERROR: Type not found: '{typeName}'");
                            break;
                        }

                        var methodParamsList = new List<string>();
                        for(int i = 1; i < execArgs.Length; i++)
                        {
                            methodParamsList.Add(execArgs[i]);
                        }

                        MethodInfo targetMethod = null;
                        try
                        {
                            targetMethod = FindStaticMethod(targetType, methodName, methodParamsList.Count);
                        }
                        catch(AmbiguousMatchException ex)
                        {
                            writer.WriteLine($"ERROR: {ex.Message}");
                            break;
                        }

                        if(targetMethod == null)
                        {
                            writer.WriteLine($"ERROR: Static method '{methodName}' not found in type '{typeName}'");
                            break;
                        }

                        WriteExecuteRunningState();

                        MainThreadQueue.Enqueue(() =>
                        {
                            ExecuteMethod(targetMethod, methodParamsList.ToArray());
                        });

                        writer.WriteLine("RUNNING");
                        break;

                    case "POLL_EXECUTE":
                        string executeRunningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_running.txt");
                        string executeResultPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_result.json");

                        if(File.Exists(executeRunningPath))
                        {
                            writer.WriteLine("RUNNING");
                        }
                        else if(File.Exists(executeResultPath))
                        {
                            try
                            {
                                string content = File.ReadAllText(executeResultPath);
                                var res = JsonUtility.FromJson<UnityExecuteResult>(content);
                                if(res.success)
                                {
                                    if(!string.IsNullOrEmpty(res.payload))
                                    {
                                        writer.WriteLine($"SUCCESS {res.payload}");
                                    }
                                    else
                                    {
                                        writer.WriteLine("SUCCESS");
                                    }
                                }
                                else
                                {
                                    writer.WriteLine($"FAILURE {res.message}");
                                }
                            }
                            catch(Exception ex)
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

        private static void WriteRefreshPollResponse(StreamWriter writer)
        {
            string response = "UPDATING";
            var evt = new ManualResetEvent(false);
            MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    response = GetRefreshPollResponse();
                }
                finally
                {
                    evt.Set();
                }
            });

            if(!evt.WaitOne(2000))
            {
                writer.WriteLine("UPDATING");
                return;
            }

            writer.WriteLine(response);
        }

        private static string GetRefreshPollResponse()
        {
            s_IsCompiling = EditorApplication.isCompiling;
            s_IsUpdating = EditorApplication.isUpdating;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;

            if(s_RefreshPending || s_CompilationRequested)
            {
                return "COMPILING";
            }

            if(s_IsCompiling)
            {
                return "COMPILING";
            }

            if(s_IsUpdating)
            {
                return "UPDATING";
            }

            if(s_ScriptCompilationFailed)
            {
                UnityCliCompilationTracker.WriteActiveErrorsToFile();
                return "COMPILATION_ERROR";
            }

            UnityCliCompilationTracker.WriteActiveErrorsToFile();
            return "READY";
        }

        private static void WriteTestRunningState()
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if(!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string runningPath = Path.Combine(tempDir, "unity_test_running.txt");
                string resultsPath = Path.Combine(tempDir, "unity_test_results.json");

                if(File.Exists(resultsPath))
                {
                    File.Delete(resultsPath);
                }
                File.WriteAllText(runningPath, DateTime.UtcNow.ToString("o"));
            }
            catch(Exception ex)
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
                s_TestRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

                var filter = new Filter();
                filter.testMode = mode;

                if(!string.IsNullOrEmpty(filterText))
                {
                    filter.groupNames = new[] { filterText };
                }

                if(!string.IsNullOrEmpty(categoryText))
                {
                    filter.categoryNames = new[] { categoryText };
                }

                HasActiveTestFilter = !string.IsNullOrEmpty(filterText) || !string.IsNullOrEmpty(categoryText);

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityCliRunner: Executing {mode} tests with filter '{filterText}' and category '{categoryText}'...");
                s_TestRunnerApi.Execute(settings);
            }
            catch(Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to start tests: {ex}");
                HasActiveTestFilter = false;
                // Clean up state so we don't hang polling
                string runningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_running.txt");
                if(File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }
            }
        }

        private static string[] SplitArguments(string commandLine)
        {
            var args = new List<string>();
            if (string.IsNullOrEmpty(commandLine))
            {
                return args.ToArray();
            }

            var current = new StringBuilder();
            bool inQuotes = false;
            bool isEscaped = false;
            bool inArg = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (isEscaped)
                {
                    current.Append(c);
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    if (i + 1 < commandLine.Length && (commandLine[i + 1] == '"' || commandLine[i + 1] == '\\'))
                    {
                        isEscaped = true;
                        inArg = true;
                    }
                    else
                    {
                        current.Append(c);
                        inArg = true;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                    inArg = true;
                }
                else if (c == ' ' && !inQuotes)
                {
                    if (inArg)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                        inArg = false;
                    }
                }
                else
                {
                    current.Append(c);
                    inArg = true;
                }
            }

            if (inArg)
            {
                args.Add(current.ToString());
            }
            return args.ToArray();
        }

        private static MethodInfo FindStaticMethod(Type type, string methodName, int paramCount)
        {
            var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo candidate = null;
            int matchCount = 0;
            foreach(var m in methods)
            {
                if(m.Name == methodName)
                {
                    if(m.GetParameters().Length == paramCount)
                    {
                        candidate = m;
                        matchCount++;
                    }
                }
            }
            if(matchCount == 1)
            {
                return candidate;
            }
            if(matchCount > 1)
            {
                throw new AmbiguousMatchException($"Ambiguous match: multiple static methods named '{methodName}' with {paramCount} parameters found in type '{type.FullName}'.");
            }
            foreach(var m in methods)
            {
                if(m.Name == methodName)
                {
                    return m;
                }
            }
            return null;
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

        private static void WriteExecuteRunningState()
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if(!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string runningPath = Path.Combine(tempDir, "unity_execute_running.txt");
                string resultsPath = Path.Combine(tempDir, "unity_execute_result.json");

                if(File.Exists(resultsPath))
                {
                    File.Delete(resultsPath);
                }
                File.WriteAllText(runningPath, DateTime.UtcNow.ToString("o"));
            }
            catch(Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write execute running state: {ex}");
            }
        }

        private static void ExecuteMethod(System.Reflection.MethodInfo method, string[] stringParams)
        {
            string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
            string runningPath = Path.Combine(tempDir, "unity_execute_running.txt");
            string resultsPath = Path.Combine(tempDir, "unity_execute_result.json");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool success = false;
            string errorMsg = "";
            string payload = null;

            try
            {
                Debug.Log($"UnityCliRunner: Executing method '{method.DeclaringType.FullName}.{method.Name}'...");

                var paramInfos = method.GetParameters();
                int expectedCount = paramInfos.Length;
                int providedCount = stringParams != null ? stringParams.Length : 0;
                if(expectedCount != providedCount)
                {
                    throw new ArgumentException($"Parameter count mismatch. Method '{method.DeclaringType.FullName}.{method.Name}' expects {expectedCount} parameters, but {providedCount} were provided.");
                }

                object[] convertedParams = null;
                if(expectedCount > 0)
                {
                    convertedParams = new object[expectedCount];
                    for(int i = 0; i < expectedCount; i++)
                    {
                        string rawArg = stringParams[i];
                        Type paramType = paramInfos[i].ParameterType;
                        try
                        {
                            if(paramType == typeof(string))
                            {
                                convertedParams[i] = rawArg;
                            }
                            else if(paramType == typeof(int))
                            {
                                convertedParams[i] = int.Parse(rawArg);
                            }
                            else if(paramType == typeof(float))
                            {
                                convertedParams[i] = float.Parse(rawArg, System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else if(paramType == typeof(double))
                            {
                                convertedParams[i] = double.Parse(rawArg, System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else if(paramType == typeof(bool))
                            {
                                convertedParams[i] = bool.Parse(rawArg);
                            }
                            else if(paramType == typeof(long))
                            {
                                convertedParams[i] = long.Parse(rawArg);
                            }
                            else if(paramType == typeof(decimal))
                            {
                                convertedParams[i] = decimal.Parse(rawArg, System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                convertedParams[i] = JsonUtility.FromJson(rawArg, paramType);
                            }
                        }
                        catch(Exception ex)
                        {
                            throw new ArgumentException($"Failed to convert parameter {i} ('{rawArg}') to type '{paramType.FullName}': {ex.Message}", ex);
                        }
                    }
                }

                object result = method.Invoke(null, convertedParams);
                success = true;

                if(method.ReturnType != typeof(void))
                {
                    if(result == null)
                    {
                        payload = "null";
                    }
                    else if(result is bool boolVal)
                    {
                        payload = boolVal ? "true" : "false";
                    }
                    else if(result.GetType().IsPrimitive || result is string || result is decimal)
                    {
                        payload = result.ToString();
                    }
                    else
                    {
                        payload = JsonUtility.ToJson(result);
                    }
                }
            }
            catch(TargetInvocationException tie)
            {
                errorMsg = tie.InnerException != null ? tie.InnerException.ToString() : tie.ToString();
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
            }
            catch(Exception ex)
            {
                errorMsg = ex.ToString();
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
            }
            finally
            {
                stopwatch.Stop();
                if(File.Exists(runningPath))
                {
                    try
                    { File.Delete(runningPath); }
                    catch { }
                }

                try
                {
                    var runResult = new UnityExecuteResult
                    {
                        success = success,
                        message = errorMsg,
                        duration = stopwatch.Elapsed.TotalSeconds,
                        payload = payload
                    };
                    string json = JsonUtility.ToJson(runResult, true);
                    File.WriteAllText(resultsPath, json);
                }
                catch(Exception ex)
                {
                    Debug.LogError($"UnityCliRunner: Failed to write execute result: {ex}");
                }
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

                var targetType = FindType(typeName);
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

                MethodInfo targetMethod = null;
                try
                {
                    targetMethod = FindStaticMethod(targetType, methodName, methodParamsList.Count);
                }
                catch(AmbiguousMatchException ex)
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

                WriteExecuteRunningState();

                ExecuteMethod(targetMethod, methodParamsList.ToArray());

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

        private static Type FindType(string fullName)
        {
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if(type != null)
                        return type;
                }
                catch { }
            }
            return null;
        }
    }

    [Serializable]
    public class FailedTestInfo
    {
        public string name;
        public string fullName;
        public string message;
        public string stackTrace;
        public double duration;
    }

    [Serializable]
    public class UnityTestRunResult
    {
        public bool success;
        public int failCount;
        public int passCount;
        public int skipCount;
        public string message;
        public string resultState;
        public List<FailedTestInfo> failedTests;
    }

    [Serializable]
    public class UnityExecuteResult
    {
        public bool success;
        public string message;
        public double duration;
        public string payload;
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

                if(File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }
                if(File.Exists(failuresPath))
                {
                    File.Delete(failuresPath);
                }

                if(m_FailedTests.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach(var test in m_FailedTests)
                    {
                        int durationMs = (int) Math.Round(test.duration * 1000);
                        string durationStr = durationMs < 1 ? "< 1 ms" : $"{durationMs} ms";
                        sb.AppendLine($"  \u001b[31mFailed\u001b[0m {test.fullName} [{durationStr}]");
                        sb.AppendLine("  Error Message:");
                        if(!string.IsNullOrEmpty(test.message))
                        {
                            foreach(var line in test.message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
                            {
                                sb.AppendLine($"   {line}");
                            }
                        }
                        sb.AppendLine("  Stack Trace:");
                        if(!string.IsNullOrEmpty(test.stackTrace))
                        {
                            foreach(var line in test.stackTrace.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
                            {
                                sb.AppendLine($"   {line}");
                            }
                        }
                        sb.AppendLine();
                    }

                    File.WriteAllText(failuresPath, sb.ToString(), new UTF8Encoding(false));
                }

                bool didNotMatchAnyTests = UnityCliServer.HasActiveTestFilter && result.FailCount == 0 && result.PassCount == 0 && result.SkipCount == 0;

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

                UnityCliServer.HasActiveTestFilter = false;

                string json = JsonUtility.ToJson(runResult, true);
                File.WriteAllText(resultsPath, json);
                Debug.Log($"UnityCliRunner: Playmode/Editmode tests completed. Success: {runResult.success}, Failed: {runResult.failCount}, Passed: {runResult.passCount}, Skipped: {runResult.skipCount}");
            }
            catch(Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Exception in RunFinished callback: {ex}");
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if(!result.HasChildren && result.TestStatus == TestStatus.Failed)
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

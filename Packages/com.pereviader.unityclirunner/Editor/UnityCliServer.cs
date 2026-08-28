using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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

        private static TcpListener _tcpListener;
        private static Thread _serverThread;
        private static volatile bool _isRunning;
        private static volatile bool _shutdownRequested;
        private static volatile bool _isReloading;
        private static readonly ManualResetEvent s_ShutdownEvent = new ManualResetEvent(false);
        private static readonly ConcurrentDictionary<TcpClient, byte> s_ActiveClients = new ConcurrentDictionary<TcpClient, byte>();

        public static bool IsRunning => _isRunning;

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
            { "POLL_EXECUTE", new PollExecuteHandler() },
            { "EVAL", new EvalHandler() },
            { "POLL_EVAL", new PollEvalHandler() }
        };

        static UnityCliServer()
        {
            if(CommandHelper.IsAssetImportWorkerProcess())
            {
                return;
            }

            RecoverOperationAfterEditorRestart();

            // Register callbacks for tests
            RunTestsHandler.RegisterCallbacks();

            // Start server
            StartServer();

            // Stop the listener and all in-flight connections before a domain reload.
            // The callbacks must be registered on every new domain because the old
            // server thread and its client threads may still be unwinding.
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
        }

        private static void RecoverOperationAfterEditorRestart()
        {
            var operation = UnityCliOperationStore.Read();
            if (operation == null || operation.editorSessionId == UnityCliOperationStore.EditorSessionId)
            {
                return;
            }

            const string message = "Unity editor restarted before the operation completed.";
            switch (operation.kind)
            {
                case "test":
                    RunTestsHandler.WriteInterruptedResult(message);
                    break;
                case "execute":
                    ExecuteMethodHandler.MarkInterrupted(message);
                    break;
                case "eval":
                    EvalHandler.MarkInterrupted(message);
                    break;
                case "refresh":
                case "recompile":
                    UnityCliCompilationTracker.WriteInterruptedRefreshResult(operation.operationId, message);
                    break;
                default:
                    UnityCliOperationStore.Complete(operation.operationId);
                    break;
            }
        }

        private static void StartServer()
        {
            if(_isRunning)
                return;

            _isRunning = true;
            _shutdownRequested = false;
            _isReloading = false;
            s_ShutdownEvent.Reset();
            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "UnityCliServerThread"
            };
            _serverThread.Start();
        }

        private static void OnBeforeAssemblyReload()
        {
            _isReloading = true;
            var operation = UnityCliOperationStore.Read();
            if (operation != null)
            {
                UnityCliOperationStore.Update(operation.operationId, "Reloading");
            }
            UnityCliCompilationTracker.WriteActiveErrorsToFile();
            RunTestsHandler.MarkTransportInterruption("Reloading");
            ExecuteMethodHandler.MarkInterrupted("Command interrupted by Unity recompilation outside the Unity CLI workflow.");
            EvalHandler.MarkInterrupted("Command interrupted by Unity recompilation outside the Unity CLI workflow.");
            StopServer();
        }

        private static void OnEditorQuitting()
        {
            var operation = UnityCliOperationStore.Read();
            if (operation != null)
            {
                UnityCliOperationStore.Update(operation.operationId, "ShuttingDown");
            }
            RunTestsHandler.MarkTransportInterruption("ShuttingDown");
            ExecuteMethodHandler.MarkInterrupted("Command interrupted by Unity editor shutdown.");
            EvalHandler.MarkInterrupted("Command interrupted by Unity editor shutdown.");
            StopServer();
        }

        internal static void StopServer()
        {
            _shutdownRequested = true;
            _isRunning = false;
            s_ShutdownEvent.Set();
            try
            {
                _tcpListener?.Stop();
            }
            catch(Exception) { }

            foreach (var client in s_ActiveClients.Keys)
            {
                try { client.Close(); } catch { }
            }

            if(_serverThread is { IsAlive: true } && !ReferenceEquals(Thread.CurrentThread, _serverThread))
            {
                _serverThread.Join(1000);
            }

            DeletePortFile();

            Debug.Log("UnityCliRunner: Socket server stopped.");
        }

        private static void ServerLoop()
        {
            try
            {
                int stickyPort = ReadPortFile();
                _tcpListener = CreateStartedListener(stickyPort);
                if (!_isRunning)
                {
                    _tcpListener.Stop();
                    _tcpListener = null;
                    return;
                }
                int port = ((IPEndPoint) _tcpListener.LocalEndpoint).Port;

                WritePortFile(port);
                Debug.Log($"UnityCliRunner: Socket server started on 127.0.0.1:{port}");

                while(_isRunning)
                {
                    TcpClient client;
                    try
                    {
                        client = _tcpListener.AcceptTcpClient();
                    }
                    catch(SocketException)
                    {
                        // listener stopped
                        break;
                    }
                    catch(ObjectDisposedException)
                    {
                        break;
                    }

                    ThreadPool.QueueUserWorkItem(state => ProcessClient((TcpClient)state), client);
                }
            }
            catch(ThreadAbortException) when (IsShuttingDown())
            {
                // Unity aborts managed threads while reloading its scripting domain.
                // This is a transport interruption, not a command failure.
            }
            catch(Exception e)
            {
                if(!IsShuttingDown())
                {
                    LogUnexpectedException("server loop", e);
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

        private static void ProcessClient(TcpClient client)
        {
            s_ActiveClients.TryAdd(client, 0);
            if (IsShuttingDown())
            {
                try { client.Close(); } catch { }
                s_ActiveClients.TryRemove(client, out _);
                return;
            }
            try
            {
                client.ReceiveTimeout = 5000;
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.UTF8);
                // We use new UTF8Encoding(false) to disable emitting a UTF-8 Byte Order Mark (BOM).
                // Emitting a BOM (\xEF\xBB\xBF in bytes) is non-standard for sockets and would be prepended
                // to our responses, breaking string comparisons (e.g. [ "$response" = "READY" ]) in the bash script.
                using StreamWriter writer = new(stream, new UTF8Encoding(false));
                writer.AutoFlush = true;

                try
                {
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

                    if (!s_Handlers.TryGetValue(command, out var handler))
                    {
                        writer.WriteLine($"ERROR: Unknown command: {command}");
                        return;
                    }

                    // Only PING is completely independent of Unity APIs and the
                    // durable JSON journal. Poll handlers also use JsonUtility,
                    // so they must run on Unity's main thread.
                    if (handler is PingHandler || handler is PollRefreshHandler)
                    {
                        handler.Handle(payload, writer);
                    }
                    else
                    {
                        using (var finishedEvent = new ManualResetEvent(false))
                        {
                            WaitHandle[] requestWaitHandles = { finishedEvent, s_ShutdownEvent };
                            Exception dispatchException = null;
                            UnityCliDispatcher.Enqueue(() =>
                            {
                                Action executeAction = () =>
                                {
                                    try
                                    {
                                        handler.Handle(payload, writer);
                                    }
                                    catch (Exception ex)
                                    {
                                        dispatchException = ex;
                                    }
                                    finally
                                    {
                                        finishedEvent.Set();
                                    }
                                };

                                bool shouldStopPlaymode = handler is RefreshHandler ||
                                                          handler is RecompileHandler ||
                                                          handler is RunTestsHandler ||
                                                          handler is ExecuteMethodHandler;

                                if (shouldStopPlaymode)
                                {
                                    RunActionAfterStoppingPlaymode(executeAction);
                                }
                                else
                                {
                                    executeAction();
                                }
                            });
                            while (WaitHandle.WaitAny(requestWaitHandles, 100) == WaitHandle.WaitTimeout)
                            {
                                // Keep waiting while Unity is healthy. The
                                // shutdown event wakes this thread immediately
                                // when a reload or editor shutdown begins.
                            }
                            if (s_ShutdownEvent.WaitOne(0) || IsShuttingDown())
                                return;
                            if (dispatchException != null)
                            {
                                throw dispatchException;
                            }
                        }
                    }
                }
                catch(ThreadAbortException) when (IsShuttingDown())
                {
                    // Do not turn Unity's reload/shutdown thread abort into a
                    // protocol-level ERROR response.
                }
                catch (Exception e)
                {
                    if (!IsShuttingDown())
                    {
                        LogUnexpectedException("client request", e);
                        try { writer.WriteLine($"ERROR: {e.Message}"); } catch { }
                    }
                }
            }
            catch(ThreadAbortException) when (IsShuttingDown())
            {
                // See the inner handler: reload aborts are expected transport
                // interruptions and must not be sent to the CLI.
            }
            catch (Exception e)
            {
                if (!IsShuttingDown())
                {
                    LogUnexpectedException("client connection", e);
                }
            }
            finally
            {
                try { client.Close(); } catch { }
                s_ActiveClients.TryRemove(client, out _);
            }
        }

        private static bool IsShuttingDown()
        {
            return _shutdownRequested || _isReloading || !_isRunning;
        }

        private static void LogUnexpectedException(string context, Exception exception)
        {
            Debug.LogError($"UnityCliRunner: Unexpected {context} exception. " +
                           $"Type={exception.GetType().FullName}, " +
                           $"Thread={Thread.CurrentThread.Name ?? "unnamed"}, " +
                           $"Reloading={_isReloading}, StackTrace={exception.StackTrace}");
        }

        private static void RunActionAfterStoppingPlaymode(Action action)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.Log("UnityCliRunner: Stopping PlayMode before executing command...");
                EditorApplication.isPlaying = false;

                EditorApplication.CallbackFunction checkPlaymode = null;
                checkPlaymode = () =>
                {
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        EditorApplication.update -= checkPlaymode;
                        Debug.Log("UnityCliRunner: PlayMode stopped. Executing command...");
                        try
                        {
                            action();
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                };
                EditorApplication.update += checkPlaymode;
            }
            else
            {
                action();
            }
        }

        private static void WritePortFile(int port)
        {
            try
            {
                string tempDir = Path.Combine(CommandHelper.ProjectRoot, "Temp");
                if(!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string portFilePath = Path.Combine(tempDir, PortFileName);
                UnityCliOperationStore.WriteAtomic(portFilePath, port.ToString(), "port");
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityCliRunner: Failed to write port file: {e}");
            }
        }

        internal static void DeletePortFile()
        {
            try
            {
                string portFilePath = Path.Combine(CommandHelper.ProjectRoot, "Temp", PortFileName);
                if(File.Exists(portFilePath))
                {
                    File.Delete(portFilePath);
                }
            }
            catch(Exception e)
            {
                Debug.LogWarning($"UnityCliRunner: Failed to remove port file: {e}");
            }
        }

        private static int ReadPortFile()
        {
            try
            {
                string portFilePath = Path.Combine(CommandHelper.ProjectRoot, "Temp", PortFileName);
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
    }
}

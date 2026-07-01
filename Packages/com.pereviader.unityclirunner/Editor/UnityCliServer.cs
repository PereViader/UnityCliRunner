using System;
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

        private static TcpListener _tcpListener;
        private static Thread _serverThread;
        private static bool _isRunning;

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
            if(CommandHelper.IsAssetImportWorkerProcess())
            {
                return;
            }

            // Register callbacks for tests
            RunTestsHandler.RegisterCallbacks();

            // Start server
            StartServer();

            // Hook domain unload to stop server cleanly
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
        }

        private static void StartServer()
        {
            if(_isRunning)
                return;

            _isRunning = true;
            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "UnityCliServerThread"
            };
            _serverThread.Start();
        }

        private static void StopServer()
        {
            if(!_isRunning)
                return;

            _isRunning = false;
            try
            {
                _tcpListener?.Stop();
            }
            catch(Exception) { }

            if(_serverThread is { IsAlive: true })
            {
                _serverThread.Join(500);
            }

            Debug.Log("UnityCliRunner: Socket server stopped.");
        }

        private static void ServerLoop()
        {
            try
            {
                int stickyPort = ReadPortFile();
                _tcpListener = CreateStartedListener(stickyPort);
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

                    UnityCliDispatcher.Enqueue(() => HandleClient(client));
                }
            }
            catch(Exception e)
            {
                if(_isRunning)
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
            try
            {
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
                
                    handler.Handle(payload, writer);
                }
                catch (Exception e)
                {
                    writer.WriteLine($"ERROR: {e.Message}");
                }
            }
            finally
            {
                client.Dispose();
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
    }
}

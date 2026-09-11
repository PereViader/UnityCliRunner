using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static string TestBusyDetection()
        {
            // Connect to UnityLeanMcpServer TCP socket while this execute method is actively running.
            // All mutating commands (RUN_TESTS, EXECUTE_METHOD, REFRESH, RECOMPILE) should detect
            // that Unity is busy.
            int port = 0;
            string portFile = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_lean_mcp_port.txt");
            if (File.Exists(portFile))
            {
                int.TryParse(File.ReadAllText(portFile).Trim(), out port);
            }

            if (port == 0)
            {
                return "FAIL_NO_PORT";
            }

            string[] commandsToTest = new[]
            {
                "RUN_TESTS busy_test_op playmode",
                "EXECUTE_METHOD busy_test_op Tests.DummyExecuteClass.TestBusyDetection",
                "REFRESH busy_test_op",
                "RECOMPILE busy_test_op"
            };

            foreach (var cmd in commandsToTest)
            {
                try
                {
                    using (var client = new System.Net.Sockets.TcpClient("127.0.0.1", port))
                    {
                        client.ReceiveTimeout = 2000;
                        using (var stream = client.GetStream())
                        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true })
                        {
                            writer.WriteLine(cmd);
                            string response = reader.ReadLine();
                            if (response == null || !response.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                            {
                                return $"FAILED for '{cmd}': expected response starting with BUSY, got: '{response}'";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    return $"FAILED for '{cmd}' with exception: {ex.Message}";
                }
            }

            return "ALL_COMMANDS_BUSY_DETECTED_BEFORE_REFRESH";
        }
    }
}

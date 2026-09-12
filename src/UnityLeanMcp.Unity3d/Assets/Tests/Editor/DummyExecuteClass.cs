using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Tests
{
    public static class DummyExecuteClass
    {
        public static string PollRefreshWhileBusy()
        {
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

            string bgResponse = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var client = new TcpClient("127.0.0.1", port);
                    client.ReceiveTimeout = 2000;
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                    writer.WriteLine("POLL_REFRESH");
                    bgResponse = reader.ReadLine();
                }
                catch (System.Exception ex)
                {
                    bgResponse = "TIMEOUT_OR_ERROR: " + ex.Message;
                }
            });

            thread.Start();

            // Simulate the main thread being blocked / busy with work for 1 second
            Thread.Sleep(1000);

            thread.Join(2000);

            if (bgResponse == null || bgResponse.StartsWith("TIMEOUT_OR_ERROR"))
            {
                return "FAIL: " + bgResponse;
            }

            return "OK:" + bgResponse;
        }

        public static string PollHandlersWhileBusy()
        {
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

            string currentOpId = "";
            string opFile = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_lean_mcp_operation.json");
            if (File.Exists(opFile))
            {
                try
                {
                    string text = File.ReadAllText(opFile);
                    int idx = text.IndexOf("\"operationId\":", System.StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int start = text.IndexOf('"', idx + 14) + 1;
                        int end = text.IndexOf('"', start);
                        if (start > 0 && end > start)
                        {
                            currentOpId = text.Substring(start, end - start);
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(currentOpId))
            {
                string runningFile = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_running.txt");
                if (File.Exists(runningFile))
                {
                    currentOpId = File.ReadAllText(runningFile).Trim();
                }
            }

            string pollExecuteResponse = null;
            string pollExecuteBusyResponse = null;
            string pollEvalResponse = null;
            string pollTestsResponse = null;

            var thread = new Thread(() =>
            {
                try
                {
                    string SendCommand(string cmd)
                    {
                        using var client = new TcpClient("127.0.0.1", port);
                        client.ReceiveTimeout = 2000;
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                        writer.WriteLine(cmd);
                        return reader.ReadLine();
                    }

                    // Test POLL_EXECUTE with current operation -> should return RUNNING
                    string execCmd = !string.IsNullOrEmpty(currentOpId) ? $"POLL_EXECUTE {currentOpId}" : "POLL_EXECUTE dummy-op";
                    pollExecuteResponse = SendCommand(execCmd);

                    // Test POLL_EXECUTE with another operation -> should return BUSY
                    pollExecuteBusyResponse = SendCommand("POLL_EXECUTE other-op");

                    // Test POLL_EVAL -> should return BUSY
                    pollEvalResponse = SendCommand("POLL_EVAL dummy-eval-op");

                    // Test POLL_TESTS -> should return BUSY
                    pollTestsResponse = SendCommand("POLL_TESTS dummy-test-op");
                }
                catch (System.Exception ex)
                {
                    pollExecuteResponse ??= "TIMEOUT_OR_ERROR: " + ex.Message;
                }
            });

            thread.Start();

            // Simulate the main thread being blocked / busy for 3 seconds
            Thread.Sleep(3000);

            thread.Join(3000);

            if (pollExecuteResponse == null || pollExecuteResponse.StartsWith("TIMEOUT_OR_ERROR"))
            {
                return "POLL_EXECUTE_FAILED: " + pollExecuteResponse;
            }

            if (pollExecuteBusyResponse == null || pollExecuteBusyResponse.StartsWith("TIMEOUT_OR_ERROR"))
            {
                return "POLL_EXECUTE_BUSY_FAILED: " + pollExecuteBusyResponse;
            }

            if (pollEvalResponse == null || pollEvalResponse.StartsWith("TIMEOUT_OR_ERROR"))
            {
                return "POLL_EVAL_FAILED: " + pollEvalResponse;
            }

            if (pollTestsResponse == null || pollTestsResponse.StartsWith("TIMEOUT_OR_ERROR"))
            {
                return "POLL_TESTS_FAILED: " + pollTestsResponse;
            }

            return $"OK|EXECUTE:{pollExecuteResponse}|BUSY_EXECUTE:{pollExecuteBusyResponse}|EVAL:{pollEvalResponse}|TESTS:{pollTestsResponse}";
        }

        public static string TestBusyDetection()
        {
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
                    using var client = new System.Net.Sockets.TcpClient("127.0.0.1", port);
                    client.ReceiveTimeout = 2000;
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                    writer.WriteLine(cmd);
                    string response = reader.ReadLine();
                    if (response == null || !response.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"FAILED for '{cmd}': expected response starting with BUSY, got: '{response}'";
                    }
                }
                catch (Exception ex)
                {
                    return $"FAILED for '{cmd}' with exception: {ex.Message}";
                }
            }

            return "ALL_COMMANDS_BUSY_DETECTED_BEFORE_REFRESH";
        }

        public static void FailMethod()
        {
            throw new InvalidOperationException("Intentional execution failure!");
        }

        public static async Task<string> CancellableMethod(CancellationToken ct)
        {
            await Task.Delay(10000, ct);
            return "completed";
        }

        public static async Task<string> CancellableWithArgMethod(string text, CancellationToken ct)
        {
            await Task.Delay(10000, ct);
            return text;
        }

        public static async Task<string> NonCancellableMethod()
        {
            await Task.Delay(2000);
            return "completed";
        }

        public static string QuickCancellable(CancellationToken ct)
        {
            return "quick-cancellable";
        }

        public static string OverloadedMethod(string value)
        {
            return "no-ct: " + value;
        }

        public static string OverloadedMethod(string value, CancellationToken ct)
        {
            return "with-ct: " + value;
        }
    }
}

using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static string PollHandlersWhileBusy()
        {
            int port = 0;
            string portFile = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_cli_port.txt");
            if (File.Exists(portFile))
            {
                int.TryParse(File.ReadAllText(portFile).Trim(), out port);
            }

            if (port == 0)
            {
                return "FAIL_NO_PORT";
            }

            string currentOpId = "";
            string runningFile = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_running.txt");
            if (File.Exists(runningFile))
            {
                currentOpId = File.ReadAllText(runningFile).Trim();
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
    }
}

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
        public static string PollRefreshWhileBusy()
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

            // Simulate the main thread being blocked / busy for 3 seconds

            Thread.Sleep(3000);

            thread.Join(3000);

            return bgResponse ?? "NO_RESPONSE";
        }
    }
}

using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal class PollExecuteHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string executeRunningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_running.txt");
            string executeResultPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_result.json");

            if (File.Exists(executeRunningPath))
            {
                writer.WriteLine("RUNNING");
            }
            else if (File.Exists(executeResultPath))
            {
                try
                {
                    string content = File.ReadAllText(executeResultPath);
                    var res = JsonUtility.FromJson<UnityExecuteResult>(content);
                    if (res.success)
                    {
                        if (!string.IsNullOrEmpty(res.payload))
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
                catch (Exception ex)
                {
                    writer.WriteLine($"ERROR: {ex.Message}");
                }
            }
            else
            {
                writer.WriteLine("IDLE");
            }
        }
    }
}

using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal class PollEvalHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string evalRunningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_eval_running.txt");
            string evalResultPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_eval_result.json");

            if (File.Exists(evalRunningPath))
            {
                writer.WriteLine("RUNNING");
            }
            else if (File.Exists(evalResultPath))
            {
                try
                {
                    string content = File.ReadAllText(evalResultPath);
                    var res = JsonUtility.FromJson<UnityEvalResult>(content);
                    if (res.success)
                    {
                        if (!string.IsNullOrEmpty(res.payload))
                        {
                            writer.WriteLine($"SUCCESS\n{res.payload}");
                        }
                        else
                        {
                            writer.WriteLine("SUCCESS");
                        }
                    }
                    else
                    {
                        writer.WriteLine($"FAILURE\n{res.message}");
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

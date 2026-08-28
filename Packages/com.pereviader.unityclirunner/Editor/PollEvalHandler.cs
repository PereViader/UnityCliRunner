using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal class PollEvalHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            string evalRunningPath = Path.Combine(CommandHelper.ProjectRoot, "Temp", "unity_eval_running.txt");
            string evalResultPath = Path.Combine(CommandHelper.ProjectRoot, "Temp", "unity_eval_result.json");

            if (File.Exists(evalRunningPath))
            {
                string runningOperationId = File.ReadAllText(evalRunningPath).Trim();
                writer.WriteLine(string.IsNullOrEmpty(operationId) || runningOperationId == operationId ? "RUNNING" : "IDLE");
            }
            else if (File.Exists(evalResultPath))
            {
                try
                {
                    string content = File.ReadAllText(evalResultPath);
                    var res = JsonUtility.FromJson<UnityEvalResult>(content);
                    if (!string.IsNullOrEmpty(operationId) && res.operationId != operationId)
                    {
                        writer.WriteLine("IDLE");
                        return;
                    }
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
                    else if (res.interrupted)
                    {
                        writer.WriteLine($"INTERRUPTION\n{res.message}");
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
                var operation = UnityCliOperationStore.Read();
                if (operation != null && operation.operationId != operationId)
                {
                    writer.WriteLine($"BUSY {operation.kind} {operation.operationId}");
                }
                else
                {
                    writer.WriteLine("IDLE");
                }
            }
        }
    }
}

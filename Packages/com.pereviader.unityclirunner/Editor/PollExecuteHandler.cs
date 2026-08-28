using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal class PollExecuteHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            string executeRunningPath = Path.Combine(CommandHelper.ProjectRoot, "Temp", "unity_execute_running.txt");
            string executeResultPath = Path.Combine(CommandHelper.ProjectRoot, "Temp", "unity_execute_result.json");

            if (File.Exists(executeRunningPath))
            {
                string runningOperationId = File.ReadAllText(executeRunningPath).Trim();
                writer.WriteLine(string.IsNullOrEmpty(operationId) || runningOperationId == operationId ? "RUNNING" : "IDLE");
            }
            else if (File.Exists(executeResultPath))
            {
                try
                {
                    string content = File.ReadAllText(executeResultPath);
                    var res = JsonUtility.FromJson<UnityExecuteResult>(content);
                    if (!string.IsNullOrEmpty(operationId) && res.operationId != operationId)
                    {
                        writer.WriteLine("IDLE");
                        return;
                    }
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
                    else if (res.interrupted)
                    {
                        writer.WriteLine($"INTERRUPTION {res.message}");
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

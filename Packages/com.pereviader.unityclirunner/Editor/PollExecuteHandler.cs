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

            // 1. Matching terminal results are authoritative
            if (File.Exists(executeResultPath))
            {
                try
                {
                    string content = File.ReadAllText(executeResultPath);
                    var res = JsonUtility.FromJson<UnityExecuteResult>(content);
                    if (res != null && (string.IsNullOrEmpty(operationId) || res.operationId == operationId))
                    {
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
                        return;
                    }
                }
                catch (Exception ex)
                {
                    writer.WriteLine($"ERROR: {ex.Message}");
                    return;
                }
            }

            // 2. Active running state for this operation
            if (File.Exists(executeRunningPath))
            {
                string runningOperationId = File.ReadAllText(executeRunningPath).Trim();
                if (string.IsNullOrEmpty(operationId) || runningOperationId == operationId)
                {
                    writer.WriteLine("RUNNING");
                    return;
                }
            }

            // 3. Fallback to operation store for busy vs idle state
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

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

            // 1. Matching terminal results are authoritative
            if (File.Exists(evalResultPath))
            {
                try
                {
                    string content = File.ReadAllText(evalResultPath);
                    var res = JsonUtility.FromJson<UnityEvalResult>(content);
                    if (res != null && (string.IsNullOrEmpty(operationId) || res.operationId == operationId))
                    {
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
            if (File.Exists(evalRunningPath))
            {
                string runningOperationId = File.ReadAllText(evalRunningPath).Trim();
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

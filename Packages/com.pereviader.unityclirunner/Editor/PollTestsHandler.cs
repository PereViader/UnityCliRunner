using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal class PollTestsHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            // 1. Matching terminal results are authoritative.
            if (File.Exists(RunTestsHandler.ResultsFilePath))
            {
                try
                {
                    string content = File.ReadAllText(RunTestsHandler.ResultsFilePath);
                    var res = JsonUtility.FromJson<UnityTestRunResult>(content);
                    if (res != null && (string.IsNullOrEmpty(operationId) || res.runId == operationId))
                    {
                        string skipStr = res.skipCount > 0 ? $", {res.skipCount} skipped" : "";
                        if (res.success)
                        {
                            writer.WriteLine($"SUCCESS {res.passCount} passed{skipStr}");
                        }
                        else if (res.resultState == "Interrupted")
                        {
                            writer.WriteLine($"INTERRUPTION {res.message}");
                        }
                        else if (!string.IsNullOrEmpty(res.message))
                        {
                            writer.WriteLine($"FAILURE {res.message}");
                        }
                        else
                        {
                            writer.WriteLine($"FAILURE {res.failCount} failed, {res.passCount} passed{skipStr}");
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
            if (File.Exists(RunTestsHandler.RunningFilePath))
            {
                var running = RunTestsHandler.ReadRunningState();
                if (running != null && (string.IsNullOrEmpty(operationId) || running.runId == operationId))
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

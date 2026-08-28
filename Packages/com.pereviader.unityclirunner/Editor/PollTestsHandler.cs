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
            // Final results are authoritative even during the short interval
            // before the running marker is removed.
            if (File.Exists(RunTestsHandler.ResultsFilePath))
            {
                try
                {
                    string content = File.ReadAllText(RunTestsHandler.ResultsFilePath);
                    var res = JsonUtility.FromJson<UnityTestRunResult>(content);
                    if (!string.IsNullOrEmpty(operationId) && res.runId != operationId)
                    {
                        writer.WriteLine("IDLE");
                        return;
                    }
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
                }
                catch (Exception ex)
                {
                    writer.WriteLine($"ERROR: {ex.Message}");
                }
            }
            else if (File.Exists(RunTestsHandler.RunningFilePath))
            {
                var running = RunTestsHandler.ReadRunningState();
                writer.WriteLine(running != null && (string.IsNullOrEmpty(operationId) || running.runId == operationId) ? "RUNNING" : "IDLE");
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

using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal class PollTestsHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            RunTestsHandler.CheckAndHandleInactiveRun();

            if (File.Exists(RunTestsHandler.RunningFilePath))
            {
                writer.WriteLine("RUNNING");
            }
            else if (File.Exists(RunTestsHandler.ResultsFilePath))
            {
                try
                {
                    string content = File.ReadAllText(RunTestsHandler.ResultsFilePath);
                    var res = JsonUtility.FromJson<UnityTestRunResult>(content);
                    string skipStr = res.skipCount > 0 ? $", {res.skipCount} skipped" : "";
                    if (res.success)
                    {
                        writer.WriteLine($"SUCCESS {res.passCount} passed{skipStr}");
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
            else
            {
                writer.WriteLine("IDLE");
            }
        }
    }
}

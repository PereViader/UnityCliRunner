using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static string TestBusyDetection()
        {
            // Run MCP CLI commands while this execute method is actively running.
            // All commands (test, executemethod, refresh, recompile) should detect that
            // Unity is busy.
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dllPath = Path.Combine(projectRoot, "Packages", "com.pereviader.unityclirunner", "MCP~", "UnityCliRunner.Mcp.dll");
            string[] commandsToTest = new[]
            {
                "test --playmode",
                "executemethod Tests.DummyExecuteClass.TestBusyDetection",
                "refresh",
                "recompile"
            };

            foreach (var cmd in commandsToTest)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{dllPath}\" test-cli {cmd}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                int exitCode = proc.ExitCode;
                bool reportedBusy = stderr.Contains("Unity is busy") || stdout.Contains("Unity is busy");

                if (exitCode != 1 || !reportedBusy)
                {
                    return $"FAILED for '{cmd}': exitCode={exitCode}, reportedBusy={reportedBusy}, stdout={stdout}, stderr={stderr}";
                }
            }

            return "ALL_COMMANDS_BUSY_DETECTED_BEFORE_REFRESH";
        }
    }
}

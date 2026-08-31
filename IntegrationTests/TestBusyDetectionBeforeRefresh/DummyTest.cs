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
        private static string FindBashExecutable()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string[] candidates = new[]
                {
                    @"C:\Program Files\Git\bin\bash.exe",
                    @"C:\Program Files\Git\usr\bin\bash.exe",
                    @"C:\Program Files (x86)\Git\bin\bash.exe",
                    @"C:\Program Files (x86)\Git\usr\bin\bash.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\bin\bash.exe")
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            return "bash";
        }

        public static string TestBusyDetection()
        {
            // Run CLI commands via bash while this execute method is actively running.
            // All commands (test, executemethod, refresh, recompile) should detect that
            // Unity is busy BEFORE attempting an AssetDatabase refresh or recompilation.
            string bashExe = FindBashExecutable();
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
                    FileName = bashExe,
                    Arguments = $"./unitycli.sh {cmd}",
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
                bool attemptedRefresh = stdout.Contains("Triggering AssetDatabase refresh") || stderr.Contains("Triggering AssetDatabase refresh")
                                     || stdout.Contains("Triggering force recompilation") || stderr.Contains("Triggering force recompilation");
                bool reportedBusy = stderr.Contains("Error: Unity is busy: BUSY execute") || stdout.Contains("Error: Unity is busy: BUSY execute");

                if (exitCode != 1 || attemptedRefresh || !reportedBusy)
                {
                    return $"FAILED for '{cmd}': exitCode={exitCode}, attemptedRefresh={attemptedRefresh}, reportedBusy={reportedBusy}, stdout={stdout}, stderr={stderr}";
                }
            }

            return "ALL_COMMANDS_BUSY_DETECTED_BEFORE_REFRESH";
        }
    }
}

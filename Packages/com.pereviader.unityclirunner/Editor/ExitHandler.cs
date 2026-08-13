using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class ExitHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            writer.WriteLine("EXITING");
            writer.Flush();
            Debug.Log("UnityCliRunner: Shutdown requested via socket.");

            string testRunningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_test_running.txt");
            string executeRunningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_execute_running.txt");

            if (File.Exists(testRunningPath) || File.Exists(executeRunningPath))
            {
                Debug.Log("UnityCliRunner: Operations are currently running. Waiting for completion before exiting...");
                EditorApplication.CallbackFunction waitForOperations = null;
                waitForOperations = () =>
                {
                    if (!File.Exists(testRunningPath) && !File.Exists(executeRunningPath))
                    {
                        EditorApplication.update -= waitForOperations;
                        Debug.Log("UnityCliRunner: Operations finished. Exiting now.");
                        EditorApplication.Exit(0);
                    }
                };
                EditorApplication.update += waitForOperations;
            }
            else
            {
                EditorApplication.Exit(0);
            }
        }
    }
}

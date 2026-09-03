using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class ExitHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;

        private static void ExitUnity()
        {
            UnityCliServer.StopServer();
            EditorApplication.Exit(0);
        }

        public void Handle(string payload, StreamWriter writer)
        {
            writer.WriteLine("EXITING");
            writer.Flush();
            Debug.Log("UnityCliRunner: Shutdown requested via socket. Exiting immediately.");
            ExitUnity();
        }
    }
}

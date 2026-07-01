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
            UnityCliServer.EnqueueToMainThread(() =>
            {
                Debug.Log("UnityCliRunner: Shutdown requested via socket.");
                EditorApplication.Exit(0);
            });
        }
    }
}

using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RefreshHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            writer.WriteLine("REFRESHING");

            UnityCliCompilationTracker.RefreshPending = true;
            UnityCliCompilationTracker.CompilationRequested = true;
            try
            {
                Debug.Log("UnityCliRunner: Triggering AssetDatabase.Refresh()");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                UnityCliCompilationTracker.RefreshPending = false;
                UnityCliCompilationTracker.WriteActiveErrorsToFile();
            }
        }
    }
}

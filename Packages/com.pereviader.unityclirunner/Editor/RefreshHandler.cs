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
            try
            {
                Debug.Log("UnityCliRunner: Triggering AssetDatabase.Refresh()");
                UnityCliCompilationTracker.DeleteDiagnosticsFile();
                UnityCliCompilationTracker.ClearActiveEntries();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                UnityCliCompilationTracker.RefreshPending = false;
            }
        }
    }
}

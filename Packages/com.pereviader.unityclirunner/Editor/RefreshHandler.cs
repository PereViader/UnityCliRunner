using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class RefreshHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            UnityCliServer.RefreshPending = true;
            UnityCliServer.EnqueueToMainThread(() =>
            {
                try
                {
                    Debug.Log("UnityCliRunner: Triggering AssetDatabase.Refresh()");
                    UnityCliCompilationTracker.DeleteDiagnosticsFile();
                    UnityCliCompilationTracker.ClearActiveEntries();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                finally
                {
                    UnityCliServer.RefreshPending = false;
                }
            });
            writer.WriteLine("REFRESHING");
        }
    }
}

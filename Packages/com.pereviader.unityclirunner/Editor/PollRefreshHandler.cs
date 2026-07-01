using System;
using System.IO;
using System.Threading;
using UnityEditor;

namespace UnityCliRunner
{
    internal class PollRefreshHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            string response = "UPDATING";
            var evt = new ManualResetEvent(false);
            UnityCliServer.EnqueueToMainThread(() =>
            {
                try
                {
                    response = GetRefreshPollResponse();
                }
                finally
                {
                    evt.Set();
                }
            });

            if (!evt.WaitOne(2000))
            {
                writer.WriteLine("UPDATING");
                return;
            }

            writer.WriteLine(response);
        }

        private string GetRefreshPollResponse()
        {
            // Update compilation state parameters on the main thread
            UnityCliServer.UpdateCompilationState();

            if (UnityCliServer.RefreshPending || UnityCliServer.CompilationRequested)
            {
                return "COMPILING";
            }

            if (UnityCliServer.IsCompiling)
            {
                return "COMPILING";
            }

            if (UnityCliServer.IsUpdating)
            {
                return "UPDATING";
            }

            if (UnityCliServer.ScriptCompilationFailed)
            {
                UnityCliCompilationTracker.WriteActiveErrorsToFile();
                return "COMPILATION_ERROR";
            }

            UnityCliCompilationTracker.WriteActiveErrorsToFile();
            return "READY";
        }
    }
}

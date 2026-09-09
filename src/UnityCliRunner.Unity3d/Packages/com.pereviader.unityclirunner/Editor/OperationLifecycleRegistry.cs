using System;
using System.Collections.Concurrent;
using System.IO;
using UnityEditor;

namespace UnityCliRunner
{
    [InitializeOnLoad]
    internal static class OperationLifecycleRegistry
    {
        private static readonly ConcurrentDictionary<string, IOperationLifecycleHandler> s_Handlers =
            new ConcurrentDictionary<string, IOperationLifecycleHandler>(StringComparer.OrdinalIgnoreCase);

        private static bool s_Initialized;
        private static readonly object s_InitLock = new object();

        static OperationLifecycleRegistry()
        {
            EnsureInitialized();
        }

        public static void EnsureInitialized()
        {
            if (s_Initialized) return;
            lock (s_InitLock)
            {
                if (s_Initialized) return;
                s_Initialized = true;
                InitializeDefaultHandlers();
            }
        }

        private static void InitializeDefaultHandlers()
        {
            Register(new TestLifecycleHandler());
            Register(new ExecuteLifecycleHandler());
            Register(new EvalLifecycleHandler());
            Register(new RefreshLifecycleHandler());
            Register(new RecompileLifecycleHandler());
        }

        public static void Register(IOperationLifecycleHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.OperationKind))
            {
                return;
            }

            s_Handlers[handler.OperationKind] = handler;
        }

        public static bool TryGetHandler(string kind, out IOperationLifecycleHandler handler)
        {
            if (string.IsNullOrEmpty(kind))
            {
                handler = null;
                return false;
            }

            EnsureInitialized();
            return s_Handlers.TryGetValue(kind, out handler);
        }

        public static void Cancel(UnityCliOperationState operation, StreamWriter writer)
        {
            if (operation != null && TryGetHandler(operation.kind, out var handler))
            {
                handler.TryCancel(operation.operationId, writer);
            }
            else
            {
                writer?.WriteLine("NOT_CANCELABLE");
            }

            writer?.Flush();
        }

        public static void RecoverOnDomainLoad(UnityCliOperationState operation, bool isRestart)
        {
            if (operation == null)
            {
                return;
            }

            if (isRestart)
            {
                const string message = "Unity editor restarted before the operation completed.";
                if (TryGetHandler(operation.kind, out var handler))
                {
                    handler.OnEditorRestarted(operation.operationId, message);
                }
                else
                {
                    UnityCliOperationStore.Complete(operation.operationId);
                }
            }
            else
            {
                const string message = "Operation was interrupted by Unity domain reload.";
                if (TryGetHandler(operation.kind, out var handler))
                {
                    handler.OnDomainReloaded(operation.operationId, message);
                }
            }
        }

        public static void NotifyQuitting(UnityCliOperationState operation)
        {
            if (operation == null)
            {
                return;
            }

            UnityCliOperationStore.Update(operation.operationId, OperationStatus.ShuttingDown);

            if (TryGetHandler(operation.kind, out var handler))
            {
                handler.OnEditorQuitting(operation.operationId);
            }
        }
    }

    internal class TestLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind => OperationKinds.Test;

        public bool TryCancel(string operationId, StreamWriter writer)
        {
            RunTestsHandler.CancelActiveTestRun(operationId, writer);
            return true;
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            RunTestsHandler.WriteInterruptedResult(message, operationId);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            // Test runner api callbacks survive domain reload and are bound during server initialization.
        }

        public void OnEditorQuitting(string operationId)
        {
            RunTestsHandler.MarkTransportInterruption(OperationStatus.ShuttingDown);
        }
    }

    internal class ExecuteLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind => OperationKinds.Execute;

        public bool TryCancel(string operationId, StreamWriter writer)
        {
            bool cancelled = ExecuteMethodHandler.CancelActiveExecute(operationId);
            if (cancelled)
            {
                writer?.WriteLine("CANCELLED");
            }
            else
            {
                writer?.WriteLine("NOT_CANCELABLE");
            }
            return cancelled;
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            ExecuteMethodHandler.MarkInterrupted(message, operationId);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            ExecuteMethodHandler.MarkInterrupted(message, operationId);
        }

        public void OnEditorQuitting(string operationId)
        {
            ExecuteMethodHandler.MarkInterrupted("Command interrupted by Unity editor shutdown.", operationId);
        }
    }

    internal class EvalLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind => OperationKinds.Eval;

        public bool TryCancel(string operationId, StreamWriter writer)
        {
            bool cancelled = EvalHandler.CancelActiveEval(operationId);
            if (cancelled)
            {
                writer?.WriteLine("CANCELLED");
            }
            else
            {
                writer?.WriteLine("NOT_CANCELABLE");
            }
            return cancelled;
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            EvalHandler.MarkInterrupted(message, operationId);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            EvalHandler.MarkInterrupted(message, operationId);
        }

        public void OnEditorQuitting(string operationId)
        {
            EvalHandler.MarkInterrupted("Command interrupted by Unity editor shutdown.", operationId);
        }
    }

    internal class RefreshLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind => OperationKinds.Refresh;

        public bool TryCancel(string operationId, StreamWriter writer)
        {
            writer?.WriteLine("NOT_CANCELABLE");
            return false;
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            UnityCliCompilationTracker.WriteInterruptedRefreshResult(operationId, message);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            UnityCliCompilationTracker.ObserveOperationUntilSettled();
        }

        public void OnEditorQuitting(string operationId)
        {
        }
    }

    internal class RecompileLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind => OperationKinds.Recompile;

        public bool TryCancel(string operationId, StreamWriter writer)
        {
            writer?.WriteLine("NOT_CANCELABLE");
            return false;
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            UnityCliCompilationTracker.WriteInterruptedRefreshResult(operationId, message);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            UnityCliCompilationTracker.ObserveOperationUntilSettled();
        }

        public void OnEditorQuitting(string operationId)
        {
        }
    }
}

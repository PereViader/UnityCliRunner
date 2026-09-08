using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class ExecuteMethodHandler : ICommandHandler
    {
        private static ConsoleLogCapture s_ActiveLogCapture;
        private static readonly object s_CtsLock = new object();
        private static CancellationTokenSource s_ActiveCts;
        private static string s_ActiveOperationId;
        private static bool s_ActiveMethodHasCancellationToken;

        public static bool CancelActiveExecute(string operationId)
        {
            lock (s_CtsLock)
            {
                if (s_ActiveCts != null && s_ActiveMethodHasCancellationToken && (string.IsNullOrEmpty(operationId) || s_ActiveOperationId == operationId))
                {
                    try
                    {
                        s_ActiveCts.Cancel();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"UnityCliRunner: Failed to cancel active execute method: {ex.Message}");
                    }
                }
            }
            return false;
        }

        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;

        public void Handle(string payload, StreamWriter writer)
        {
            if (UnityCliCompilationTracker.ScriptCompilationFailed)
            {
                writer.WriteLine("FAILURE Compilation failed");
                return;
            }

            if (string.IsNullOrEmpty(payload))
            {
                writer.WriteLine("ERROR: Missing method name");
                return;
            }

            string[] execArgs = CommandHelper.SplitArguments(payload);
            if (execArgs.Length < 2)
            {
                writer.WriteLine("ERROR: Missing operation id or method name");
                return;
            }

            string operationId = execArgs[0];
            string targetMethodName = execArgs[1];
            int lastDot = targetMethodName.LastIndexOf('.');
            if (lastDot == -1)
            {
                writer.WriteLine($"ERROR: Invalid method format: '{targetMethodName}'. Expected FullyQualifiedType.Method");
                return;
            }

            string typeName = targetMethodName.Substring(0, lastDot);
            string methodName = targetMethodName.Substring(lastDot + 1);

            var targetType = CommandHelper.FindType(typeName);
            if (targetType == null)
            {
                writer.WriteLine($"ERROR: Type not found: '{typeName}'");
                return;
            }

            var methodParamsList = new List<string>();
            for (int i = 2; i < execArgs.Length; i++)
            {
                methodParamsList.Add(execArgs[i]);
            }

            MethodInfo targetMethod = null;
            try
            {
                targetMethod = CommandHelper.FindStaticMethod(targetType, methodName, methodParamsList.Count);
            }
            catch (AmbiguousMatchException ex)
            {
                writer.WriteLine($"ERROR: {ex.Message}");
                return;
            }

            if (targetMethod == null)
            {
                writer.WriteLine($"ERROR: Static method '{methodName}' not found in type '{typeName}'");
                return;
            }

            var begin = UnityCliOperationStore.TryBegin(operationId, OperationKinds.Execute, OperationStatus.Executing, out var existing);
            if (begin == BeginOperationResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }
            if (begin == BeginOperationResult.Busy)
            {
                writer.WriteLine($"BUSY {existing.kind} {existing.operationId}");
                return;
            }

            if (begin == BeginOperationResult.AlreadyStarted)
            {
                writer.WriteLine("RUNNING");
                return;
            }

            bool hasCt = false;
            foreach (var p in targetMethod.GetParameters())
            {
                if (p.ParameterType == typeof(CancellationToken))
                {
                    hasCt = true;
                    break;
                }
            }

            CancellationTokenSource cts;
            lock (s_CtsLock)
            {
                s_ActiveCts?.Dispose();
                s_ActiveCts = new CancellationTokenSource();
                s_ActiveOperationId = operationId;
                s_ActiveMethodHasCancellationToken = hasCt;
                cts = s_ActiveCts;
            }

            writer.WriteLine("RUNNING");
            writer.Flush();

            try
            {
                WriteExecuteRunningState(operationId);
                if (cts.IsCancellationRequested)
                {
                    FinishExecute(operationId, false, "Method execution was canceled.", 0, null, new List<ConsoleLogEntry>(), interrupted: true);
                    return;
                }
                ExecuteMethod(operationId, targetMethod, methodParamsList.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Unhandled exception during ExecuteMethod: {ex}");
                FinishExecute(operationId, false, ex.ToString(), 0, null, new List<ConsoleLogEntry>());
            }
        }

        public static void WriteExecuteRunningState(string operationId)
        {
            try
            {
                if (!Directory.Exists(UnityCliPaths.TempDir))
                {
                    Directory.CreateDirectory(UnityCliPaths.TempDir);
                }
                UnityCliOperationStore.WriteAtomic(UnityCliPaths.ExecuteRunningFile, operationId, operationId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write execute running state: {ex}");
            }
        }

        public static void ExecuteMethod(string operationId, MethodInfo method, string[] stringParams)
        {
            UnityCliDispatcher.EnsureInitialized();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var logCapture = new ConsoleLogCapture();
            s_ActiveLogCapture = logCapture;

            object result = null;
            try
            {
                Debug.Log($"UnityCliRunner: Executing method '{method.DeclaringType.FullName}.{method.Name}'...");

                var paramInfos = method.GetParameters();
                int expectedCount = paramInfos.Length;
                int providedCount = stringParams != null ? stringParams.Length : 0;

                bool hasCt = false;
                foreach (var p in paramInfos)
                {
                    if (p.ParameterType == typeof(CancellationToken))
                    {
                        hasCt = true;
                        break;
                    }
                }

                CancellationTokenSource cts;
                lock (s_CtsLock)
                {
                    if (s_ActiveOperationId == operationId && s_ActiveCts != null)
                    {
                        cts = s_ActiveCts;
                    }
                    else
                    {
                        s_ActiveCts?.Dispose();
                        s_ActiveCts = new CancellationTokenSource();
                        s_ActiveOperationId = operationId;
                        s_ActiveMethodHasCancellationToken = hasCt;
                        cts = s_ActiveCts;
                    }
                }

                object[] convertedParams = null;
                if (expectedCount > 0)
                {
                    convertedParams = new object[expectedCount];
                    int stringParamIdx = 0;
                    for (int i = 0; i < expectedCount; i++)
                    {
                        var p = paramInfos[i];
                        if (p.ParameterType == typeof(CancellationToken))
                        {
                            convertedParams[i] = cts.Token;
                        }
                        else
                        {
                            if (stringParamIdx >= providedCount)
                            {
                                throw new ArgumentException($"Parameter count mismatch. Method '{method.DeclaringType.FullName}.{method.Name}' expects {expectedCount} parameters, but {providedCount} were provided.");
                            }
                            string rawArg = stringParams[stringParamIdx++];
                            Type paramType = p.ParameterType;
                            try
                            {
                                convertedParams[i] = CommandHelper.ConvertParameter(rawArg, paramType);
                            }
                            catch (Exception ex)
                            {
                                throw new ArgumentException($"Failed to convert parameter {stringParamIdx - 1} ('{rawArg}') to type '{paramType.FullName}': {ex.Message}", ex);
                            }
                        }
                    }

                    if (stringParamIdx != providedCount)
                    {
                        throw new ArgumentException($"Parameter count mismatch. Method '{method.DeclaringType.FullName}.{method.Name}' expects {stringParamIdx} non-cancellation arguments, but {providedCount} were provided.");
                    }
                }
                else if (providedCount > 0)
                {
                    throw new ArgumentException($"Parameter count mismatch. Method '{method.DeclaringType.FullName}.{method.Name}' expects 0 parameters, but {providedCount} were provided.");
                }

                if (cts.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    var logs = logCapture.GetLogs();
                    DisposeCapture(logCapture);
                    FinishExecute(operationId, false, "Method execution was canceled.", stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: true);
                    return;
                }

                result = method.Invoke(null, convertedParams);
            }
            catch (TargetInvocationException tie)
            {
                stopwatch.Stop();
                var inner = tie.InnerException != null ? tie.InnerException : tie;
                string errorMsg = inner.ToString();
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                if (inner is OperationCanceledException)
                {
                    Debug.LogWarning($"UnityCliRunner: Method execution was canceled: {inner.Message}");
                    FinishExecute(operationId, false, "Method execution was canceled.", stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: true);
                    return;
                }
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
                FinishExecute(operationId, false, errorMsg, stopwatch.Elapsed.TotalSeconds, null, logs);
                return;
            }
            catch (OperationCanceledException oce)
            {
                stopwatch.Stop();
                Debug.LogWarning($"UnityCliRunner: Method execution was canceled: {oce.Message}");
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishExecute(operationId, false, "Method execution was canceled.", stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: true);
                return;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                string errorMsg = ex.ToString();
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishExecute(operationId, false, errorMsg, stopwatch.Elapsed.TotalSeconds, null, logs);
                return;
            }

            // Check if result is an async Task or ValueTask
            try
            {
                UnwrapAndFinish(result, operationId, method.ReturnType == typeof(void), stopwatch, logCapture);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishExecute(operationId, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
            }
        }

        private static void UnwrapAndFinish(object rawResult, string operationId, bool isVoidMethod, System.Diagnostics.Stopwatch stopwatch, ConsoleLogCapture logCapture)
        {
            // Check if rawResult is a ValueTask or ValueTask<T>
            if (rawResult != null)
            {
                Type resultType = rawResult.GetType();
                if (resultType.FullName != null && resultType.FullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal))
                {
                    var asTaskMethod = resultType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
                    if (asTaskMethod != null && asTaskMethod.GetParameters().Length == 0 && typeof(Task).IsAssignableFrom(asTaskMethod.ReturnType))
                    {
                        try
                        {
                            rawResult = asTaskMethod.Invoke(rawResult, null);
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                            var logs = logCapture.GetLogs();
                            DisposeCapture(logCapture);
                            FinishExecute(operationId, false, inner.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                            return;
                        }
                    }
                }
            }

            // Check if rawResult is a Task
            if (rawResult is Task innerTask)
            {
                if (!innerTask.IsCompleted)
                {
                    innerTask.ContinueWith(t => UnityCliDispatcher.Enqueue(() =>
                    {
                        try
                        {
                            UnwrapCompletedTask(t, operationId, stopwatch, logCapture);
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            var logs = logCapture.GetLogs();
                            DisposeCapture(logCapture);
                            FinishExecute(operationId, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                        }
                    }));
                    return;
                }

                try
                {
                    UnwrapCompletedTask(innerTask, operationId, stopwatch, logCapture);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    var logs = logCapture.GetLogs();
                    DisposeCapture(logCapture);
                    FinishExecute(operationId, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                }
                return;
            }

            // Not a task, finish directly
            stopwatch.Stop();
            double duration = stopwatch.Elapsed.TotalSeconds;
            var finalLogs = logCapture.GetLogs();
            DisposeCapture(logCapture);
            string payload = null;
            if (!isVoidMethod)
            {
                try
                {
                    payload = CommandHelper.FormatResult(rawResult, false, false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityCliRunner: Failed to format result: {ex.Message}");
                    payload = rawResult?.ToString();
                }
            }
            FinishExecute(operationId, true, "", duration, payload, finalLogs);
        }

        private static void UnwrapCompletedTask(Task innerTask, string operationId, System.Diagnostics.Stopwatch stopwatch, ConsoleLogCapture logCapture)
        {
            if (innerTask.IsFaulted)
            {
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                var ex = innerTask.Exception != null
                    ? (innerTask.Exception.InnerExceptions.Count == 1 ? innerTask.Exception.InnerExceptions[0] : innerTask.Exception)
                    : new Exception("Unknown task failure");
                if (ex is OperationCanceledException)
                {
                    Debug.LogWarning("UnityCliRunner: Method execution was canceled.");
                    FinishExecute(operationId, false, "Method execution was canceled.", duration, null, logs, interrupted: true);
                    return;
                }
                string errorMsg = ex.ToString();
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
                FinishExecute(operationId, false, errorMsg, duration, null, logs);
                return;
            }

            if (innerTask.IsCanceled)
            {
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                Debug.LogWarning("UnityCliRunner: Method execution was canceled.");
                FinishExecute(operationId, false, "Method execution was canceled.", duration, null, logs, interrupted: true);
                return;
            }

            // Check if generic Task<T>
            Type tType = innerTask.GetType();
            PropertyInfo resultProp = null;
            Type cur = tType;
            while (cur != null && cur != typeof(object))
            {
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    resultProp = cur.GetProperty("Result");
                    break;
                }
                cur = cur.BaseType;
            }

            if (resultProp != null)
            {
                object innerVal = resultProp.GetValue(innerTask);
                UnwrapAndFinish(innerVal, operationId, false, stopwatch, logCapture);
            }
            else
            {
                // Non-generic Task (void completion)
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishExecute(operationId, true, "", duration, null, logs);
            }
        }

        private static void DisposeCapture(ConsoleLogCapture logCapture)
        {
            if (logCapture == null) return;
            if (s_ActiveLogCapture == logCapture)
            {
                s_ActiveLogCapture = null;
            }
            logCapture.Dispose();
        }

        private static void FinishExecute(string operationId, bool success, string message, double duration, string payload, List<ConsoleLogEntry> logs, bool interrupted = false)
        {
            lock (s_CtsLock)
            {
                if (s_ActiveOperationId == operationId)
                {
                    s_ActiveCts?.Dispose();
                    s_ActiveCts = null;
                    s_ActiveOperationId = null;
                    s_ActiveMethodHasCancellationToken = false;
                }
            }

            if (!UnityCliOperationStore.IsOwnedBy(operationId, OperationKinds.Execute))
            {
                return;
            }
            try
            {
                string resultsPath = UnityCliPaths.ExecuteResultFile;
                var runResult = new UnityExecuteResult
                {
                    operationId = operationId,
                    success = success,
                    interrupted = interrupted,
                    message = message,
                    duration = duration,
                    payload = payload,
                    logs = logs
                };
                string json = JsonUtility.ToJson(runResult, true);
                UnityCliOperationStore.WriteAtomic(resultsPath, json, operationId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write execute result: {ex}");
            }
            finally
            {
                if (File.Exists(UnityCliPaths.ExecuteRunningFile))
                {
                    try { File.Delete(UnityCliPaths.ExecuteRunningFile); } catch { }
                }
                UnityCliOperationStore.Complete(operationId);
            }
        }

        public static void MarkInterrupted(string message, string targetOperationId = null)
        {
            lock (s_CtsLock)
            {
                if (string.IsNullOrEmpty(targetOperationId) || s_ActiveOperationId == targetOperationId)
                {
                    s_ActiveCts?.Dispose();
                    s_ActiveCts = null;
                    s_ActiveOperationId = null;
                    s_ActiveMethodHasCancellationToken = false;
                }
            }

            if (s_ActiveLogCapture != null)
            {
                s_ActiveLogCapture.Dispose();
                s_ActiveLogCapture = null;
            }

            string runningPath = UnityCliPaths.ExecuteRunningFile;
            string resultsPath = UnityCliPaths.ExecuteResultFile;
            var operation = UnityCliOperationStore.Read();
            string opId = targetOperationId ?? operation?.operationId;
            if (string.IsNullOrEmpty(opId))
            {
                return;
            }
            if (operation != null && operation.kind != OperationKinds.Execute && targetOperationId == null)
            {
                return;
            }

            try
            {
                var result = new UnityExecuteResult
                {
                    operationId = opId,
                    success = false,
                    interrupted = true,
                    message = message,
                    duration = 0,
                    payload = null
                };
                UnityCliOperationStore.WriteAtomic(resultsPath, JsonUtility.ToJson(result, true), opId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to persist interrupted method result: {ex}");
            }
            finally
            {
                if (File.Exists(runningPath))
                {
                    try { File.Delete(runningPath); } catch { }
                }
                UnityCliOperationStore.Complete(opId);
            }
        }
    }
}

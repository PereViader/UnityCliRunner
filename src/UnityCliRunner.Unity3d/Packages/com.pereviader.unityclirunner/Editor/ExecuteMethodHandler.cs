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

            writer.WriteLine("RUNNING");
            writer.Flush();

            if (begin == BeginOperationResult.AlreadyStarted)
            {
                return;
            }

            WriteExecuteRunningState(operationId);
            ExecuteMethod(operationId, targetMethod, methodParamsList.ToArray());
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
                if (expectedCount != providedCount)
                {
                    throw new ArgumentException($"Parameter count mismatch. Method '{method.DeclaringType.FullName}.{method.Name}' expects {expectedCount} parameters, but {providedCount} were provided.");
                }

                object[] convertedParams = null;
                if (expectedCount > 0)
                {
                    convertedParams = new object[expectedCount];
                    for (int i = 0; i < expectedCount; i++)
                    {
                        string rawArg = stringParams[i];
                        Type paramType = paramInfos[i].ParameterType;
                        try
                        {
                            convertedParams[i] = CommandHelper.ConvertParameter(rawArg, paramType);
                        }
                        catch (Exception ex)
                        {
                            throw new ArgumentException($"Failed to convert parameter {i} ('{rawArg}') to type '{paramType.FullName}': {ex.Message}", ex);
                        }
                    }
                }

                result = method.Invoke(null, convertedParams);
            }
            catch (TargetInvocationException tie)
            {
                stopwatch.Stop();
                var inner = tie.InnerException != null ? tie.InnerException : tie;
                string errorMsg = inner.ToString();
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishExecute(operationId, false, errorMsg, stopwatch.Elapsed.TotalSeconds, null, logs);
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
            UnwrapAndFinish(result, operationId, method.ReturnType == typeof(void), stopwatch, logCapture);
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
                        UnwrapCompletedTask(t, operationId, stopwatch, logCapture);
                    }));
                    return;
                }

                UnwrapCompletedTask(innerTask, operationId, stopwatch, logCapture);
                return;
            }

            // Not a task, finish directly
            stopwatch.Stop();
            double duration = stopwatch.Elapsed.TotalSeconds;
            var finalLogs = logCapture.GetLogs();
            DisposeCapture(logCapture);
            string payload = !isVoidMethod ? CommandHelper.FormatResult(rawResult, false, false) : null;
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
                Debug.LogError("UnityCliRunner: Method execution was canceled.");
                FinishExecute(operationId, false, "Method execution was canceled.", duration, null, logs);
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

        private static void FinishExecute(string operationId, bool success, string message, double duration, string payload, List<ConsoleLogEntry> logs)
        {
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
                    message = message,
                    duration = duration,
                    payload = payload,
                    logs = logs
                };
                string json = JsonUtility.ToJson(runResult, true);
                UnityCliOperationStore.WriteAtomic(resultsPath, json, operationId);
                if (File.Exists(UnityCliPaths.ExecuteRunningFile)) File.Delete(UnityCliPaths.ExecuteRunningFile);
                UnityCliOperationStore.Complete(operationId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write execute result: {ex}");
            }
        }

        public static void MarkInterrupted(string message)
        {
            if (s_ActiveLogCapture != null)
            {
                s_ActiveLogCapture.Dispose();
                s_ActiveLogCapture = null;
            }

            string runningPath = UnityCliPaths.ExecuteRunningFile;
            string resultsPath = UnityCliPaths.ExecuteResultFile;
            var operation = UnityCliOperationStore.Read();
            if (operation == null || operation.kind != OperationKinds.Execute)
            {
                return;
            }

            try
            {
                var result = new UnityExecuteResult
                {
                    operationId = operation.operationId,
                    success = false,
                    interrupted = true,
                    message = message,
                    duration = 0,
                    payload = null
                };
                UnityCliOperationStore.WriteAtomic(resultsPath, JsonUtility.ToJson(result, true), operation.operationId);
                if (File.Exists(runningPath)) File.Delete(runningPath);
                UnityCliOperationStore.Complete(operation.operationId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to persist interrupted method result: {ex}");
            }
        }
    }
}

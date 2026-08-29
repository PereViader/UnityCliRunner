using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class ExecuteMethodHandler : ICommandHandler
    {
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
            string runningPath = UnityCliPaths.ExecuteRunningFile;
            string resultsPath = UnityCliPaths.ExecuteResultFile;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool success = false;
            string errorMsg = "";
            string payload = null;

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

                object result = method.Invoke(null, convertedParams);
                success = true;

                if (method.ReturnType != typeof(void))
                {
                    payload = CommandHelper.FormatResult(result, false, false);
                }
            }
            catch (TargetInvocationException tie)
            {
                errorMsg = tie.InnerException != null ? tie.InnerException.ToString() : tie.ToString();
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
            }
            catch (Exception ex)
            {
                errorMsg = ex.ToString();
                Debug.LogError($"UnityCliRunner: Method execution failed: {errorMsg}");
            }
            finally
            {
                stopwatch.Stop();
                if (UnityCliOperationStore.IsOwnedBy(operationId, OperationKinds.Execute))
                {
                    try
                    {
                        var runResult = new UnityExecuteResult
                        {
                            operationId = operationId,
                            success = success,
                            message = errorMsg,
                            duration = stopwatch.Elapsed.TotalSeconds,
                            payload = payload
                        };
                        string json = JsonUtility.ToJson(runResult, true);
                        UnityCliOperationStore.WriteAtomic(resultsPath, json, operationId);
                        if (File.Exists(runningPath)) File.Delete(runningPath);
                        UnityCliOperationStore.Complete(operationId);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"UnityCliRunner: Failed to write execute result: {ex}");
                    }
                }
            }
        }

        public static void MarkInterrupted(string message)
        {
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

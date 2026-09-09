using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityCliRunner
{
    internal class ExecuteMethodHandler : ICommandHandler
    {
        public static bool CancelActiveExecute(string operationId)
        {
            return OperationExecutionEngine.TryCancel(operationId);
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
                targetMethod = CommandHelper.FindStaticMethod(targetType, methodName, methodParamsList.Count, allowTrailingCancellationToken: true);
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

            var cts = OperationExecutionEngine.RegisterActiveOperation(operationId, isCancelable: hasCt);

            writer.WriteLine("RUNNING");
            writer.Flush();

            try
            {
                if (cts.IsCancellationRequested)
                {
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Execute, UnityCliPaths.ExecuteResultFile, false, "Method execution was canceled.", 0, null, new List<ConsoleLogEntry>(), interrupted: true);
                    return;
                }
                ExecuteMethod(operationId, targetMethod, methodParamsList.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Unhandled exception during ExecuteMethod: {ex}");
                OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Execute, UnityCliPaths.ExecuteResultFile, false, ex.ToString(), 0, null, new List<ConsoleLogEntry>());
            }
        }

        [Obsolete("Running state is now tracked exclusively in UnityCliOperationStore.")]
        public static void WriteExecuteRunningState(string operationId)
        {
        }

        public static void ExecuteMethod(string operationId, MethodInfo method, string[] stringParams)
        {
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

            Debug.Log($"UnityCliRunner: Executing method '{method.DeclaringType.FullName}.{method.Name}'...");

            OperationExecutionEngine.Execute(
                operationId: operationId,
                operationKind: OperationKinds.Execute,
                resultFilePath: UnityCliPaths.ExecuteResultFile,
                isVoid: method.ReturnType == typeof(void) || method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask),
                canCancel: hasCt,
                invoker: ct =>
                {
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
                                convertedParams[i] = ct;
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

                    return method.Invoke(null, convertedParams);
                },
                onInvocationException: ex =>
                {
                    Debug.LogError($"UnityCliRunner: Method execution failed: {ex}");
                }
            );
        }

        public static void MarkInterrupted(string message, string targetOperationId = null)
        {
            OperationExecutionEngine.MarkInterrupted(OperationKinds.Execute, UnityCliPaths.ExecuteResultFile, message, targetOperationId);
        }
    }
}

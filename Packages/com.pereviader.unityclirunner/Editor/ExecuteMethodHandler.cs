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
            if (execArgs.Length == 0)
            {
                writer.WriteLine("ERROR: Missing method name");
                return;
            }

            string targetMethodName = execArgs[0];
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
            for (int i = 1; i < execArgs.Length; i++)
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

            WriteExecuteRunningState();

            writer.WriteLine("RUNNING");
            writer.Flush();

            ExecuteMethod(targetMethod, methodParamsList.ToArray());
        }

        public static void WriteExecuteRunningState()
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string runningPath = Path.Combine(tempDir, "unity_execute_running.txt");
                string resultsPath = Path.Combine(tempDir, "unity_execute_result.json");

                if (File.Exists(resultsPath))
                {
                    File.Delete(resultsPath);
                }
                File.WriteAllText(runningPath, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write execute running state: {ex}");
            }
        }

        public static void ExecuteMethod(MethodInfo method, string[] stringParams)
        {
            string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
            string runningPath = Path.Combine(tempDir, "unity_execute_running.txt");
            string resultsPath = Path.Combine(tempDir, "unity_execute_result.json");

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
                            if (paramType == typeof(string))
                            {
                                convertedParams[i] = rawArg;
                            }
                            else if (paramType == typeof(int))
                            {
                                convertedParams[i] = int.Parse(rawArg);
                            }
                            else if (paramType == typeof(float))
                            {
                                convertedParams[i] = float.Parse(rawArg, System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else if (paramType == typeof(double))
                            {
                                convertedParams[i] = double.Parse(rawArg, System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else if (paramType == typeof(bool))
                            {
                                convertedParams[i] = bool.Parse(rawArg);
                            }
                            else if (paramType == typeof(long))
                            {
                                convertedParams[i] = long.Parse(rawArg);
                            }
                            else if (paramType == typeof(decimal))
                            {
                                convertedParams[i] = decimal.Parse(rawArg, System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                convertedParams[i] = JsonUtility.FromJson(rawArg, paramType);
                            }
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
                    if (result == null)
                    {
                        payload = "null";
                    }
                    else if (result is bool boolVal)
                    {
                        payload = boolVal ? "true" : "false";
                    }
                    else if (result.GetType().IsPrimitive || result is string || result is decimal)
                    {
                        payload = result.ToString();
                    }
                    else
                    {
                        payload = JsonUtility.ToJson(result);
                    }
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
                if (File.Exists(runningPath))
                {
                    try
                    { File.Delete(runningPath); }
                    catch { }
                }

                try
                {
                    var runResult = new UnityExecuteResult
                    {
                        success = success,
                        message = errorMsg,
                        duration = stopwatch.Elapsed.TotalSeconds,
                        payload = payload
                    };
                    string json = JsonUtility.ToJson(runResult, true);
                    File.WriteAllText(resultsPath, json);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"UnityCliRunner: Failed to write execute result: {ex}");
                }
            }
        }
    }
}

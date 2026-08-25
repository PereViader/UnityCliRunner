using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class EvalHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            if (!RoslynCompilerHelper.IsSupported)
            {
                string msg = "The 'eval' command is not supported on this Unity version (" + RoslynCompilerHelper.UnsupportedReason + ")";
                WriteEvalResult(false, msg, 0, null);
                writer.WriteLine($"FAILURE {msg}");
                return;
            }

            if (string.IsNullOrEmpty(payload))
            {
                string msg = "Missing code snippet or expression to evaluate.";
                WriteEvalResult(false, msg, 0, null);
                writer.WriteLine($"FAILURE {msg}");
                return;
            }

            string rawCode = payload.Trim();

            // Attempt compilation with smart wrapping
            byte[] assemblyBytes;
            bool isVoidStatement;
            List<string> errors;

            if (!TryCompileSnippet(rawCode, out assemblyBytes, out isVoidStatement, out errors) || assemblyBytes == null)
            {
                string combinedErrors = string.Join("\n", errors);
                WriteEvalResult(false, combinedErrors, 0, null);
                writer.WriteLine($"FAILURE\n{combinedErrors}");
                return;
            }

            // Execute compiled assembly
            WriteEvalRunningState();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool success = false;
            string errorMsg = "";
            string formattedPayload = null;

            try
            {
                var asm = Assembly.Load(assemblyBytes);
                var runnerType = asm.GetType("__UnityCliEvalRunner");
                if (runnerType == null)
                {
                    throw new Exception("Evaluation runner type could not be loaded from dynamic assembly.");
                }

                var execMethod = runnerType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (execMethod == null)
                {
                    throw new Exception("Evaluation runner execute method not found.");
                }

                object result = execMethod.Invoke(null, null);
                success = true;
                formattedPayload = FormatResult(result, isVoidStatement);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                errorMsg = inner.ToString();
            }
            catch (Exception ex)
            {
                errorMsg = ex.ToString();
            }
            finally
            {
                stopwatch.Stop();
                ClearEvalRunningState();
            }

            double duration = stopwatch.Elapsed.TotalSeconds;
            WriteEvalResult(success, errorMsg, duration, formattedPayload);

            if (success)
            {
                if (formattedPayload != null)
                {
                    writer.WriteLine($"SUCCESS\n{formattedPayload}");
                }
                else
                {
                    writer.WriteLine("SUCCESS");
                }
            }
            else
            {
                writer.WriteLine($"FAILURE\n{errorMsg}");
            }
        }

        private static bool TryCompileSnippet(string rawCode, out byte[] assemblyBytes, out bool isVoidStatement, out List<string> errors)
        {
            assemblyBytes = null;
            isVoidStatement = false;
            errors = new List<string>();

            bool hasExplicitReturn = rawCode.StartsWith("return ", StringComparison.Ordinal) ||
                                     rawCode.Contains("\nreturn ") ||
                                     rawCode.Contains(";return ") ||
                                     rawCode.Contains("; return ");

            if (hasExplicitReturn)
            {
                isVoidStatement = false;
                string source = BuildSource(rawCode);
                return RoslynCompilerHelper.CompileAndEmit(source, out assemblyBytes, out errors);
            }

            // Attempt 1: Expression wrapper `return (<code>);`
            string trimmed = rawCode.TrimEnd(';', ' ', '\r', '\n');
            string exprBody = "return (" + trimmed + ");";
            string exprSource = BuildSource(exprBody);

            if (RoslynCompilerHelper.CompileAndEmit(exprSource, out assemblyBytes, out errors))
            {
                isVoidStatement = false;
                return true;
            }

            // Attempt 2: Statement wrapper `<code>; return null;`
            string stmtBody = rawCode.EndsWith(";") ? (rawCode + "\nreturn null;") : (rawCode + ";\nreturn null;");
            string stmtSource = BuildSource(stmtBody);
            var stmtErrors = new List<string>();

            if (RoslynCompilerHelper.CompileAndEmit(stmtSource, out assemblyBytes, out stmtErrors))
            {
                isVoidStatement = true;
                errors.Clear();
                return true;
            }

            // Return the most informative error list (prefer statement errors if multi-line)
            if (rawCode.Contains("\n") || rawCode.Contains(";"))
            {
                errors = stmtErrors;
            }

            return false;
        }

        private static string BuildSource(string methodBody)
        {
            return @"using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class __UnityCliEvalRunner
{
    public static object Execute()
    {
#line 1 ""eval""
" + methodBody + @"
    }
}";
        }

        public static string FormatResult(object result, bool isVoidStatement = false)
        {
            if (result == null)
            {
                return isVoidStatement ? null : "null";
            }

            if (result is bool b)
            {
                return b ? "true" : "false";
            }

            var type = result.GetType();

            if (type.IsPrimitive || result is string || result is decimal)
            {
                return result.ToString();
            }

            if (type.IsEnum)
            {
                return result.ToString();
            }

            if (result is UnityEngine.Object unityObj && unityObj == null)
            {
                if (result is GameObject) return "null (GameObject)";
                if (result is Component) return "null (Component)";
                return $"null ({result.GetType().Name})";
            }

            // GameObject formatting
            if (result is GameObject go)
            {
                if (go == null) return "null (GameObject)";
                var compNames = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name);

                return $"{go.name} (GameObject) [active: {go.activeSelf}, tag: \"{go.tag}\", layer: {go.layer}, components: {string.Join(", ", compNames)}]";
            }

            // Component formatting
            if (result is Component comp)
            {
                if (comp == null) return "null (Component)";
                string goName = comp.gameObject != null ? comp.gameObject.name : "null";
                return $"{comp.GetType().Name} (Component on \"{goName}\")";
            }

            // Collections / IEnumerable
            if (result is IEnumerable enumerable && !(result is string))
            {
                var items = new List<string>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    count++;
                    if (count > 100)
                    {
                        items.Add("... (truncated)");
                        break;
                    }
                    items.Add(FormatResult(item));
                }
                return "[" + string.Join(", ", items) + "]";
            }

            // JsonUtility serialization fallback
            try
            {
                string json = JsonUtility.ToJson(result, true);
                if (!string.IsNullOrEmpty(json) && json.Trim() != "{}")
                {
                    return json;
                }
            }
            catch { }

            return result.ToString();
        }

        public static void WriteEvalRunningState()
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string runningPath = Path.Combine(tempDir, "unity_eval_running.txt");
                string resultsPath = Path.Combine(tempDir, "unity_eval_result.json");

                if (File.Exists(resultsPath))
                {
                    File.Delete(resultsPath);
                }
                File.WriteAllText(runningPath, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write eval running state: {ex}");
            }
        }

        public static void ClearEvalRunningState()
        {
            try
            {
                string runningPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "unity_eval_running.txt");
                if (File.Exists(runningPath))
                {
                    File.Delete(runningPath);
                }
            }
            catch { }
        }

        public static void WriteEvalResult(bool success, string message, double duration, string payload)
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                string resultsPath = Path.Combine(tempDir, "unity_eval_result.json");

                var runResult = new UnityEvalResult
                {
                    success = success,
                    message = message,
                    duration = duration,
                    payload = payload
                };
                string json = JsonUtility.ToJson(runResult, true);
                File.WriteAllText(resultsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write eval result: {ex}");
            }
        }
    }
}

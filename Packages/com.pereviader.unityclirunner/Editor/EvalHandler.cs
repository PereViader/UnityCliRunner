using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class EvalHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;

        public void Handle(string payload, StreamWriter writer)
        {
            string[] requestParts = (payload ?? "").Split(new[] { ' ' }, 2);
            if (requestParts.Length < 2 || string.IsNullOrWhiteSpace(requestParts[1]))
            {
                writer.WriteLine("ERROR: Missing operation id or code snippet");
                return;
            }

            string operationId = requestParts[0];
            string rawCode = UnescapeCode(requestParts[1].Trim());
            var begin = UnityCliOperationStore.TryBegin(operationId, OperationKinds.Eval, OperationStatus.Compiling, out var existing);
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

            WriteEvalRunningState(operationId);

            if (!RoslynCompilerHelper.IsSupported)
            {
                string msg = "The 'eval' command is not supported on this Unity version (" + RoslynCompilerHelper.UnsupportedReason + ")";
                FinishEval(operationId, false, msg, 0, null);
                writer.WriteLine($"FAILURE {msg}");
                return;
            }

            // Attempt compilation with smart wrapping
            byte[] assemblyBytes;
            bool isVoidStatement;
            List<string> errors;

            if (!TryCompileSnippet(rawCode, out assemblyBytes, out isVoidStatement, out errors) || assemblyBytes == null)
            {
                string combinedErrors = string.Join("\n", errors);
                FinishEval(operationId, false, combinedErrors, 0, null);
                string singleLineErrors = string.Join(" | ", errors);
                writer.WriteLine($"FAILURE {singleLineErrors}");
                return;
            }

            // Execute compiled assembly
            UnityCliOperationStore.Update(operationId, OperationStatus.Executing);

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
                formattedPayload = CommandHelper.FormatResult(result, isVoidStatement);
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
            finally { stopwatch.Stop(); }

            double duration = stopwatch.Elapsed.TotalSeconds;
            FinishEval(operationId, success, errorMsg, duration, formattedPayload);

            if (success)
            {
                if (!string.IsNullOrEmpty(formattedPayload))
                {
                    writer.WriteLine($"SUCCESS {formattedPayload}");
                }
                else
                {
                    writer.WriteLine("SUCCESS");
                }
            }
            else
            {
                writer.WriteLine($"FAILURE {errorMsg}");
            }
        }

        private static string UnescapeCode(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var sb = new StringBuilder(input.Length);
            bool isEscaped = false;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (isEscaped)
                {
                    switch (c)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        default:
                            sb.Append('\\');
                            sb.Append(c);
                            break;
                    }
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    if (i + 1 < input.Length)
                    {
                        char next = input[i + 1];
                        if (next == 'n' || next == 'r' || next == 't' || next == '\\' || next == '"')
                        {
                            isEscaped = true;
                        }
                        else
                        {
                            sb.Append(c);
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
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

        public static void WriteEvalRunningState(string operationId)
        {
            try
            {
                if (!Directory.Exists(UnityCliPaths.TempDir))
                {
                    Directory.CreateDirectory(UnityCliPaths.TempDir);
                }
                UnityCliOperationStore.WriteAtomic(UnityCliPaths.EvalRunningFile, operationId, operationId);
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
                if (File.Exists(UnityCliPaths.EvalRunningFile))
                {
                    File.Delete(UnityCliPaths.EvalRunningFile);
                }
            }
            catch { }
        }

        public static void WriteEvalResult(string operationId, bool success, string message, double duration, string payload, bool interrupted = false)
        {
            try
            {
                if (!Directory.Exists(UnityCliPaths.TempDir))
                {
                    Directory.CreateDirectory(UnityCliPaths.TempDir);
                }
                string resultsPath = UnityCliPaths.EvalResultFile;

                var runResult = new UnityEvalResult
                {
                    operationId = operationId,
                    success = success,
                    interrupted = interrupted,
                    message = message,
                    duration = duration,
                    payload = payload
                };
                string json = JsonUtility.ToJson(runResult, true);
                UnityCliOperationStore.WriteAtomic(resultsPath, json, operationId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write eval result: {ex}");
            }
        }

        public static void MarkInterrupted(string message)
        {
            var operation = UnityCliOperationStore.Read();
            if (operation == null || operation.kind != OperationKinds.Eval)
            {
                return;
            }

            WriteEvalResult(operation.operationId, false, message, 0, null, true);
            ClearEvalRunningState();
            UnityCliOperationStore.Complete(operation.operationId);
        }

        private static void FinishEval(string operationId, bool success, string message, double duration, string payload)
        {
            if (!UnityCliOperationStore.IsOwnedBy(operationId, OperationKinds.Eval))
            {
                return;
            }
            WriteEvalResult(operationId, success, message, duration, payload);
            ClearEvalRunningState();
            UnityCliOperationStore.Complete(operationId);
        }
    }
}

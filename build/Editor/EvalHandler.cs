using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace UnityCliRunner
{
    internal class EvalHandler : ICommandHandler
    {
        private static readonly Regex ExplicitReturnRegex = new Regex(@"(?m)(?:^\s*|[;{}:)]|\belse\s+)\s*return\b", RegexOptions.Compiled);

        public static bool CancelActiveEval(string operationId)
        {
            return OperationExecutionEngine.TryCancel(operationId);
        }

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

            var cts = OperationExecutionEngine.RegisterActiveOperation(operationId, isCancelable: true);

            try
            {
                if (cts.IsCancellationRequested)
                {
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityCliPaths.EvalResultFile, false, "Evaluation was canceled.", 0, null, null, interrupted: true);
                    writer.WriteLine("FAILURE Evaluation was canceled.");
                    return;
                }

                if (!RoslynCompilerHelper.IsSupported)
                {
                    string msg = "The 'eval' command is not supported on this Unity version (" + RoslynCompilerHelper.UnsupportedReason + ")";
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityCliPaths.EvalResultFile, false, msg, 0, null, null);
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
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityCliPaths.EvalResultFile, false, combinedErrors, 0, null, null);
                    string singleLineErrors = string.Join(" | ", errors);
                    writer.WriteLine($"FAILURE {singleLineErrors}");
                    return;
                }

                if (cts.IsCancellationRequested)
                {
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityCliPaths.EvalResultFile, false, "Evaluation was canceled.", 0, null, null, interrupted: true);
                    writer.WriteLine("FAILURE Evaluation was canceled.");
                    return;
                }

                // Acknowledge execution start to client immediately
                writer.WriteLine("RUNNING");
                writer.Flush();

                // Execute compiled assembly under async harness
                UnityCliOperationStore.Update(operationId, OperationStatus.Executing);

                OperationExecutionEngine.Execute(
                    operationId: operationId,
                    operationKind: OperationKinds.Eval,
                    resultFilePath: UnityCliPaths.EvalResultFile,
                    isVoid: isVoidStatement,
                    invoker: ct =>
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

                        return execMethod.Invoke(null, new object[] { ct });
                    },
                    canCancel: true
                );
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Unhandled exception during Eval: {ex}");
                OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityCliPaths.EvalResultFile, false, ex.ToString(), 0, null, null);
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

            bool hasExplicitReturn = !string.IsNullOrEmpty(rawCode) && ExplicitReturnRegex.IsMatch(rawCode);

            if (hasExplicitReturn)
            {
                isVoidStatement = false;
                string source = BuildSource(rawCode);
                var explicitErrors = new List<string>();
                if (RoslynCompilerHelper.CompileAndEmit(source, out assemblyBytes, out explicitErrors))
                {
                    return true;
                }
                errors = explicitErrors;
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

        private static bool HasType(string fullName)
        {
            try
            {
                return CommandHelper.FindType(fullName) != null;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildSource(string methodBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEngine.SceneManagement;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine("using UnityEditor.SceneManagement;");

            if (HasType("UnityEngine.UI.Button") || HasType("UnityEngine.UI.CanvasUpdate"))
            {
                sb.AppendLine("using UnityEngine.UI;");
            }
            if (HasType("UnityEngine.EventSystems.EventSystem") || HasType("UnityEngine.EventSystems.UIBehaviour"))
            {
                sb.AppendLine("using UnityEngine.EventSystems;");
            }
            if (HasType("UnityEditor.Animations.AnimatorController"))
            {
                sb.AppendLine("using UnityEditor.Animations;");
            }

            sb.AppendLine();
            sb.AppendLine("public static class __UnityCliEvalRunner");
            sb.AppendLine("{");
            sb.AppendLine("    public static async Task<object> Execute(CancellationToken cancellationToken)");
            sb.AppendLine("    {");
            sb.AppendLine("#line 1 \"eval\"");
            sb.AppendLine(methodBody);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        [Obsolete("Running state is now tracked exclusively in UnityCliOperationStore.")]
        public static void WriteEvalRunningState(string operationId)
        {
        }

        [Obsolete("Running state is now tracked exclusively in UnityCliOperationStore.")]
        public static void ClearEvalRunningState()
        {
        }

        public static void WriteEvalResult(string operationId, bool success, string message, double duration, string payload, List<ConsoleLogEntry> logs = null, bool interrupted = false)
        {
            OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityCliPaths.EvalResultFile, success, message, duration, payload, logs, interrupted);
        }

        public static void MarkInterrupted(string message, string targetOperationId = null)
        {
            OperationExecutionEngine.MarkInterrupted(OperationKinds.Eval, UnityCliPaths.EvalResultFile, message, targetOperationId);
        }
    }
}

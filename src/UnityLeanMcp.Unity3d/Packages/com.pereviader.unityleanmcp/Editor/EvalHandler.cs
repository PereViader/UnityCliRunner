using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class EvalHandler : ICommandHandler
    {
        public static bool CancelActiveEval(string operationId)
        {
            return OperationExecutionEngine.TryCancel(operationId);
        }

        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;

        public void Handle(string payload, StreamWriter writer)
        {
            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed)
            {
                writer.WriteLine("FAILURE Compilation failed");
                return;
            }

            if (UnityLeanMcpCompilationTracker.IsCompiling || UnityLeanMcpCompilationTracker.RefreshPending)
            {
                writer.WriteLine("BUSY compile");
                return;
            }

            string[] requestParts = (payload ?? "").Split(new[] { ' ' }, 2);
            if (requestParts.Length < 2 || string.IsNullOrWhiteSpace(requestParts[1]))
            {
                writer.WriteLine("ERROR: Missing operation id or code snippet");
                return;
            }

            string operationId = requestParts[0];
            string rawCode = ProtocolCodec.UnescapeLine(requestParts[1].Trim());
            var begin = UnityLeanMcpOperationStore.TryBegin(operationId, OperationKinds.Eval, OperationStatus.Compiling, out var existing);
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
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityLeanMcpPaths.EvalResultFile, false, "Evaluation was canceled.", 0, null, null, interrupted: true);
                    writer.WriteLine("FAILURE Evaluation was canceled.");
                    return;
                }

                if (!RoslynCompilerHelper.IsSupported)
                {
                    string msg = "The 'eval' command is not supported on this Unity version (" + RoslynCompilerHelper.UnsupportedReason + ")";
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityLeanMcpPaths.EvalResultFile, false, msg, 0, null, null);
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
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityLeanMcpPaths.EvalResultFile, false, combinedErrors, 0, null, null);
                    string singleLineErrors = string.Join(" | ", errors);
                    writer.WriteLine($"FAILURE {singleLineErrors}");
                    return;
                }

                if (cts.IsCancellationRequested)
                {
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityLeanMcpPaths.EvalResultFile, false, "Evaluation was canceled.", 0, null, null, interrupted: true);
                    writer.WriteLine("FAILURE Evaluation was canceled.");
                    return;
                }

                // Acknowledge execution start to client immediately
                writer.WriteLine("RUNNING");
                writer.Flush();

                // Execute compiled assembly under async harness
                UnityLeanMcpOperationStore.Update(operationId, OperationStatus.Executing);

                OperationExecutionEngine.Execute(
                    operationId: operationId,
                    operationKind: OperationKinds.Eval,
                    resultFilePath: UnityLeanMcpPaths.EvalResultFile,
                    isVoid: isVoidStatement,
                    invoker: ct =>
                    {
                        var asm = Assembly.Load(assemblyBytes);
                        var runnerType = asm.GetType("__UnityLeanMcpEvalRunner");
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
                Debug.LogError($"UnityLeanMcp: Unhandled exception during Eval: {ex}");
                OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, UnityLeanMcpPaths.EvalResultFile, false, ex.ToString(), 0, null, null);
            }
        }

        private static bool TryCompileSnippet(string rawCode, out byte[] assemblyBytes, out bool isVoidStatement, out List<string> errors)
        {
            assemblyBytes = null;
            errors = new List<string>();

            bool hasValueReturn = RoslynCompilerHelper.HasTopLevelValueReturn(rawCode);
            isVoidStatement = !hasValueReturn;

            string source = BuildSource(rawCode, isVoidStatement);
            return RoslynCompilerHelper.CompileAndEmit(source, out assemblyBytes, out errors);
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

        private static string BuildSource(string methodBody, bool isVoid)
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
            sb.AppendLine("public static class __UnityLeanMcpEvalRunner");
            sb.AppendLine("{");
            if (isVoid)
            {
                sb.AppendLine("    public static async Task Execute(CancellationToken cancellationToken)");
            }
            else
            {
                sb.AppendLine("    public static async Task<object> Execute(CancellationToken cancellationToken)");
            }
            sb.AppendLine("    {");
            sb.AppendLine("#line 1 \"eval\"");
            sb.AppendLine(methodBody);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }


        public static void MarkInterrupted(string message, string targetOperationId = null)
        {
            OperationExecutionEngine.MarkInterrupted(OperationKinds.Eval, UnityLeanMcpPaths.EvalResultFile, message, targetOperationId);
        }
    }
}

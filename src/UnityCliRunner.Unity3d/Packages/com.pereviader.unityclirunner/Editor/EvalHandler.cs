using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal class EvalHandler : ICommandHandler
    {
        private static ConsoleLogCapture s_ActiveLogCapture;
        private static readonly object s_CtsLock = new object();
        private static CancellationTokenSource s_ActiveCts;
        private static string s_ActiveOperationId;

        public static bool CancelActiveEval(string operationId)
        {
            lock (s_CtsLock)
            {
                if (s_ActiveCts != null && (string.IsNullOrEmpty(operationId) || s_ActiveOperationId == operationId))
                {
                    try
                    {
                        s_ActiveCts.Cancel();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"UnityCliRunner: Failed to cancel active eval: {ex.Message}");
                    }
                }
            }
            return false;
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

            CancellationTokenSource cts;
            lock (s_CtsLock)
            {
                s_ActiveCts?.Dispose();
                s_ActiveCts = new CancellationTokenSource();
                s_ActiveOperationId = operationId;
                cts = s_ActiveCts;
            }

            try
            {
                WriteEvalRunningState(operationId);

                if (cts.IsCancellationRequested)
                {
                    FinishEval(operationId, false, "Evaluation was canceled.", 0, null, interrupted: true);
                    writer.WriteLine("FAILURE Evaluation was canceled.");
                    return;
                }

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

                if (cts.IsCancellationRequested)
                {
                    FinishEval(operationId, false, "Evaluation was canceled.", 0, null, interrupted: true);
                    writer.WriteLine("FAILURE Evaluation was canceled.");
                    return;
                }

                // Acknowledge execution start to client immediately
                writer.WriteLine("RUNNING");
                writer.Flush();

                // Execute compiled assembly under async harness
                UnityCliOperationStore.Update(operationId, OperationStatus.Executing);
                UnityCliDispatcher.EnsureInitialized();

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var logCapture = new ConsoleLogCapture();
                s_ActiveLogCapture = logCapture;

                Task<object> task;
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

                    task = (Task<object>)execMethod.Invoke(null, new object[] { cts.Token });
                }
                catch (TargetInvocationException tie)
                {
                    stopwatch.Stop();
                    var inner = tie.InnerException ?? tie;
                    var logs = logCapture.GetLogs();
                    DisposeCapture(logCapture);
                    bool isCanceled = inner is OperationCanceledException;
                    FinishEval(operationId, false, isCanceled ? "Evaluation was canceled." : inner.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: isCanceled);
                    return;
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    var logs = logCapture.GetLogs();
                    DisposeCapture(logCapture);
                    FinishEval(operationId, false, "Evaluation was canceled.", stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: true);
                    return;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    var logs = logCapture.GetLogs();
                    DisposeCapture(logCapture);
                    FinishEval(operationId, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                    return;
                }

                if (task.IsCompleted)
                {
                    try
                    {
                        ProcessCompletedTask(task, operationId, isVoidStatement, stopwatch, logCapture);
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        var logs = logCapture.GetLogs();
                        DisposeCapture(logCapture);
                        FinishEval(operationId, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                    }
                }
                else
                {
                    task.ContinueWith(t => UnityCliDispatcher.Enqueue(() =>
                    {
                        try
                        {
                            ProcessCompletedTask(t, operationId, isVoidStatement, stopwatch, logCapture);
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            var logs = logCapture.GetLogs();
                            DisposeCapture(logCapture);
                            FinishEval(operationId, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Unhandled exception during Eval: {ex}");
                FinishEval(operationId, false, ex.ToString(), 0, null);
            }
        }

        private static void ProcessCompletedTask(Task<object> task, string operationId, bool isVoidStatement, System.Diagnostics.Stopwatch stopwatch, ConsoleLogCapture logCapture)
        {
            if (task.IsFaulted)
            {
                stopwatch.Stop();
                var ex = task.Exception != null
                    ? (task.Exception.InnerExceptions.Count == 1 ? task.Exception.InnerExceptions[0] : task.Exception)
                    : new Exception("Unknown task failure");
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                bool isCanceled = ex is OperationCanceledException;
                FinishEval(operationId, false, isCanceled ? "Evaluation was canceled." : ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: isCanceled);
                return;
            }

            if (task.IsCanceled)
            {
                stopwatch.Stop();
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishEval(operationId, false, "Evaluation was canceled.", stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: true);
                return;
            }

            object rawResult = task.Result;
            UnwrapAndFinish(rawResult, operationId, isVoidStatement, stopwatch, logCapture);
        }

        private static void UnwrapAndFinish(object rawResult, string operationId, bool isVoidStatement, System.Diagnostics.Stopwatch stopwatch, ConsoleLogCapture logCapture)
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
                            bool isCanceled = inner is OperationCanceledException;
                            FinishEval(operationId, false, isCanceled ? "Evaluation was canceled." : inner.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: isCanceled);
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
            string formattedPayload = null;
            if (!isVoidStatement)
            {
                try
                {
                    formattedPayload = CommandHelper.FormatResult(rawResult, isVoidStatement);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityCliRunner: Failed to format eval result: {ex.Message}");
                    formattedPayload = rawResult?.ToString();
                }
            }
            FinishEval(operationId, true, "", duration, formattedPayload, finalLogs);
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
                bool isCanceled = ex is OperationCanceledException;
                FinishEval(operationId, false, isCanceled ? "Evaluation was canceled." : ex.ToString(), duration, null, logs, interrupted: isCanceled);
                return;
            }

            if (innerTask.IsCanceled)
            {
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishEval(operationId, false, "Evaluation was canceled.", duration, null, logs, interrupted: true);
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
                // Non-generic Task (void)
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                string payload = CommandHelper.FormatResult(null, true);
                FinishEval(operationId, true, "", duration, payload, logs);
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

        private static string BuildSource(string methodBody)
        {
            return @"using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class __UnityCliEvalRunner
{
    public static async Task<object> Execute(CancellationToken cancellationToken)
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

        public static void WriteEvalResult(string operationId, bool success, string message, double duration, string payload, List<ConsoleLogEntry> logs = null, bool interrupted = false)
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
                    payload = payload,
                    logs = logs
                };
                string json = JsonUtility.ToJson(runResult, true);
                UnityCliOperationStore.WriteAtomic(resultsPath, json, operationId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write eval result: {ex}");
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
                }
            }

            if (s_ActiveLogCapture != null)
            {
                s_ActiveLogCapture.Dispose();
                s_ActiveLogCapture = null;
            }

            var operation = UnityCliOperationStore.Read();
            string opId = targetOperationId ?? operation?.operationId;
            if (string.IsNullOrEmpty(opId))
            {
                return;
            }
            if (operation != null && operation.kind != OperationKinds.Eval && targetOperationId == null)
            {
                return;
            }

            try
            {
                WriteEvalResult(opId, false, message, 0, null, null, true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write interrupted eval result: {ex}");
            }
            finally
            {
                ClearEvalRunningState();
                UnityCliOperationStore.Complete(opId);
            }
        }

        private static void FinishEval(string operationId, bool success, string message, double duration, string payload, List<ConsoleLogEntry> logs = null, bool interrupted = false)
        {
            lock (s_CtsLock)
            {
                if (s_ActiveOperationId == operationId)
                {
                    s_ActiveCts?.Dispose();
                    s_ActiveCts = null;
                    s_ActiveOperationId = null;
                }
            }

            if (!UnityCliOperationStore.IsOwnedBy(operationId, OperationKinds.Eval))
            {
                return;
            }
            try
            {
                WriteEvalResult(operationId, success, message, duration, payload, logs, interrupted);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to write eval result: {ex}");
            }
            finally
            {
                ClearEvalRunningState();
                UnityCliOperationStore.Complete(operationId);
            }
        }
    }
}

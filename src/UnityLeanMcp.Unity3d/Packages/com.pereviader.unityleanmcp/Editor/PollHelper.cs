using System;
using System.IO;
using UnityEngine;

namespace UnityLeanMcp
{
    internal static class PollHelper
    {
        public static string EscapeLine(string text)
        {
            return ProtocolCodec.EscapeLine(text);
        }

        public static void WriteOperationResultResponse(UnityOperationResult res, StreamWriter writer)
        {
            if (res.success)
            {
                if (!string.IsNullOrEmpty(res.payload))
                {
                    writer.WriteLine($"SUCCESS {EscapeLine(res.payload)}");
                }
                else
                {
                    writer.WriteLine("SUCCESS");
                }
            }
            else if (res.interrupted)
            {
                writer.WriteLine($"INTERRUPTION {EscapeLine(res.message)}");
            }
            else
            {
                writer.WriteLine($"FAILURE {EscapeLine(res.message)}");
            }
        }

        public static void PollOperationResult<TResult>(
            string operationId,
            string resultFilePath,
            string runningFilePath,
            StreamWriter writer,
            Func<TResult, string> getResultOperationId,
            Action<TResult, StreamWriter> writeResultResponse,
            Func<string, string, bool> isRunningMatch = null) where TResult : class
        {
            bool TryWriteTerminalResult()
            {
                if (!string.IsNullOrEmpty(resultFilePath) && File.Exists(resultFilePath))
                {
                    try
                    {
                        string content = CommandHelper.ReadFileWithRetry(resultFilePath, maxRetries: 3, delayMs: 10);
                        if (!string.IsNullOrEmpty(content))
                        {
                            var res = JsonUtility.FromJson<TResult>(content);
                            if (res != null)
                            {
                                string resultOpId = getResultOperationId != null ? getResultOperationId(res) : null;
                                if (string.IsNullOrEmpty(operationId) || resultOpId == operationId)
                                {
                                    writeResultResponse(res, writer);
                                    return true;
                                }
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // File is temporarily being written or replaced; fall through to running check
                    }
                    catch (Exception)
                    {
                        // Transient read or deserialization error during reload/transition; fall through to running check
                    }
                }
                return false;
            }

            // 1. Matching terminal results are authoritative
            if (TryWriteTerminalResult())
            {
                return;
            }

            // 2. Active running state for this operation
            if (isRunningMatch != null)
            {
                if (isRunningMatch(runningFilePath, operationId))
                {
                    writer.WriteLine("RUNNING");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(runningFilePath) && File.Exists(runningFilePath))
            {
                bool matches = false;
                try
                {
                    string runningOperationId = CommandHelper.ReadFileWithRetry(runningFilePath, maxRetries: 3, delayMs: 10).Trim();
                    matches = string.IsNullOrEmpty(operationId) || runningOperationId == operationId;
                }
                catch (IOException)
                {
                    matches = false;
                }
                catch (Exception)
                {
                    matches = false;
                }

                if (matches)
                {
                    writer.WriteLine("RUNNING");
                    return;
                }
            }

            // 3. Fallback to operation store for busy vs idle state
            var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
            if (operation != null)
            {
                if (!string.IsNullOrEmpty(operationId) && operation.operationId != operationId)
                {
                    writer.WriteLine($"BUSY {operation.kind} {operation.operationId}");
                }
                else if (operation.status == OperationStatus.Interrupted)
                {
                    writer.WriteLine("INTERRUPTION Unity editor restarted before the operation completed.");
                }
                else
                {
                    writer.WriteLine("RUNNING");
                }
            }
            else
            {
                // Re-check terminal result file before declaring IDLE to avoid boundary race condition
                // where the result file was durable and ownership was cleared right as polling occurred.
                if (TryWriteTerminalResult())
                {
                    return;
                }

                writer.WriteLine("IDLE");
            }
        }
    }
}

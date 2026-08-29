using System;
using System.IO;
using UnityEngine;

namespace UnityCliRunner
{
    internal static class PollHelper
    {
        public static void PollOperationResult<TResult>(
            string operationId,
            string resultFilePath,
            string runningFilePath,
            StreamWriter writer,
            Func<TResult, string> getResultOperationId,
            Action<TResult, StreamWriter> writeResultResponse,
            Func<string, string, bool> isRunningMatch = null) where TResult : class
        {
            // 1. Matching terminal results are authoritative
            if (File.Exists(resultFilePath))
            {
                try
                {
                    string content = File.ReadAllText(resultFilePath);
                    var res = JsonUtility.FromJson<TResult>(content);
                    if (res != null)
                    {
                        string resultOpId = getResultOperationId != null ? getResultOperationId(res) : null;
                        if (string.IsNullOrEmpty(operationId) || resultOpId == operationId)
                        {
                            writeResultResponse(res, writer);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    writer.WriteLine($"ERROR: {ex.Message}");
                    return;
                }
            }

            // 2. Active running state for this operation
            if (File.Exists(runningFilePath))
            {
                bool matches = false;
                if (isRunningMatch != null)
                {
                    matches = isRunningMatch(runningFilePath, operationId);
                }
                else
                {
                    string runningOperationId = File.ReadAllText(runningFilePath).Trim();
                    matches = string.IsNullOrEmpty(operationId) || runningOperationId == operationId;
                }

                if (matches)
                {
                    writer.WriteLine("RUNNING");
                    return;
                }
            }

            // 3. Fallback to operation store for busy vs idle state
            var operation = UnityCliOperationStore.Read();
            if (operation != null && operation.operationId != operationId)
            {
                writer.WriteLine($"BUSY {operation.kind} {operation.operationId}");
            }
            else
            {
                writer.WriteLine("IDLE");
            }
        }
    }
}

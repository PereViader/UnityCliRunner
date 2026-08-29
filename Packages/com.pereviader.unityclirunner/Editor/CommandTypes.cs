using System;
using System.Collections.Generic;

namespace UnityCliRunner
{
    internal static class OperationKinds
    {
        public const string Refresh = "refresh";
        public const string Recompile = "recompile";
        public const string Test = "test";
        public const string Execute = "execute";
        public const string Eval = "eval";
    }

    internal static class OperationStatus
    {
        public const string Queued = "Queued";
        public const string Executing = "Executing";
        public const string Compiling = "Compiling";
        public const string Refreshing = "Refreshing";
        public const string Recompiling = "Recompiling";
        public const string Requested = "Requested";
        public const string WaitingForUnity = "WaitingForUnity";
        public const string Reloading = "Reloading";
        public const string ShuttingDown = "ShuttingDown";
        public const string Interrupted = "Interrupted";
        public const string Cancelled = "Cancelled";
    }

    [Serializable]
    public class FailedTestInfo
    {
        public string name;
        public string fullName;
        public string message;
        public string stackTrace;
        public double duration;
    }

    [Serializable]
    public class UnityTestRunState
    {
        public string runId;
        public string mode;
        public string filter;
        public string category;
        public string status;
        public string startedUtc;
    }

    [Serializable]
    public class UnityTestRunResult
    {
        public string runId;
        public bool success;
        public int failCount;
        public int passCount;
        public int skipCount;
        public string message;
        public string resultState;
        public List<FailedTestInfo> failedTests;
    }

    [Serializable]
    public class UnityRefreshResult
    {
        public string operationId;
        public bool success;
        public bool interrupted;
        public string message;
    }

    [Serializable]
    public class UnityExecuteResult
    {
        public string operationId;
        public bool success;
        public bool interrupted;
        public string message;
        public double duration;
        public string payload;
    }

    [Serializable]
    public class UnityEvalResult
    {
        public string operationId;
        public bool success;
        public bool interrupted;
        public string message;
        public double duration;
        public string payload;
    }
}

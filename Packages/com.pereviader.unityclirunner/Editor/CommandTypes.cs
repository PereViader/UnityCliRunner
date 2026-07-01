using System;
using System.Collections.Generic;

namespace UnityCliRunner
{
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
    public class UnityTestRunResult
    {
        public bool success;
        public int failCount;
        public int passCount;
        public int skipCount;
        public string message;
        public string resultState;
        public List<FailedTestInfo> failedTests;
    }

    [Serializable]
    public class UnityExecuteResult
    {
        public bool success;
        public string message;
        public double duration;
        public string payload;
    }
}

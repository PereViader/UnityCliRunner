using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UnityCliRunner.Mcp;

public class FailedTestInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("stackTrace")]
    public string StackTrace { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }
}

public class ConsoleLogEntry
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("logType")]
    public string LogType { get; set; } = "";
}

public class UnityRefreshResult
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class UnityTestRunResult
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }

    [JsonPropertyName("skipCount")]
    public int SkipCount { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("resultState")]
    public string ResultState { get; set; } = "";

    [JsonPropertyName("failedTests")]
    public List<FailedTestInfo> FailedTests { get; set; } = new();
}

public class UnityExecuteResult
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    [JsonPropertyName("logs")]
    public List<ConsoleLogEntry> Logs { get; set; } = new();
}

public class UnityEvalResult
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    [JsonPropertyName("logs")]
    public List<ConsoleLogEntry> Logs { get; set; } = new();
}

public class UnityStatusResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = ""; // Ready, Not Running, Compiling, Running Unreachable

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}

public class UnityCliOperationState
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("editorSessionId")]
    public string EditorSessionId { get; set; } = "";

    [JsonPropertyName("startedUtc")]
    public string StartedUtc { get; set; } = "";

    [JsonPropertyName("updatedUtc")]
    public string UpdatedUtc { get; set; } = "";
}

public class UnityCompilationException : System.Exception
{
    public List<string> ErrorLines { get; }
    public UnityCompilationException(string message, List<string> errorLines) : base(message)
    {
        ErrorLines = errorLines;
    }
}

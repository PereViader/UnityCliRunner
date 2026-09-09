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

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("failedTests")]
    public List<FailedTestInfo> FailedTests { get; set; } = new();
}

public class UnityTestRunState
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("filter")]
    public string Filter { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("startedUtc")]
    public string StartedUtc { get; set; } = "";

    [JsonPropertyName("totalTests")]
    public int TotalTests { get; set; }

    [JsonPropertyName("completedTests")]
    public int CompletedTests { get; set; }

    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("skipCount")]
    public int SkipCount { get; set; }

    [JsonPropertyName("currentTestName")]
    public string CurrentTestName { get; set; } = "";
}


public class UnityOperationResult
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

public class UnityExecuteResult : UnityOperationResult
{
}

public class UnityEvalResult : UnityOperationResult
{
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

public class StructuredTestFailure
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("stackTrace")]
    public string StackTrace { get; set; } = "";

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("lineNumber")]
    public int? LineNumber { get; set; }

    [JsonPropertyName("fileUri")]
    public string? FileUri { get; set; }
}

public class StructuredTestRunResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("skipCount")]
    public int SkipCount { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("resultState")]
    public string ResultState { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("failures")]
    public List<StructuredTestFailure> Failures { get; set; } = new();
}

public class StructuredCompilerDiagnostic
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("assembly")]
    public string? Assembly { get; set; }
}

public class StructuredRefreshResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("diagnostics")]
    public List<StructuredCompilerDiagnostic> Diagnostics { get; set; } = new();
}

public class StructuredStatusResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("editorVersion")]
    public string? EditorVersion { get; set; }

    [JsonPropertyName("projectRoot")]
    public string? ProjectRoot { get; set; }

    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("activeOperation")]
    public string? ActiveOperation { get; set; }
}

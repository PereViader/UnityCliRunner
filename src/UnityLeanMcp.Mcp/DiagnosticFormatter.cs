using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityLeanMcp.Mcp;

public interface IDiagnosticFormatter
{
    (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot);
    List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText);
}

public class DiagnosticFormatter : IDiagnosticFormatter
{
    public static IDiagnosticFormatter Default { get; } = new DiagnosticFormatter();

    private static readonly Regex s_StackTraceRegex = new(
        @"(?:(?:in|\bat\b|\()\s*)?(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-]+[\\/])[^:\r\n()]+):(?:line\s+)?(?<line>\d+)\)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_CompilerDiagnosticRegex = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<code>[A-Z0-9]+):\s*(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return (null, null, null);

        using var reader = new StringReader(stackTrace);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Contains("<filename unknown>", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = s_StackTraceRegex.Match(line);
            if (match.Success)
            {
                string rawFile = match.Groups["file"].Value.Trim();
                if (int.TryParse(match.Groups["line"].Value, out int lineNum))
                {
                    string resolvedPath = rawFile;
                    if (!Path.IsPathRooted(resolvedPath) && !string.IsNullOrEmpty(projectRoot))
                    {
                        resolvedPath = Path.Combine(projectRoot, resolvedPath);
                    }

                    string normalizedPath = resolvedPath.Replace('\\', '/');
                    string fileUri = normalizedPath.StartsWith('/')
                        ? $"file://{normalizedPath}#L{lineNum}"
                        : $"file:///{normalizedPath}#L{lineNum}";

                    return (rawFile, lineNum, fileUri);
                }
            }
        }

        return (null, null, null);
    }

    public List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
    {
        var diagnostics = new List<StructuredCompilerDiagnostic>();
        if (string.IsNullOrWhiteSpace(diagnosticText))
            return diagnostics;

        using var reader = new StringReader(diagnosticText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = s_CompilerDiagnosticRegex.Match(line.Trim());
            if (match.Success)
            {
                diagnostics.Add(new StructuredCompilerDiagnostic
                {
                    File = match.Groups["file"].Value,
                    Line = int.Parse(match.Groups["line"].Value),
                    Column = int.Parse(match.Groups["col"].Value),
                    Severity = match.Groups["severity"].Value.ToLowerInvariant(),
                    Code = match.Groups["code"].Value,
                    Message = match.Groups["msg"].Value.Trim(),
                    Assembly = null
                });
            }
        }

        return diagnostics;
    }
}

using Xunit;

namespace UnityCliRunner.Mcp.Tests;

public class ProtocolCodecTests
{
    [Fact]
    public void EscapeToken_NullAndEmpty_ReturnsExpected()
    {
        Assert.Null(ProtocolCodec.EscapeToken(null));
        Assert.Equal(string.Empty, ProtocolCodec.EscapeToken(string.Empty));

        Assert.Null(ProtocolCodec.UnescapeToken(null));
        Assert.Equal(string.Empty, ProtocolCodec.UnescapeToken(string.Empty));
    }

    [Fact]
    public void EscapeLine_NullAndEmpty_ReturnsExpected()
    {
        Assert.Null(ProtocolCodec.EscapeLine(null));
        Assert.Equal(string.Empty, ProtocolCodec.EscapeLine(string.Empty));

        Assert.Null(ProtocolCodec.UnescapeLine(null));
        Assert.Equal(string.Empty, ProtocolCodec.UnescapeLine(string.Empty));
    }

    [Fact]
    public void EscapeParam_IsIdenticalTo_EscapeToken()
    {
        const string input = "path\\with\"quotes\"\r\nand\ttabs";
        Assert.Equal(ProtocolCodec.EscapeToken(input), ProtocolCodec.EscapeParam(input));
    }

    [Fact]
    public void EscapeCode_IsIdenticalTo_EscapeLine()
    {
        const string input = "Debug.Log(\"test\");\r\nreturn 42;";
        Assert.Equal(ProtocolCodec.EscapeLine(input), ProtocolCodec.EscapeCode(input));
    }

    [Theory]
    [InlineData("Hello World")]
    [InlineData("Single'Quotes'AreNotEscaped")]
    [InlineData("Double \"Quotes\" Are Escaped")]
    [InlineData("\"Leading and trailing quotes\"")]
    [InlineData("\"\"")]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Carriage\rReturn")]
    [InlineData("Tab\tSeparated\tValues")]
    [InlineData(@"C:\Program Files\Unity\Editor\Data")]
    [InlineData(@"C:\Folder\With\Trailing\Backslash\")]
    [InlineData(@"\\NetworkShare\Folder\Subfolder")]
    [InlineData(@"Literal \n is not a newline")]
    [InlineData(@"Literal \r and \t are not carriage returns or tabs")]
    [InlineData("Mixed: literal \\n and actual \n newline together")]
    [InlineData("Multiple \\\\ backslashes and \"\" quotes \"\"")]
    [InlineData("C# Code: var s = \"Hello \\\"World\\\"\";\nConsole.WriteLine(s);")]
    public void TokenCodec_Roundtrip_RestoresOriginalString(string input)
    {
        string escaped = ProtocolCodec.EscapeToken(input);
        string unescaped = ProtocolCodec.UnescapeToken(escaped);

        Assert.Equal(input, unescaped);
    }

    [Theory]
    [InlineData("Hello World")]
    [InlineData("Quotes \"remain\" unescaped in whole-line payloads")]
    [InlineData("\"\"")]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Carriage\rReturn")]
    [InlineData("Tab\tSeparated\tValues")]
    [InlineData(@"C:\Program Files\Unity\Editor\Data")]
    [InlineData(@"C:\Folder\With\Trailing\Backslash\")]
    [InlineData(@"\\NetworkShare\Folder\Subfolder")]
    [InlineData(@"Literal \n is not a newline")]
    [InlineData(@"Literal \r and \t are not control chars")]
    [InlineData("Mixed: literal \\n and actual \n newline together")]
    [InlineData(@"Regex: \d+\s*\w+")]
    [InlineData("SUCCESS {\"status\":\"ok\",\"path\":\"C:\\\\Unity\"}")]
    public void LineCodec_Roundtrip_RestoresOriginalString(string input)
    {
        string escaped = ProtocolCodec.EscapeLine(input);
        string unescaped = ProtocolCodec.UnescapeLine(escaped);

        Assert.Equal(input, unescaped);
    }

    [Fact]
    public void LineCodec_DoesNotEscapeDoubleQuotes()
    {
        const string input = "SUCCESS {\"key\": \"value\"}";
        string escaped = ProtocolCodec.EscapeLine(input);

        // Quotes must remain as literal double-quotes in line-based responses
        Assert.Contains("\"key\"", escaped);
        Assert.DoesNotContain("\\\"", escaped);

        string unescaped = ProtocolCodec.UnescapeLine(escaped);
        Assert.Equal(input, unescaped);
    }

    [Fact]
    public void TokenCodec_EscapesDoubleQuotes()
    {
        const string input = "param \"with\" quotes";
        string escaped = ProtocolCodec.EscapeToken(input);

        Assert.Contains("\\\"with\\\"", escaped);

        string unescaped = ProtocolCodec.UnescapeToken(escaped);
        Assert.Equal(input, unescaped);
    }

    [Fact]
    public void LineCodec_EscapesBackslashes_PreventingPathCorruption()
    {
        // When a path like C:\new\test or C:\read\table is sent,
        // \n and \r must not be mistakenly treated as control characters.
        const string windowsPath = @"C:\new\read\test\table";
        string escaped = ProtocolCodec.EscapeLine(windowsPath);

        // All backslashes must be escaped
        Assert.Equal(@"C:\\new\\read\\test\\table", escaped);

        string unescaped = ProtocolCodec.UnescapeLine(escaped);
        Assert.Equal(windowsPath, unescaped);
    }

    [Fact]
    public void TokenCodec_EscapesBackslashes_PreventingPathCorruption()
    {
        const string windowsPath = @"C:\new\read\test\table";
        string escaped = ProtocolCodec.EscapeToken(windowsPath);

        Assert.Equal(@"C:\\new\\read\\test\\table", escaped);

        string unescaped = ProtocolCodec.UnescapeToken(escaped);
        Assert.Equal(windowsPath, unescaped);
    }

    [Fact]
    public void TokenCodec_HandlesLoneAndTrailingBackslashes()
    {
        const string trailingSlash = @"test\";
        string escaped = ProtocolCodec.EscapeToken(trailingSlash);
        Assert.Equal(@"test\\", escaped);

        string unescaped = ProtocolCodec.UnescapeToken(escaped);
        Assert.Equal(trailingSlash, unescaped);

        // Unescaped trailing lone backslash without escaping
        Assert.Equal(@"test\", ProtocolCodec.UnescapeToken(@"test\"));
    }

    [Fact]
    public void LineCodec_HandlesLoneAndTrailingBackslashes()
    {
        const string trailingSlash = @"test\";
        string escaped = ProtocolCodec.EscapeLine(trailingSlash);
        Assert.Equal(@"test\\", escaped);

        string unescaped = ProtocolCodec.UnescapeLine(escaped);
        Assert.Equal(trailingSlash, unescaped);

        Assert.Equal(@"test\", ProtocolCodec.UnescapeLine(@"test\"));
    }

    [Fact]
    public void ParameterRoundtrip_SimulatingCommandHelperSplitArguments()
    {
        string[] originalArgs =
        [
            "arg with spaces",
            "arg with \"quotes\"",
            "arg with \n newline and \r carriage return",
            "arg with \t tab",
            @"C:\Program Files\Unity\Editor\Data\",
            @"\\Server\Share\Path",
            @"\d+\s*regex",
            ""
        ];

        // Format command line as UnityClient.cs does
        var sb = new System.Text.StringBuilder("EXECUTE_METHOD op123 MyClass.MyMethod");
        foreach (var arg in originalArgs)
        {
            sb.Append(" \"").Append(ProtocolCodec.EscapeParam(arg)).Append('"');
        }

        string commandLine = sb.ToString();

        // Parse with same logic as CommandHelper.SplitArguments
        var args = new System.Collections.Generic.List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool inArg = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                if (i + 1 < commandLine.Length)
                {
                    char next = commandLine[i + 1];
                    if (next == '"' || next == '\\')
                    {
                        current.Append('\\');
                        current.Append(next);
                        i++;
                        inArg = true;
                        continue;
                    }
                }
                current.Append('\\');
                inArg = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                inArg = true;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (inArg)
                {
                    args.Add(ProtocolCodec.UnescapeToken(current.ToString()));
                    current.Clear();
                    inArg = false;
                }
            }
            else
            {
                current.Append(c);
                inArg = true;
            }
        }

        if (inArg)
        {
            args.Add(ProtocolCodec.UnescapeToken(current.ToString()));
        }

        Assert.Equal("EXECUTE_METHOD", args[0]);
        Assert.Equal("op123", args[1]);
        Assert.Equal("MyClass.MyMethod", args[2]);

        for (int i = 0; i < originalArgs.Length; i++)
        {
            Assert.Equal(originalArgs[i], args[3 + i]);
        }
    }
}

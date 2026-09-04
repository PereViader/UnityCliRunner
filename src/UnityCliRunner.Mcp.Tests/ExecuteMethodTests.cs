using System;
using System.Threading.Tasks;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

[Collection("UnityIntegration")]
public class ExecuteMethodTests
{
    private readonly UnityIntegrationFixture _fixture;

    public ExecuteMethodTests(UnityIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestExecuteSuccess_InvokesStaticMethodSuccessfully()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteSuccess");
        await using var client = new McpTestClient(_fixture.UnityRoot);
        
        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.SuccessMethod"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Method execution succeeded.", result.Text);
    }

    [Fact]
    public async Task TestExecuteFailure_ReportsThrownException()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteFailure");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.FailMethod"
        });

        Assert.True(result.IsError);
        Assert.Contains("Method execution failed.", result.Text);
    }

    [Fact]
    public async Task TestExecuteNotFound_ReportsMethodNotFoundError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteNotFound");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.NonExistentMethod"
        });

        Assert.True(result.IsError);
        Assert.Contains("Static method 'NonExistentMethod' not found", result.Text);
    }

    [Fact]
    public async Task TestExecuteCompileError_FailsWhenProjectHasCompilationErrors()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteCompileError");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.SuccessMethod"
        });

        Assert.True(result.IsError);
        Assert.Contains("CS1002", result.Text);
    }

    [Fact]
    public async Task TestConsecutiveRefreshCompileError_ReportsCompilationDiagnostics()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteCompileError");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_refresh");

        Assert.True(result.IsError);
        Assert.Contains("CS1002", result.Text);
    }

    [Fact]
    public async Task TestExecuteReturnsInt_ReturnsMethodResult()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteReturnsInt");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.Something"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("4", result.Text);
        Assert.Contains("Method execution succeeded.", result.Text);
    }

    [Fact]
    public async Task TestExecuteReturnsObject_ReturnsSerializedObject()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteReturnsObject");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.Something"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("{\"Value\":4}", result.Text);
    }

    [Fact]
    public async Task TestExecuteParams_ParsesMultipleTypedArguments()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteParams");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.ParamsMethod",
            args = new[] { "4", "3.5", "hello", "{\"Value\":42}" }
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("intVal", result.Text);
        Assert.Contains("floatVal", result.Text);
    }

    [Fact]
    public async Task TestExecuteOverloads_ResolvesCorrectOverloadBySignature()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteOverloads");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.OverloadMethod",
            args = new[] { "42", "hello", "true" }
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Overload 3: 42, hello, True", result.Text);
    }

    [Fact]
    public async Task TestExecuteParamCountMismatch_ReportsParameterCountError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteParamCountMismatch");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.ExactTwo",
            args = new[] { "42" }
        });

        Assert.True(result.IsError);
        Assert.Contains("Static method 'ExactTwo' not found", result.Text);
    }

    [Fact]
    public async Task TestExecuteAmbiguous_ReportsAmbiguousMatchError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteAmbiguous");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.Ambiguous",
            args = new[] { "42" }
        });

        Assert.True(result.IsError);
        Assert.Contains("Ambiguous", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestExecuteParamConversionFailure_ReportsTypeConversionError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteParamConversionFailure");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.NumberMethod",
            args = new[] { "not_a_number" }
        });

        Assert.True(result.IsError);
        Assert.Contains("Failed to convert parameter", result.Text);
    }

    [Fact]
    public async Task TestExecuteReturnsNull_CompletesWithoutError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteReturnsNull");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.NullMethod"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Method execution succeeded.", result.Text);
    }

    [Fact]
    public async Task TestExecuteMultiLineParam_PreservesNewlinesInArguments()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteMultiLineParam");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.EchoMultiLine",
            args = new[] { "line1\nline2\nline3" }
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("line1|line2|line3", result.Text);
    }

    [Fact]
    public async Task TestExecuteConsoleLogs_CapturesUnityConsoleLogs()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestExecuteConsoleLogs");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.LogAndReturn"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Standard log message from execute", result.Text);
    }
}

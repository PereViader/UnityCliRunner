using System;
using System.Threading.Tasks;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

[Collection("UnityIntegration")]
public class EvalTests
{
    private readonly UnityIntegrationFixture _fixture;

    public EvalTests(UnityIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestEvalSuccess_EvaluatesAdditionExpression()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "1 + 1" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("2", result.Text);
    }

    [Fact]
    public async Task TestEvalExpression_EvaluatesMathfSqrt()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "Mathf.Sqrt(16f)" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("4", result.Text);
    }

    [Fact]
    public async Task TestEvalSyntaxError_ReturnsCompilationFailure()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "this is invalid syntax @@" });

        Assert.True(result.IsError);
        Assert.Contains("CS", result.Text); // Roslyn compiler diagnostic error code
    }

    [Fact]
    public async Task TestEvalMultiStatement_ExecutesMultipleStatementsAndReturnsValue()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "int a = 10; int b = 20; return a + b;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("30", result.Text);
    }

    [Fact]
    public async Task TestEvalLiteralNewlines_HandlesMultiLineCodeBlock()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "int x = 10;\nint y = 20;\nreturn x + y;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("30", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidStatement_ExecutesWithoutError()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "UnityEngine.Debug.Log(42);" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("42", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidMethod_ExecutesGCCollectWithoutError()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "System.GC.Collect()" });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestEvalConsoleLogs_CapturesLogsAndReturnValue()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "UnityEngine.Debug.Log(\"info log\"); UnityEngine.Debug.LogWarning(\"warn log\"); return 100;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("info log", result.Text);
        Assert.Contains("warn log", result.Text);
        Assert.Contains("100", result.Text);
    }

    [Fact]
    public async Task TestEvalNull_ReturnsNullWithoutError()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "(object)null" });

        Assert.False(result.IsError, result.Text);
        Assert.True(string.IsNullOrWhiteSpace(result.Text) || result.Text.Contains("null"));
    }

    [Fact]
    public async Task TestEvalDestroyedObject_HandlesDestroyedUnityObjectGracefully()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "GameObject go = new GameObject(\"TempObj\"); GameObject.DestroyImmediate(go); return go;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("null", result.Text);
    }

    [Fact]
    public async Task TestEvalGameObject_InstantiatesAndReturnsGameObjectRepresentation()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "new GameObject(\"SampleEntity\")" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("SampleEntity", result.Text);
    }

    [Fact]
    public async Task TestEvalCollection_ReturnsArrayElements()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "new int[] { 10, 20, 30 }" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("10", result.Text);
        Assert.Contains("20", result.Text);
        Assert.Contains("30", result.Text);
    }

    [Fact]
    public async Task TestEvalException_ReportsUserExceptionMessage()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "throw new System.InvalidOperationException(\"test-eval-error\");" });

        Assert.True(result.IsError);
        Assert.Contains("test-eval-error", result.Text);
    }
}

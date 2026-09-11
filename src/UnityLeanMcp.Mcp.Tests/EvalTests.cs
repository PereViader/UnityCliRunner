using System;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

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
        var result = await client.CallToolAsync("unity_eval", new { code = "return 1 + 1;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("2", result.Text);
    }

    [Fact]
    public async Task TestEvalExpression_EvaluatesMathfSqrt()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "return Mathf.Sqrt(16f);" });

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
        var result = await client.CallToolAsync("unity_eval", new { code = "System.GC.Collect();" });

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
        var result = await client.CallToolAsync("unity_eval", new { code = "return (object)null;" });

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
        var result = await client.CallToolAsync("unity_eval", new { code = "return new GameObject(\"SampleEntity\");" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("SampleEntity", result.Text);
    }

    [Fact]
    public async Task TestEvalCollection_ReturnsArrayElements()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "return new int[] { 10, 20, 30 };" });

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

    [Fact]
    public async Task TestEvalAsync_TopLevelAwait_ReturnsValue()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "await System.Threading.Tasks.Task.Delay(50); return 42;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("42", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_TopLevelAwait_Yield()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "await System.Threading.Tasks.Task.Yield(); return \"async-ok\";" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("async-ok", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_ReturnsTaskGeneric()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "return System.Threading.Tasks.Task.FromResult(\"hello-task\");" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("hello-task", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_VoidDelay()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "await System.Threading.Tasks.Task.Delay(50);" });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_CapturesLogsAcrossFrames()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "UnityEngine.Debug.Log(\"before-await\"); await System.Threading.Tasks.Task.Delay(50); UnityEngine.Debug.Log(\"after-await\"); return \"done\";";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("before-await", result.Text);
        Assert.Contains("after-await", result.Text);
        Assert.Contains("done", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_ExceptionInAsyncContinuation()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "await System.Threading.Tasks.Task.Delay(20); throw new System.InvalidOperationException(\"delayed-fail\");";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.True(result.IsError);
        Assert.Contains("delayed-fail", result.Text);
    }

    [Fact]
    public async Task TestEvalDefaultImports_ResolvesExpandedNamespaces()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        // Uses Path from System.IO, Image from UnityEngine.UI, UIBehaviour from UnityEngine.EventSystems, AnimatorController from UnityEditor.Animations
        string code = "return Path.Combine(\"dir\", \"file\") + \":\" + typeof(Image).Name + \":\" + typeof(UIBehaviour).Name + \":\" + typeof(AnimatorController).Name;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("dir", result.Text);
        Assert.Contains("Image", result.Text);
        Assert.Contains("UIBehaviour", result.Text);
        Assert.Contains("AnimatorController", result.Text);
    }

    [Fact]
    public async Task TestEvalFormatting_Scene_ReturnsRicherFormat()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "return SceneManager.GetActiveScene();" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Scene \"", result.Text);
        Assert.Contains("path: \"", result.Text);
        Assert.Contains("isLoaded:", result.Text);
        Assert.Contains("isDirty:", result.Text);
        Assert.Contains("rootCount:", result.Text);
    }

    [Fact]
    public async Task TestEvalFormatting_Transform_ReturnsTransformFormat()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "return new GameObject(\"EvalTransformTest\").transform;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Transform \"EvalTransformTest\" [children: 0, localPos:", result.Text);
    }

    [Fact]
    public async Task TestEvalFormatting_ScriptableObject_ReturnsScriptableObjectFormat()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "return ScriptableObject.CreateInstance<ScriptableObject>();" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("ScriptableObject (ScriptableObject) [name:", result.Text);
    }

    [Fact]
    public async Task TestEvalIndentedReturn_ReturnsValue()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "bool condition = true;\nif (condition)\n{\n    return \"indented-result\";\n}\nelse\n{\n    return \"fallback\";\n}";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("indented-result", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidEarlyReturn_ExecutesWithoutError()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "if (true) return; UnityEngine.Debug.Log(\"should not log\");";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("should not log", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidExplicitReturn_ExecutesWithoutError()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_eval", new { code = "return;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("(Evaluation succeeded with no output)", result.Text);
    }

    [Fact]
    public async Task TestEvalLambdaWithReturnInVoid_ExecutesWithoutError()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "var list = new System.Collections.Generic.List<int> { 1, 2 }.FindAll(x => { return x > 1; });";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("(Evaluation succeeded with no output)", result.Text);
    }

    [Fact]
    public async Task TestEvalLambdaWithVoidInValueReturn_ReturnsValue()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        string code = "System.Action a = () => { return; }; a(); return 42;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("42", result.Text);
    }
}

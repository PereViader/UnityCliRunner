using System;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Collection("UnityIntegration")]
[Trait("Category", "UnityIntegration")]
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
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "return 1 + 1;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("2", result.Text);
    }

    [Fact]
    public async Task TestEvalExpression_EvaluatesMathfSqrt()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "using UnityEngine;\nreturn Mathf.Sqrt(16f);" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("4", result.Text);
    }

    [Fact]
    public async Task TestEvalWithoutUsings_FailsToResolveUnimportedType()
    {
        var client = _fixture.SharedClient;
        // Mathf is in UnityEngine; without 'using UnityEngine;', this must fail to compile
        var result = await client.CallToolAsync("unity_eval", new { code = "return Mathf.Sqrt(16f);" });

        Assert.True(result.IsError);
        Assert.Contains("CS0103", result.Text); // The name 'Mathf' does not exist in the current context
    }

    [Fact]
    public async Task TestEvalSyntaxError_ReturnsCompilationFailure()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "this is invalid syntax @@" });

        Assert.True(result.IsError);
        Assert.Contains("CS", result.Text); // Roslyn compiler diagnostic error code
    }

    [Fact]
    public async Task TestEvalMultiStatement_ExecutesMultipleStatementsAndReturnsValue()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "int a = 10; int b = 20; return a + b;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("30", result.Text);
    }

    [Fact]
    public async Task TestEvalLiteralNewlines_HandlesMultiLineCodeBlock()
    {
        var client = _fixture.SharedClient;
        string code = "int x = 10;\nint y = 20;\nreturn x + y;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("30", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidStatement_ExecutesWithoutError()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "UnityEngine.Debug.Log(42);" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("42", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidMethod_ExecutesGCCollectWithoutError()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "System.GC.Collect();" });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestEvalConsoleLogs_CapturesLogsAndReturnValue()
    {
        var client = _fixture.SharedClient;
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
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "return (object)null;" });

        Assert.False(result.IsError, result.Text);
        Assert.True(string.IsNullOrWhiteSpace(result.Text) || result.Text.Contains("null"));
    }

    [Fact]
    public async Task TestEvalDestroyedObject_HandlesDestroyedUnityObjectGracefully()
    {
        var client = _fixture.SharedClient;
        string code = "using UnityEngine;\nGameObject go = new GameObject(\"TempObj\"); GameObject.DestroyImmediate(go); return go;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("null", result.Text);
    }

    [Fact]
    public async Task TestEvalGameObject_InstantiatesAndReturnsGameObjectRepresentation()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "using UnityEngine;\nreturn new GameObject(\"SampleEntity\");" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("SampleEntity", result.Text);
    }

    [Fact]
    public async Task TestEvalCollection_ReturnsArrayElements()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "return new int[] { 10, 20, 30 };" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("10", result.Text);
        Assert.Contains("20", result.Text);
        Assert.Contains("30", result.Text);
    }

    [Fact]
    public async Task TestEvalException_ReportsUserExceptionMessage()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "throw new System.InvalidOperationException(\"test-eval-error\");" });

        Assert.True(result.IsError);
        Assert.Contains("test-eval-error", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_TopLevelAwait_ReturnsValue()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "await System.Threading.Tasks.Task.Delay(50); return 42;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("42", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_TopLevelAwait_Yield()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "await System.Threading.Tasks.Task.Yield(); return \"async-ok\";" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("async-ok", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_ReturnsTaskGeneric()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "return System.Threading.Tasks.Task.FromResult(\"hello-task\");" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("hello-task", result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_VoidDelay()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "await System.Threading.Tasks.Task.Delay(50);" });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestEvalAsync_CapturesLogsAcrossFrames()
    {
        var client = _fixture.SharedClient;
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
        var client = _fixture.SharedClient;
        string code = "await System.Threading.Tasks.Task.Delay(20); throw new System.InvalidOperationException(\"delayed-fail\");";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.True(result.IsError);
        Assert.Contains("delayed-fail", result.Text);
    }

    [Fact]
    public async Task TestEvalExplicitImports_ResolvesExpandedNamespaces()
    {
        var client = _fixture.SharedClient;
        // Uses Path from System.IO, Image from UnityEngine.UI, UIBehaviour from UnityEngine.EventSystems, AnimatorController from UnityEditor.Animations
        string code = "using System.IO;\nusing UnityEngine.UI;\nusing UnityEngine.EventSystems;\nusing UnityEditor.Animations;\nreturn Path.Combine(\"dir\", \"file\") + \":\" + typeof(Image).Name + \":\" + typeof(UIBehaviour).Name + \":\" + typeof(AnimatorController).Name;";
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
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "using UnityEngine.SceneManagement;\nreturn SceneManager.GetActiveScene();" });

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
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "using UnityEngine;\nreturn new GameObject(\"EvalTransformTest\").transform;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Transform \"EvalTransformTest\" [children: 0, localPos:", result.Text);
    }

    [Fact]
    public async Task TestEvalFormatting_ScriptableObject_ReturnsScriptableObjectFormat()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "using UnityEngine;\nreturn ScriptableObject.CreateInstance<ScriptableObject>();" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("ScriptableObject (ScriptableObject) [name:", result.Text);
    }

    [Fact]
    public async Task TestEvalIndentedReturn_ReturnsValue()
    {
        var client = _fixture.SharedClient;
        string code = "bool condition = true;\nif (condition)\n{\n    return \"indented-result\";\n}\nelse\n{\n    return \"fallback\";\n}";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("indented-result", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidEarlyReturn_ExecutesWithoutError()
    {
        var client = _fixture.SharedClient;
        string code = "if (true) return; UnityEngine.Debug.Log(\"should not log\");";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("should not log", result.Text);
    }

    [Fact]
    public async Task TestEvalVoidExplicitReturn_ExecutesWithoutError()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_eval", new { code = "return;" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("(Evaluation completed without a return statement. Use 'return <expr>;' to return a value.)", result.Text);
    }

    [Fact]
    public async Task TestEvalLambdaWithReturnInVoid_ExecutesWithoutError()
    {
        var client = _fixture.SharedClient;
        string code = "var list = new System.Collections.Generic.List<int> { 1, 2 }.FindAll(x => { return x > 1; });";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("(Evaluation completed without a return statement. Use 'return <expr>;' to return a value.)", result.Text);
    }

    [Fact]
    public async Task TestEvalLambdaWithVoidInValueReturn_ReturnsValue()
    {
        var client = _fixture.SharedClient;
        string code = "System.Action a = () => { return; }; a(); return 42;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("42", result.Text);
    }

    [Fact]
    public async Task TestEvalUsingDirective_ImportsNamespaceAndEvaluates()
    {
        var client = _fixture.SharedClient;
        string code = "using System.IO;\nreturn Path.GetExtension(\"sample.txt\");";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains(".txt", result.Text);
    }

    [Fact]
    public async Task TestEvalUsingStaticDirective_EvaluatesDirectMethod()
    {
        var client = _fixture.SharedClient;
        string code = "using static System.Math;\nreturn Sqrt(64.0);";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("8", result.Text);
    }

    [Fact]
    public async Task TestEvalUsingAliasDirective_EvaluatesViaAlias()
    {
        var client = _fixture.SharedClient;
        string code = "using MyPath = System.IO.Path;\nreturn MyPath.GetFileName(\"folder/file.cs\");";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("file.cs", result.Text);
    }

    [Fact]
    public async Task TestEvalUsingDirective_WithVoidStatement()
    {
        var client = _fixture.SharedClient;
        string code = "using UnityEngine;\nDebug.Log(\"test using void log\");";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("test using void log", result.Text);
    }

    [Fact]
    public async Task TestEvalIDisposableUsingStatement_PreservedInsideMethod()
    {
        var client = _fixture.SharedClient;
        string code = "using (var reader = new System.IO.StringReader(\"disposable line\"))\n{\n    return reader.ReadLine();\n}";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("disposable line", result.Text);
    }

    [Fact]
    public async Task TestEvalIDisposableUsingDeclaration_PreservedInsideMethod()
    {
        var client = _fixture.SharedClient;
        string code = "using var ms = new System.IO.MemoryStream(new byte[] { 1, 2, 3 });\nreturn ms.Length;";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("3", result.Text);
    }

    [Fact]
    public async Task TestEvalUnwrapJsonCode_WhenAccidentallyWrappedInJson()
    {
        var client = _fixture.SharedClient;
        string code = "{\n  \"code\": \"using System.IO;\\nreturn Path.GetFileName(\\\"test/doc.pdf\\\");\"\n}";
        var result = await client.CallToolAsync("unity_eval", new { code });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("doc.pdf", result.Text);
    }
}

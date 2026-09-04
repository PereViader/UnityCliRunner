using System;
using System.Threading.Tasks;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

[Collection("UnityIntegration")]
public class TestFrameworkTests
{
    private readonly UnityIntegrationFixture _fixture;

    public TestFrameworkTests(UnityIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestEverythingPasses_RunsEditModeTestsSuccessfully()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestEverythingPasses");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_run_tests", new { mode = "editmode" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestCompileErrorsAndWarnings_FailsOnScriptCompileError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestCompileErrorsAndWarnings");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_run_tests", new { mode = "editmode" });

        Assert.True(result.IsError);
        Assert.Contains("CS", result.Text);
    }

    [Fact]
    public async Task TestCompileWarningsAndPass_RunsTestsDespiteCompilerWarnings()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestCompileWarningsAndPass");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_run_tests", new { mode = "editmode" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndFailures_ReportsFailedAssertionsAndStackTraces()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestNoWarningsAndFailures");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_run_tests", new { mode = "editmode" });

        Assert.True(result.IsError);
        Assert.Contains("Tests Failed:", result.Text);
        Assert.Contains("Failures:", result.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndSkipped_ReportsSkippedTests()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestNoWarningsAndSkipped");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_run_tests", new { mode = "editmode" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("skipped", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestFilterCategory_ExcludesOrIncludesCategories()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestFilterCategory");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            category = "!LongRunning"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestFilterByName_RunsOnlyTargetedTest()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestFilterByName");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            filter = "SpecificTargetTest"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed: 1 passed", result.Text);
    }
}

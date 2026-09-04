using System;
using System.Threading.Tasks;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

[Collection("UnityIntegration")]
public class LifecycleAndCompilationTests
{
    private readonly UnityIntegrationFixture _fixture;

    public LifecycleAndCompilationTests(UnityIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestBackgroundStatusOnline_ReturnsReadyWhenEditorIsRunning()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_status");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Status: Ready", result.Text);
    }

    [Fact]
    public async Task TestBackgroundStartAlreadyRunning_ReportsAlreadyRunningIdempotently()
    {
        await using var client = new McpTestClient(_fixture.UnityRoot);
        var result = await client.CallToolAsync("unity_start");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("already running", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestRefresh_TriggersAssetDatabaseRefreshSuccessfully()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestRefresh");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_refresh");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Unity is ready!", result.Text);
    }

    [Fact]
    public async Task TestRecompile_TriggersCleanScriptRecompilationSuccessfully()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestRecompile");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_recompile");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Unity is ready!", result.Text);
    }

    [Fact]
    public async Task TestPollRefreshNonBlocking_PollsRefreshStateWithoutBlocking()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestPollRefreshNonBlocking");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.PollRefreshWhileBusy"
        });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestBusyDetectionBeforeRefresh_RejectsConcurrentMutatingOperations()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestBusyDetectionBeforeRefresh");
        await using var client = new McpTestClient(_fixture.UnityRoot);

        var result = await client.CallToolAsync("unity_execute_method", new
        {
            methodName = "Tests.DummyExecuteClass.TestBusyDetection"
        });

        Assert.False(result.IsError, result.Text);
    }
}

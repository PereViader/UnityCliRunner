using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[CollectionDefinition("UnityIntegration", DisableParallelization = true)]
public class UnityIntegrationCollection : ICollectionFixture<UnityIntegrationFixture>
{
}

public class UnityIntegrationFixture : IAsyncLifetime
{
    private string _repoRoot = null!;
    private string _unityRoot = null!;
    private string _dummyTestPath = null!;
    private string _dummyTestMetaPath = null!;
    private string _backupDummyTestPath = null!;
    private string _backupDummyTestMetaPath = null!;
    private string _tempDir = null!;
    private McpTestClient? _sharedClient;

    public string RepoRoot => _repoRoot;
    public string UnityRoot => _unityRoot;
    public McpTestClient SharedClient => _sharedClient ?? throw new InvalidOperationException("Shared McpTestClient is not initialized.");

    public async Task InitializeAsync()
    {
        _repoRoot = McpTestClient.GetRepoRoot();
        _unityRoot = McpTestClient.GetUnityProjectRoot();
        _dummyTestPath = Path.Combine(_unityRoot, "Assets", "Tests", "Editor", "DummyTest.cs");
        _dummyTestMetaPath = Path.Combine(_unityRoot, "Assets", "Tests", "Editor", "DummyTest.cs.meta");
        _tempDir = Path.Combine(Path.GetTempPath(), "UnityLeanMcp_TestBackup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _backupDummyTestPath = Path.Combine(_tempDir, "DummyTest.cs.bak");
        _backupDummyTestMetaPath = Path.Combine(_tempDir, "DummyTest.cs.meta.bak");

        // Take backup of original DummyTest.cs
        if (File.Exists(_dummyTestPath))
        {
            File.Copy(_dummyTestPath, _backupDummyTestPath, true);
        }
        if (File.Exists(_dummyTestMetaPath))
        {
            File.Copy(_dummyTestMetaPath, _backupDummyTestMetaPath, true);
        }

        // Build / publish MCP server if needed
        string publishedDll = Path.Combine(_unityRoot, "Packages", "com.pereviader.unityleanmcp", "MCP~", "UnityLeanMcp.Mcp.dll");
        if (!File.Exists(publishedDll))
        {
            await PublishMcpServerAsync();
        }

        // Ensure Unity is started and ready using shared client
        _sharedClient = new McpTestClient(_unityRoot);
        await _sharedClient.InitializeAsync();
        var startRes = await _sharedClient.CallToolAsync("unity_refresh", timeout: TimeSpan.FromSeconds(120));
        if (startRes.IsError)
        {
            throw new InvalidOperationException($"Failed to start Unity for integration tests: {startRes.Text}");
        }
    }

    private static async Task PublishMcpServerAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "publish src/UnityLeanMcp.Mcp/UnityLeanMcp.Mcp.csproj -c Release -f net10.0 -o src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/MCP~",
            WorkingDirectory = McpTestClient.GetRepoRoot(),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
    }

    public async Task<IAsyncDisposable> UseFixtureAsync(string testCaseName)
    {
        string fixtureSource = Path.Combine(_repoRoot, "src", "UnityLeanMcp.Mcp.Tests", "Fixtures", testCaseName, "DummyTest.cs");
        if (!File.Exists(fixtureSource))
        {
            fixtureSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", testCaseName, "DummyTest.cs");
        }

        if (File.Exists(fixtureSource))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dummyTestPath)!);
            File.Copy(fixtureSource, _dummyTestPath, true);
            File.SetLastWriteTimeUtc(_dummyTestPath, DateTime.UtcNow);
            await Task.Delay(100);
        }

        return new DummyTestScope(this);
    }

    public void RestoreOriginalDummyTest()
    {
        try
        {
            if (File.Exists(_backupDummyTestPath))
            {
                File.Copy(_backupDummyTestPath, _dummyTestPath, true);
                File.SetLastWriteTimeUtc(_dummyTestPath, DateTime.UtcNow);
            }
            else if (File.Exists(_dummyTestPath))
            {
                File.Delete(_dummyTestPath);
            }

            if (File.Exists(_backupDummyTestMetaPath))
            {
                File.Copy(_backupDummyTestMetaPath, _dummyTestMetaPath, true);
            }
            else if (File.Exists(_dummyTestMetaPath))
            {
                File.Delete(_dummyTestMetaPath);
            }
        }
        catch { }
    }

    public async Task DisposeAsync()
    {
        RestoreOriginalDummyTest();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }

        // Stop Unity instance cleanly after all tests finish
        if (_sharedClient != null)
        {
            try
            {
                await _sharedClient.CallToolAsync("unity_stop", timeout: TimeSpan.FromSeconds(15));
                await _sharedClient.DisposeAsync();
            }
            catch { }
        }
    }

    private sealed class DummyTestScope : IAsyncDisposable
    {
        private readonly UnityIntegrationFixture _fixture;
        private bool _disposed;

        public DummyTestScope(UnityIntegrationFixture fixture)
        {
            _fixture = fixture;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _fixture.RestoreOriginalDummyTest();

            try
            {
                await _fixture.SharedClient.CallToolAsync("unity_refresh");
            }
            catch { }
        }
    }
}

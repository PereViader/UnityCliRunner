using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

public class InstallerTests
{
    private static string FindRepositoryRoot(string assetsPath)
    {
        var dir = new DirectoryInfo(assetsPath);
        while (dir != null)
        {
            string gitDir = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitDir) || File.Exists(gitDir))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(assetsPath, ".."));
    }

    private static string GetRelativeProjectPath(string rootFolder, string unityProjectFolder)
    {
        string fullRoot = Path.GetFullPath(rootFolder).TrimEnd('/', '\\');
        string fullProject = Path.GetFullPath(unityProjectFolder).TrimEnd('/', '\\');
        if (string.Equals(fullRoot, fullProject, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        string rel = MakeRelativePath(fullRoot, fullProject).Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(rel) || rel == "." || rel == "./")
        {
            return "";
        }
        return rel.TrimEnd('/');
    }

    private static void InstallLauncher(string rootFolder, string? templateFilePath = null)
    {
        string unityCliDir = Path.Combine(rootFolder, ".unity-cli");
        if (!Directory.Exists(unityCliDir))
        {
            Directory.CreateDirectory(unityCliDir);
        }

        string launcherPath = Path.Combine(unityCliDir, "Launcher.cs");
        if (!string.IsNullOrEmpty(templateFilePath) && File.Exists(templateFilePath))
        {
            if (!File.Exists(launcherPath) || File.ReadAllText(launcherPath, System.Text.Encoding.UTF8) != File.ReadAllText(templateFilePath, System.Text.Encoding.UTF8))
            {
                File.Copy(templateFilePath, launcherPath, overwrite: true);
            }
        }

        string legacyLauncherDir = Path.Combine(unityCliDir, "launcher");
        if (Directory.Exists(legacyLauncherDir))
        {
            try { Directory.Delete(legacyLauncherDir, true); } catch { }
        }
    }

    private static string BuildVsCodeSnippet(string relProjectPath)
    {
        string projectArg = string.IsNullOrEmpty(relProjectPath)
            ? "${workspaceFolder}"
            : "${workspaceFolder}/" + relProjectPath.TrimStart('/');

        return $$"""
            "unity-cli": {
              "command": "dotnet",
              "args": [
                "run",
                "--file",
                "${workspaceFolder}/.unity-cli/Launcher.cs",
                "--",
                "--project",
                "{{projectArg}}"
              ]
            }
        """;
    }

    private static string BuildCursorSnippet(string relProjectPath)
    {
        string projectArg = string.IsNullOrEmpty(relProjectPath)
            ? "${workspaceFolder}"
            : "${workspaceFolder}/" + relProjectPath.TrimStart('/');

        return $$"""
            "unity-cli": {
              "command": "dotnet",
              "args": [
                "run",
                "--file",
                "${workspaceFolder}/.unity-cli/Launcher.cs",
                "--",
                "--project",
                "{{projectArg}}"
              ]
            }
        """;
    }

    private static string BuildClaudeCodeSnippet(string relProjectPath)
    {
        string projectArg = string.IsNullOrEmpty(relProjectPath)
            ? "${CLAUDE_PROJECT_DIR:-.}"
            : "${CLAUDE_PROJECT_DIR:-.}/" + relProjectPath.TrimStart('/');

        return $$"""
            "unity-cli": {
              "command": "dotnet",
              "args": [
                "run",
                "--file",
                "${CLAUDE_PROJECT_DIR:-.}/.unity-cli/Launcher.cs",
                "--",
                "--project",
                "{{projectArg}}"
              ]
            }
        """;
    }

    private static string BuildAntigravitySnippet(string launcherPath, string fullProjectPath)
    {
        string formattedLauncherPath = launcherPath.Replace('\\', '/');
        string formattedProjectPath = fullProjectPath.Replace('\\', '/');

        return $$"""
            "unity-cli": {
              "command": "dotnet",
              "args": [
                "run",
                "--file",
                "{{formattedLauncherPath}}",
                "--",
                "--project",
                "{{formattedProjectPath}}"
              ]
            }
        """;
    }

    private static void UpdateOrWriteVsCodeConfig(string configPath, string relProjectPath)
    {
        UpdateOrWriteServerConfig(configPath, "servers", BuildVsCodeSnippet(relProjectPath));
    }

    private static void UpdateOrWriteCursorConfig(string configPath, string relProjectPath)
    {
        UpdateOrWriteServerConfig(configPath, "mcpServers", BuildCursorSnippet(relProjectPath));
    }

    private static void UpdateOrWriteClaudeCodeConfig(string configPath, string relProjectPath)
    {
        UpdateOrWriteServerConfig(configPath, "mcpServers", BuildClaudeCodeSnippet(relProjectPath));
    }

    private static void UpdateOrWriteAntigravityConfig(string configPath, string launcherPath, string fullProjectPath)
    {
        UpdateOrWriteServerConfig(configPath, "mcpServers", BuildAntigravitySnippet(launcherPath, fullProjectPath));
    }

    private static void UpdateOrWriteServerConfig(string configPath, string rootKey, string serverSnippet)
    {
        string dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!File.Exists(configPath))
        {
            string newContent = $$"""
            {
              "{{rootKey}}": {
            {{serverSnippet}}
              }
            }

            """;
            File.WriteAllText(configPath, newContent, System.Text.Encoding.UTF8);
            return;
        }

        string existing = File.ReadAllText(configPath, System.Text.Encoding.UTF8).Trim();
        if (string.IsNullOrWhiteSpace(existing))
        {
            string newContent = $$"""
            {
              "{{rootKey}}": {
            {{serverSnippet}}
              }
            }

            """;
            File.WriteAllText(configPath, newContent, System.Text.Encoding.UTF8);
            return;
        }

        if (existing.Contains("\"unity-cli\""))
        {
            int unityIndex = existing.IndexOf("\"unity-cli\"", StringComparison.Ordinal);
            int openBrace = existing.IndexOf('{', unityIndex);
            if (openBrace != -1)
            {
                int depth = 1;
                int closeBrace = -1;
                for (int i = openBrace + 1; i < existing.Length; i++)
                {
                    if (existing[i] == '{') depth++;
                    else if (existing[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeBrace = i;
                            break;
                        }
                    }
                }

                if (closeBrace != -1)
                {
                    string before = existing.Substring(0, unityIndex);
                    string after = existing.Substring(closeBrace + 1);
                    string updated = before + serverSnippet.TrimStart() + after;
                    File.WriteAllText(configPath, updated, System.Text.Encoding.UTF8);
                    return;
                }
            }
        }

        int sectionIndex = existing.IndexOf($"\"{rootKey}\"", StringComparison.Ordinal);
        if (sectionIndex == -1 && rootKey == "servers")
        {
            sectionIndex = existing.IndexOf("\"mcpServers\"", StringComparison.Ordinal);
        }

        if (sectionIndex != -1)
        {
            int openBrace = existing.IndexOf('{', sectionIndex);
            if (openBrace != -1)
            {
                string before = existing.Substring(0, openBrace + 1);
                string after = existing.Substring(openBrace + 1);
                string separator = after.TrimStart().StartsWith("}") ? "\n" : ",\n";
                string updated = before + "\n" + serverSnippet + separator + after.TrimStart();
                File.WriteAllText(configPath, updated, System.Text.Encoding.UTF8);
                return;
            }
        }

        string fallbackContent = $$"""
            {
              "{{rootKey}}": {
            {{serverSnippet}}
              }
            }

            """;
        File.WriteAllText(configPath, fallbackContent, System.Text.Encoding.UTF8);
    }

    private static void AppendCodexMcpConfig(string configPath, string launcherPath, string fullProjectPath)
    {
        string dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string formattedLauncherPath = launcherPath.Replace('\\', '/');
        string formattedProjectPath = fullProjectPath.Replace('\\', '/');

        string codexTomlSnippet = $$"""
            [mcp_servers.unity-cli]
            command = "dotnet"
            args = ["run", "--file", "{{formattedLauncherPath}}", "--", "--project", "{{formattedProjectPath}}"]

            """;

        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, codexTomlSnippet, System.Text.Encoding.UTF8);
            return;
        }

        string existing = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
        const string targetHeader = "[mcp_servers.unity-cli]";
        int headerIndex = existing.IndexOf(targetHeader, StringComparison.Ordinal);

        if (headerIndex != -1)
        {
            int nextSectionIndex = -1;
            var nextMatch = System.Text.RegularExpressions.Regex.Match(
                existing.Substring(headerIndex + targetHeader.Length),
                @"(?m)^\[");
            if (nextMatch.Success)
            {
                nextSectionIndex = headerIndex + targetHeader.Length + nextMatch.Index;
            }

            string before = existing.Substring(0, headerIndex);
            string after = nextSectionIndex != -1 ? existing.Substring(nextSectionIndex) : string.Empty;

            var sbReplace = new System.Text.StringBuilder();
            sbReplace.Append(before);
            sbReplace.Append(codexTomlSnippet);
            if (!string.IsNullOrEmpty(after))
            {
                if (!sbReplace.ToString().EndsWith("\n\n"))
                {
                    sbReplace.AppendLine();
                }
                sbReplace.Append(after.TrimStart('\r', '\n'));
            }

            string newText = sbReplace.ToString();
            if (newText != existing)
            {
                File.WriteAllText(configPath, newText, System.Text.Encoding.UTF8);
            }
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(existing);
        if (!existing.EndsWith("\n"))
        {
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.Append(codexTomlSnippet);
        File.WriteAllText(configPath, sb.ToString(), System.Text.Encoding.UTF8);
    }

    private static string MakeRelativePath(string fromPath, string toPath)
    {
        var fromUri = new Uri(AppendSlash(Path.GetFullPath(fromPath)));
        var toUri = new Uri(Path.GetFullPath(toPath));
        if (fromUri.Scheme != toUri.Scheme)
        {
            return toPath;
        }
        var relativeUri = fromUri.MakeRelativeUri(toUri);
        string relPath = Uri.UnescapeDataString(relativeUri.ToString());
        return relPath.Replace('\\', '/');
    }

    private static string AppendSlash(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string? FindMcpDll(string unityProjectPath)
    {
        string[] embeddedPaths = new[]
        {
            Path.Combine(unityProjectPath, "Packages", "com.pereviader.unityclirunner", "MCP~", "UnityCliRunner.Mcp.dll"),
            Path.Combine(unityProjectPath, "Packages", "com.pereviader.unityclirunner", "MCP", "UnityCliRunner.Mcp.dll")
        };
        foreach (var p in embeddedPaths)
        {
            if (File.Exists(p)) return p;
        }

        string packageCache = Path.Combine(unityProjectPath, "Library", "PackageCache");
        if (Directory.Exists(packageCache))
        {
            try
            {
                var matchingDirs = new DirectoryInfo(packageCache)
                    .GetDirectories("com.pereviader.unityclirunner*")
                    .OrderByDescending(d => d.LastWriteTimeUtc);

                foreach (var d in matchingDirs)
                {
                    string dll1 = Path.Combine(d.FullName, "MCP~", "UnityCliRunner.Mcp.dll");
                    if (File.Exists(dll1)) return dll1;

                    string dll2 = Path.Combine(d.FullName, "MCP", "UnityCliRunner.Mcp.dll");
                    if (File.Exists(dll2)) return dll2;
                }
            }
            catch { }
        }

        return null;
    }

    [Fact]
    public void FindRepositoryRoot_WhenGitFolderExistsInParent_FindsGitFolderRoot()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_repo_" + Guid.NewGuid().ToString("N"));
        try
        {
            string gitDir = Path.Combine(tempBase, ".git");
            string unityDir = Path.Combine(tempBase, "src", "UnityProject");
            string assetsDir = Path.Combine(unityDir, "Assets");
            Directory.CreateDirectory(gitDir);
            Directory.CreateDirectory(assetsDir);

            string detected = FindRepositoryRoot(assetsDir);
            Assert.Equal(Path.GetFullPath(tempBase), Path.GetFullPath(detected));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void FindRepositoryRoot_WhenNoGitFolder_FallsBackToUnityProjectRoot()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_nogit_" + Guid.NewGuid().ToString("N"));
        try
        {
            string unityDir = Path.Combine(tempBase, "MyUnityProject");
            string assetsDir = Path.Combine(unityDir, "Assets");
            Directory.CreateDirectory(assetsDir);

            string detected = FindRepositoryRoot(assetsDir);
            Assert.Equal(Path.GetFullPath(unityDir), Path.GetFullPath(detected));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void GetRelativeProjectPath_WhenSameFolder_ReturnsEmpty()
    {
        string repoRoot = "C:/repo";
        string unityProject = "C:/repo";
        Assert.Equal("", GetRelativeProjectPath(repoRoot, unityProject));
    }

    [Fact]
    public void GetRelativeProjectPath_WhenSubfolder_ReturnsRelativePath()
    {
        string repoRoot = "C:/repo";
        string unityProject = "C:/repo/src/UnityProject";
        Assert.Equal("src/UnityProject", GetRelativeProjectPath(repoRoot, unityProject));
    }

    [Fact]
    public void InstallLauncher_CopiesLauncherTemplate()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_launcher_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempBase, "Launcher~"));
            string templateFile = Path.Combine(tempBase, "Launcher~", "Launcher.cs");
            File.WriteAllText(templateFile, "// Launcher template test content");

            InstallLauncher(tempBase, templateFile);

            string launcherFile = Path.Combine(tempBase, ".unity-cli", "Launcher.cs");
            Assert.True(File.Exists(launcherFile));
            Assert.Equal("// Launcher template test content", File.ReadAllText(launcherFile));
            Assert.False(Directory.Exists(Path.Combine(tempBase, ".unity-cli", "launcher")));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteVsCodeConfig_CreatesServersSectionWithWorkspaceFolder()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_vscode_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".vscode", "mcp.json");
            UpdateOrWriteVsCodeConfig(configFile, "src/UnityProject");

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("servers", out var servers));
            var server = servers.GetProperty("unity-cli");

            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("run", args);
            Assert.Contains("--file", args);
            Assert.Contains("${workspaceFolder}/.unity-cli/Launcher.cs", args);
            Assert.Contains("${workspaceFolder}/src/UnityProject", args);
            Assert.False(server.TryGetProperty("cwd", out _));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteCursorConfig_CreatesMcpServersSectionWithWorkspaceFolder()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_cursor_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".cursor", "mcp.json");
            UpdateOrWriteCursorConfig(configFile, "src/UnityProject");

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("mcpServers", out var servers));
            var server = servers.GetProperty("unity-cli");

            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("run", args);
            Assert.Contains("--file", args);
            Assert.Contains("${workspaceFolder}/.unity-cli/Launcher.cs", args);
            Assert.Contains("${workspaceFolder}/src/UnityProject", args);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteClaudeCodeConfig_UsesClaudeProjectDirSubstitution()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_claude_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".mcp.json");
            UpdateOrWriteClaudeCodeConfig(configFile, "src/UnityProject");

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("mcpServers", out var servers));
            var server = servers.GetProperty("unity-cli");

            var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("run", args);
            Assert.Contains("--file", args);
            Assert.Contains("${CLAUDE_PROJECT_DIR:-.}/.unity-cli/Launcher.cs", args);
            Assert.Contains("${CLAUDE_PROJECT_DIR:-.}/src/UnityProject", args);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteAntigravityConfig_UsesLauncherAndProjectArgs()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_agy_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".agents", "plugins", "unity-cli", "mcp_config.json");
            string launcherPath = "C:/MyRepo/.unity-cli/Launcher.cs";
            string projectPath = "C:/MyRepo/src/UnityProject";

            UpdateOrWriteAntigravityConfig(configFile, launcherPath, projectPath);

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);

            using var doc = JsonDocument.Parse(content);
            var server = doc.RootElement.GetProperty("mcpServers").GetProperty("unity-cli");

            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("run", args);
            Assert.Contains("--file", args);
            Assert.Contains(launcherPath, args);
            Assert.Contains(projectPath, args);
            Assert.False(server.TryGetProperty("cwd", out _));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteVsCodeConfig_WhenExistingFileHasOtherServers_PreservesOtherServers()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_vscode_preserve_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".vscode", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);

            string existingContent = """
                {
                  "servers": {
                    "other-server": {
                      "command": "node",
                      "args": ["index.js"]
                    }
                  }
                }
                """;
            File.WriteAllText(configFile, existingContent);

            UpdateOrWriteVsCodeConfig(configFile, "");

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("servers");

            Assert.True(servers.TryGetProperty("other-server", out var otherServer));
            Assert.Equal("node", otherServer.GetProperty("command").GetString());

            Assert.True(servers.TryGetProperty("unity-cli", out var unityCli));
            Assert.Equal("dotnet", unityCli.GetProperty("command").GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void FindMcpDll_WhenEmbeddedPackageExists_FindsDll()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_find_embedded_" + Guid.NewGuid().ToString("N"));
        try
        {
            string mcpDir = Path.Combine(tempBase, "Packages", "com.pereviader.unityclirunner", "MCP~");
            Directory.CreateDirectory(mcpDir);
            string dll = Path.Combine(mcpDir, "UnityCliRunner.Mcp.dll");
            File.WriteAllText(dll, "fake dll");

            string? found = FindMcpDll(tempBase);
            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(dll), Path.GetFullPath(found));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void FindMcpDll_WhenInPackageCacheWithFingerprint_FindsDll()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_find_cache_" + Guid.NewGuid().ToString("N"));
        try
        {
            string cacheFolder = Path.Combine(tempBase, "Library", "PackageCache", "com.pereviader.unityclirunner@a1b2c3d4", "MCP~");
            Directory.CreateDirectory(cacheFolder);
            string dll = Path.Combine(cacheFolder, "UnityCliRunner.Mcp.dll");
            File.WriteAllText(dll, "fake dll");

            string? found = FindMcpDll(tempBase);
            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(dll), Path.GetFullPath(found));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void FindMcpDll_WhenMultipleFingerprintsExist_FindsNewestByWriteTime()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_find_multi_" + Guid.NewGuid().ToString("N"));
        try
        {
            string olderFolder = Path.Combine(tempBase, "Library", "PackageCache", "com.pereviader.unityclirunner@old111", "MCP~");
            Directory.CreateDirectory(olderFolder);
            string oldDll = Path.Combine(olderFolder, "UnityCliRunner.Mcp.dll");
            File.WriteAllText(oldDll, "old dll");
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(olderFolder)!, DateTime.UtcNow.AddHours(-2));

            string newerFolder = Path.Combine(tempBase, "Library", "PackageCache", "com.pereviader.unityclirunner@new222", "MCP~");
            Directory.CreateDirectory(newerFolder);
            string newDll = Path.Combine(newerFolder, "UnityCliRunner.Mcp.dll");
            File.WriteAllText(newDll, "new dll");
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(newerFolder)!, DateTime.UtcNow);

            string? found = FindMcpDll(tempBase);
            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(newDll), Path.GetFullPath(found));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void FindMcpDll_WhenNotRestored_ReturnsNull()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_find_none_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempBase);
            string? found = FindMcpDll(tempBase);
            Assert.Null(found);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void AppendCodexMcpConfig_WhenFileDoesNotExist_CreatesFile()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            string launcherPath = "C:/MyRepo/.unity-cli/Launcher.cs";
            string projectPath = "C:/MyRepo/src/UnityProject";

            AppendCodexMcpConfig(configFile, launcherPath, projectPath);

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);
            Assert.Contains("[mcp_servers.unity-cli]", content);
            Assert.Contains("command = \"dotnet\"", content);
            Assert.Contains($"args = [\"run\", \"--file\", \"{launcherPath}\", \"--\", \"--project\", \"{projectPath}\"]", content);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void AppendCodexMcpConfig_WhenAlreadyExists_ReplacesSectionAndPreservesSurroundings()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            string initialContent = """
                [general]
                project = "demo"

                [mcp_servers.unity-cli]
                command = "dotnet"
                args = ["run", "--file", "C:/OldPath/.unity-cli/Launcher.cs", "--", "--project", "C:/OldPath"]

                [other_section]
                key = "value"

                """;
            File.WriteAllText(configFile, initialContent);

            string newLauncherPath = "C:/NewRepo/.unity-cli/Launcher.cs";
            string newProjectPath = "C:/NewRepo/src/UnityProject";
            AppendCodexMcpConfig(configFile, newLauncherPath, newProjectPath);

            string updated = File.ReadAllText(configFile);
            Assert.Contains("[general]\nproject = \"demo\"", updated);
            Assert.Contains("[other_section]\nkey = \"value\"", updated);
            Assert.Contains(newLauncherPath, updated);
            Assert.Contains(newProjectPath, updated);
            Assert.DoesNotContain("C:/OldPath", updated);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void AppendCodexMcpConfig_WhenExactSameConfig_LeavesContentUnchanged()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            string launcherPath = "C:/SameRepo/.unity-cli/Launcher.cs";
            string projectPath = "C:/SameRepo/src/UnityProject";
            AppendCodexMcpConfig(configFile, launcherPath, projectPath);
            string original = File.ReadAllText(configFile);

            // Re-run with same configuration
            AppendCodexMcpConfig(configFile, launcherPath, projectPath);
            string second = File.ReadAllText(configFile);

            Assert.Equal(original, second);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }
}

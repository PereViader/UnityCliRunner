using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace UnityCliRunner.Mcp.Tests;

public class InstallerTests
{
    // Replicates UnityCliInstaller.FindRepositoryRoot
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

    // Replicates UnityCliInstaller.UpdateOrWriteMcpConfig
    private static void UpdateOrWriteMcpConfig(string configPath, string mcpDir)
    {
        string dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string formattedMcpDir = mcpDir.Replace('\\', '/');
        if (!formattedMcpDir.EndsWith("/"))
        {
            formattedMcpDir += "/";
        }

        string serverJsonSnippet =
            "    \"unity-cli\": {\n" +
            "      \"command\": \"dotnet\",\n" +
            "      \"args\": [\n" +
            "        \"UnityCliRunner.Mcp.dll\"\n" +
            "      ],\n" +
            $"      \"cwd\": \"{formattedMcpDir}\"\n" +
            "    }";

        if (!File.Exists(configPath))
        {
            string newContent =
                "{\n" +
                "  \"mcpServers\": {\n" +
                serverJsonSnippet.TrimStart() + "\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(configPath, newContent, System.Text.Encoding.UTF8);
            return;
        }

        string existing = File.ReadAllText(configPath, System.Text.Encoding.UTF8).Trim();
        if (string.IsNullOrWhiteSpace(existing) || !existing.Contains("mcpServers"))
        {
            string newContent =
                "{\n" +
                "  \"mcpServers\": {\n" +
                serverJsonSnippet.TrimStart() + "\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(configPath, newContent, System.Text.Encoding.UTF8);
            return;
        }

        // If unity-cli already exists, replace its block; otherwise insert into mcpServers
        if (existing.Contains("\"unity-cli\""))
        {
            // Replace unity-cli block
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
                    string updated = before + serverJsonSnippet.TrimStart() + after;
                    File.WriteAllText(configPath, updated, System.Text.Encoding.UTF8);
                    return;
                }
            }
        }

        // Insert into mcpServers
        int mcpServersIndex = existing.IndexOf("\"mcpServers\"", StringComparison.Ordinal);
        int mcpOpenBrace = existing.IndexOf('{', mcpServersIndex);
        if (mcpOpenBrace != -1)
        {
            string before = existing.Substring(0, mcpOpenBrace + 1);
            string after = existing.Substring(mcpOpenBrace + 1);
            string separator = after.TrimStart().StartsWith("}") ? "\n" : ",\n";
            string updated = before + "\n" + serverJsonSnippet + separator + after.TrimStart();
            File.WriteAllText(configPath, updated, System.Text.Encoding.UTF8);
            return;
        }

        File.WriteAllText(configPath, existing, System.Text.Encoding.UTF8);
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
    public void UpdateOrWriteMcpConfig_CreatesNewFileWithCwdAndArgs()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".vscode", "mcp.json");
            string mcpDir = "C:/MyPackages/UnityCliRunner/MCP~/";

            UpdateOrWriteMcpConfig(configFile, mcpDir);

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var server = root.GetProperty("mcpServers").GetProperty("unity-cli");

            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            Assert.Equal("UnityCliRunner.Mcp.dll", server.GetProperty("args")[0].GetString());
            Assert.Equal(mcpDir, server.GetProperty("cwd").GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenExistingFileHasOtherServers_PreservesOtherServers()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);

            string existingContent =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"other-server\": {\n" +
                "      \"command\": \"node\",\n" +
                "      \"args\": [\"index.js\"]\n" +
                "    }\n" +
                "  }\n" +
                "}";
            File.WriteAllText(configFile, existingContent);

            string mcpDir = "C:/Package/MCP~/";
            UpdateOrWriteMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("mcpServers");

            // Both other-server and unity-cli must exist
            Assert.True(servers.TryGetProperty("other-server", out var otherServer));
            Assert.Equal("node", otherServer.GetProperty("command").GetString());

            Assert.True(servers.TryGetProperty("unity-cli", out var unityCli));
            Assert.Equal("dotnet", unityCli.GetProperty("command").GetString());
            Assert.Equal(mcpDir, unityCli.GetProperty("cwd").GetString());
            Assert.Equal("UnityCliRunner.Mcp.dll", unityCli.GetProperty("args")[0].GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void UpdateOrWriteMcpConfig_WhenUnityCliAlreadyExists_UpdatesCwdAndArgs()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_mcp_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".claude", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);

            string existingContent =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"unity-cli\": {\n" +
                "      \"command\": \"dotnet\",\n" +
                "      \"args\": [\"old_path/UnityCliRunner.Mcp.dll\"]\n" +
                "    },\n" +
                "    \"github\": {\n" +
                "      \"command\": \"gh\"\n" +
                "    }\n" +
                "  }\n" +
                "}";
            File.WriteAllText(configFile, existingContent);

            string mcpDir = "C:/NewPackagePath/MCP~/";
            UpdateOrWriteMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(content);
            var servers = doc.RootElement.GetProperty("mcpServers");

            Assert.True(servers.TryGetProperty("github", out _));
            Assert.True(servers.TryGetProperty("unity-cli", out var unityCli));
            Assert.Equal(mcpDir, unityCli.GetProperty("cwd").GetString());
            Assert.Equal("UnityCliRunner.Mcp.dll", unityCli.GetProperty("args")[0].GetString());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    private static void AppendCodexMcpConfig(string configPath, string mcpDir)
    {
        string dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string formattedMcpDir = mcpDir.Replace('\\', '/');
        if (!formattedMcpDir.EndsWith("/"))
        {
            formattedMcpDir += "/";
        }

        string codexTomlSnippet =
            "[mcp_servers.unity-cli]\n" +
            "command = \"dotnet\"\n" +
            "args = [\"UnityCliRunner.Mcp.dll\"]\n" +
            $"cwd = \"{formattedMcpDir}\"\n";

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

    [Fact]
    public void AppendCodexMcpConfig_WhenFileDoesNotExist_CreatesFile()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            string mcpDir = "C:/Packages/UnityCliRunner/MCP~/";

            AppendCodexMcpConfig(configFile, mcpDir);

            Assert.True(File.Exists(configFile));
            string content = File.ReadAllText(configFile);
            Assert.Contains("[mcp_servers.unity-cli]", content);
            Assert.Contains("command = \"dotnet\"", content);
            Assert.Contains("args = [\"UnityCliRunner.Mcp.dll\"]", content);
            Assert.Contains($"cwd = \"{mcpDir}\"", content);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void AppendCodexMcpConfig_WhenFileExists_AppendsAtEnd()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "test_codex_" + Guid.NewGuid().ToString("N"));
        try
        {
            string configFile = Path.Combine(tempBase, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            File.WriteAllText(configFile, "[model]\nname = \"o3-mini\"\n");

            string mcpDir = "C:/Packages/UnityCliRunner/MCP~/";
            AppendCodexMcpConfig(configFile, mcpDir);

            string content = File.ReadAllText(configFile);
            Assert.StartsWith("[model]\nname = \"o3-mini\"", content);
            Assert.Contains("[mcp_servers.unity-cli]", content);
            Assert.Contains($"cwd = \"{mcpDir}\"", content);
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
            string initialContent =
                "[general]\n" +
                "project = \"demo\"\n\n" +
                "[mcp_servers.unity-cli]\n" +
                "command = \"dotnet\"\n" +
                "args = [\"old_UnityCliRunner.Mcp.dll\"]\n" +
                "cwd = \"C:/OldPath/\"\n\n" +
                "[other_section]\n" +
                "key = \"value\"\n";
            File.WriteAllText(configFile, initialContent);

            string newMcpDir = "C:/NewPackagePath/MCP~/";
            AppendCodexMcpConfig(configFile, newMcpDir);

            string updated = File.ReadAllText(configFile);
            Assert.Contains("[general]\nproject = \"demo\"", updated);
            Assert.Contains("[other_section]\nkey = \"value\"", updated);
            Assert.Contains($"cwd = \"{newMcpDir}\"", updated);
            Assert.DoesNotContain("C:/OldPath/", updated);
            Assert.DoesNotContain("old_UnityCliRunner.Mcp.dll", updated);
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
            string mcpDir = "C:/SamePath/MCP~/";
            AppendCodexMcpConfig(configFile, mcpDir);
            string original = File.ReadAllText(configFile);
            var writeTime = File.GetLastWriteTimeUtc(configFile);

            // Re-run with same configuration
            AppendCodexMcpConfig(configFile, mcpDir);
            string second = File.ReadAllText(configFile);

            Assert.Equal(original, second);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }
}


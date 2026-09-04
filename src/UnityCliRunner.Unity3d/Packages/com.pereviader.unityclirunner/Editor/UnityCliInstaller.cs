using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    public static class UnityCliInstaller
    {
        [MenuItem("Tools/UnityCliRunner/Install MCP Configurations")]
        public static void InstallMcpConfigurations()
        {
            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityCliInstaller).Assembly);
                if (packageInfo == null)
                {
                    Debug.LogError("[UnityCliRunner] Could not find package info for assembly.");
                    EditorUtility.DisplayDialog("UnityCliRunner Error", "Could not find package information for assembly. Installation aborted.", "OK");
                    return;
                }

                string assetsPath = Application.dataPath;
                string rootFolder = FindRepositoryRoot(assetsPath);
                string packagePath = packageInfo.resolvedPath;
                string mcpDir = Path.GetFullPath(Path.Combine(packagePath, "MCP~")).Replace('\\', '/');
                if (!mcpDir.EndsWith("/"))
                {
                    mcpDir += "/";
                }

                string[] targetConfigs = new[]
                {
                    Path.Combine(rootFolder, ".agents", "plugins", "unity-cli", "mcp_config.json"),
                    Path.Combine(rootFolder, ".vscode", "mcp.json"),
                    Path.Combine(rootFolder, ".cursor", "mcp.json"),
                    Path.Combine(rootFolder, ".claude", "mcp.json"),
                    Path.Combine(rootFolder, ".mcp.json")
                };

                string pluginJsonPath = Path.Combine(rootFolder, ".agents", "plugins", "unity-cli", "plugin.json");
                if (!File.Exists(pluginJsonPath))
                {
                    string pluginDir = Path.GetDirectoryName(pluginJsonPath);
                    if (!Directory.Exists(pluginDir)) Directory.CreateDirectory(pluginDir);
                    File.WriteAllText(pluginJsonPath, "{\n  \"name\": \"unity-cli\"\n}\n", Encoding.UTF8);
                }

                var sb = new StringBuilder();
                sb.AppendLine("Installed MCP configuration to:");

                foreach (string configPath in targetConfigs)
                {
                    UpdateOrWriteMcpConfig(configPath, mcpDir);
                    sb.AppendLine($"• {MakeRelativePath(rootFolder, configPath).Replace('\\', '/')}");
                }

                string codexConfigPath = Path.Combine(rootFolder, ".codex", "config.toml");
                AppendCodexMcpConfig(codexConfigPath, mcpDir);
                sb.AppendLine($"• {MakeRelativePath(rootFolder, codexConfigPath).Replace('\\', '/')}");

                Debug.Log($"[UnityCliRunner] {sb}");
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("UnityCliRunner Success", sb.ToString(), "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliRunner] Failed to install MCP configurations: {ex.Message}");
                Debug.LogException(ex);
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("UnityCliRunner Error", $"Failed to install MCP configurations:\n{ex.Message}", "OK");
                }
            }
        }

        public static string FindRepositoryRoot(string assetsPath)
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

        public static void UpdateOrWriteMcpConfig(string configPath, string mcpDir)
        {
            string dir = Path.GetDirectoryName(configPath);
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
                File.WriteAllText(configPath, newContent, Encoding.UTF8);
                return;
            }

            string existing = File.ReadAllText(configPath, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(existing) || !existing.Contains("mcpServers"))
            {
                string newContent =
                    "{\n" +
                    "  \"mcpServers\": {\n" +
                    serverJsonSnippet.TrimStart() + "\n" +
                    "  }\n" +
                    "}\n";
                File.WriteAllText(configPath, newContent, Encoding.UTF8);
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
                        File.WriteAllText(configPath, updated, Encoding.UTF8);
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
                File.WriteAllText(configPath, updated, Encoding.UTF8);
                return;
            }

            File.WriteAllText(configPath, existing, Encoding.UTF8);
        }

        public static void AppendCodexMcpConfig(string configPath, string mcpDir)
        {
            string dir = Path.GetDirectoryName(configPath);
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
                File.WriteAllText(configPath, codexTomlSnippet, Encoding.UTF8);
                return;
            }

            string existing = File.ReadAllText(configPath, Encoding.UTF8);
            const string targetHeader = "[mcp_servers.unity-cli]";
            int headerIndex = existing.IndexOf(targetHeader, StringComparison.Ordinal);

            if (headerIndex != -1)
            {
                // Find where this section ends: either the next section header starting with '[' on a line, or end of text.
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

                var sbReplace = new StringBuilder();
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
                    File.WriteAllText(configPath, newText, Encoding.UTF8);
                }
                return;
            }

            var sb = new StringBuilder();
            sb.Append(existing);
            if (!existing.EndsWith("\n"))
            {
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.Append(codexTomlSnippet);
            File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
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
    }
}

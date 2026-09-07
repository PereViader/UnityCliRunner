using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    public static class UnityCliInstaller
    {
        [InitializeOnLoadMethod]
        private static void OnEditorLoaded()
        {
            try
            {
                string assetsPath = Application.dataPath;
                string rootFolder = FindRepositoryRoot(assetsPath);
                string launcherPath = Path.Combine(rootFolder, ".unity-cli", "Launcher.cs");
                if (!File.Exists(launcherPath))
                {
                    InstallLauncher(rootFolder);
                }
            }
            catch { }
        }

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

                string unityProjectFolder = Path.GetFullPath(Path.Combine(assetsPath, ".."));
                string relProjectPath = GetRelativeProjectPath(rootFolder, unityProjectFolder);

                // 1. Install repository launcher
                InstallLauncher(rootFolder, packagePath);

                // 2. Ensure Antigravity plugin manifest
                string pluginJsonPath = Path.Combine(rootFolder, ".agents", "plugins", "unity-cli", "plugin.json");
                if (!File.Exists(pluginJsonPath))
                {
                    string pluginDir = Path.GetDirectoryName(pluginJsonPath);
                    if (!Directory.Exists(pluginDir)) Directory.CreateDirectory(pluginDir);
                    File.WriteAllText(pluginJsonPath, "{\n  \"name\": \"unity-cli\"\n}\n", Encoding.UTF8);
                }

                var sb = new StringBuilder();
                sb.AppendLine("Installed MCP launcher and configurations to:");
                sb.AppendLine($"• {MakeRelativePath(rootFolder, Path.Combine(rootFolder, ".unity-cli", "Launcher.cs")).Replace('\\', '/')}");

                // 3. Update portable configs (VS Code, Cursor, Claude Code)
                string vsCodePath = Path.Combine(rootFolder, ".vscode", "mcp.json");
                UpdateOrWriteVsCodeConfig(vsCodePath, relProjectPath);
                sb.AppendLine($"• {MakeRelativePath(rootFolder, vsCodePath).Replace('\\', '/')}");

                string cursorPath = Path.Combine(rootFolder, ".cursor", "mcp.json");
                UpdateOrWriteCursorConfig(cursorPath, relProjectPath);
                sb.AppendLine($"• {MakeRelativePath(rootFolder, cursorPath).Replace('\\', '/')}");

                string mcpJsonPath = Path.Combine(rootFolder, ".mcp.json");
                UpdateOrWriteClaudeCodeConfig(mcpJsonPath, relProjectPath);
                sb.AppendLine($"• {MakeRelativePath(rootFolder, mcpJsonPath).Replace('\\', '/')}");

                // 4. Update absolute configs (Antigravity, Codex)
                string launcherPath = Path.GetFullPath(Path.Combine(rootFolder, ".unity-cli", "Launcher.cs")).Replace('\\', '/');
                string fullProjectPath = Path.GetFullPath(unityProjectFolder).Replace('\\', '/');

                string agyConfigPath = Path.Combine(rootFolder, ".agents", "plugins", "unity-cli", "mcp_config.json");
                UpdateOrWriteAntigravityConfig(agyConfigPath, launcherPath, fullProjectPath);
                sb.AppendLine($"• {MakeRelativePath(rootFolder, agyConfigPath).Replace('\\', '/')}");

                string codexConfigPath = Path.Combine(rootFolder, ".codex", "config.toml");
                AppendCodexMcpConfig(codexConfigPath, launcherPath, fullProjectPath);
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

        public static string GetRelativeProjectPath(string rootFolder, string unityProjectFolder)
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

        public static string FindLauncherTemplatePath(string packagePath = null)
        {
            if (!string.IsNullOrEmpty(packagePath))
            {
                string p = Path.Combine(packagePath, "Launcher~", "Launcher.cs");
                if (File.Exists(p)) return p;
            }

            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityCliInstaller).Assembly);
                if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
                {
                    string p = Path.Combine(packageInfo.resolvedPath, "Launcher~", "Launcher.cs");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }

            string assetsPath = Application.dataPath;
            string unityProjectFolder = Path.GetFullPath(Path.Combine(assetsPath, ".."));
            string[] searchPaths = new[]
            {
                Path.Combine(unityProjectFolder, "Packages", "com.pereviader.unityclirunner", "Launcher~", "Launcher.cs"),
                Path.Combine(unityProjectFolder, "src", "UnityCliRunner.Unity3d", "Packages", "com.pereviader.unityclirunner", "Launcher~", "Launcher.cs")
            };

            foreach (var sp in searchPaths)
            {
                if (File.Exists(sp)) return sp;
            }

            return null;
        }

        public static void InstallLauncher(string rootFolder, string packagePath = null)
        {
            string unityCliDir = Path.Combine(rootFolder, ".unity-cli");
            if (!Directory.Exists(unityCliDir))
            {
                Directory.CreateDirectory(unityCliDir);
            }

            string launcherPath = Path.Combine(unityCliDir, "Launcher.cs");
            string templatePath = FindLauncherTemplatePath(packagePath);

            if (templatePath != null && File.Exists(templatePath))
            {
                if (!File.Exists(launcherPath) || File.ReadAllText(launcherPath, Encoding.UTF8) != File.ReadAllText(templatePath, Encoding.UTF8))
                {
                    File.Copy(templatePath, launcherPath, overwrite: true);
                }
            }
            else if (!File.Exists(launcherPath))
            {
                Debug.LogWarning("[UnityCliRunner] Launcher template file 'Launcher~/Launcher.cs' could not be found.");
            }

            string legacyLauncherDir = Path.Combine(unityCliDir, "launcher");
            if (Directory.Exists(legacyLauncherDir))
            {
                try { Directory.Delete(legacyLauncherDir, true); } catch { }
            }
        }

        public static string BuildVsCodeSnippet(string relProjectPath)
        {
            string projectArg = string.IsNullOrEmpty(relProjectPath)
                ? "${workspaceFolder}"
                : "${workspaceFolder}/" + relProjectPath.TrimStart('/');

            return $@"    ""unity-cli"": {{
      ""command"": ""dotnet"",
      ""args"": [
        ""run"",
        ""-v"",
        ""q"",
        ""${{workspaceFolder}}/.unity-cli/Launcher.cs"",
        ""--"",
        ""--project"",
        ""{projectArg}""
      ]
    }}";
        }

        public static string BuildCursorSnippet(string relProjectPath)
        {
            string projectArg = string.IsNullOrEmpty(relProjectPath)
                ? "${workspaceFolder}"
                : "${workspaceFolder}/" + relProjectPath.TrimStart('/');

            return $@"    ""unity-cli"": {{
      ""command"": ""dotnet"",
      ""args"": [
        ""run"",
        ""-v"",
        ""q"",
        ""${{workspaceFolder}}/.unity-cli/Launcher.cs"",
        ""--"",
        ""--project"",
        ""{projectArg}""
      ]
    }}";
        }

        public static string BuildClaudeCodeSnippet(string relProjectPath)
        {
            string projectArg = string.IsNullOrEmpty(relProjectPath)
                ? "${CLAUDE_PROJECT_DIR:-.}"
                : "${CLAUDE_PROJECT_DIR:-.}/" + relProjectPath.TrimStart('/');

            return $@"    ""unity-cli"": {{
      ""command"": ""dotnet"",
      ""args"": [
        ""run"",
        ""-v"",
        ""q"",
        ""${{CLAUDE_PROJECT_DIR:-.}}/.unity-cli/Launcher.cs"",
        ""--"",
        ""--project"",
        ""{projectArg}""
      ]
    }}";
        }

        public static string BuildAntigravitySnippet(string launcherPath, string fullProjectPath)
        {
            string formattedLauncherPath = launcherPath.Replace('\\', '/');
            string formattedProjectPath = fullProjectPath.Replace('\\', '/');

            return $@"    ""unity-cli"": {{
      ""command"": ""dotnet"",
      ""args"": [
        ""run"",
        ""-v"",
        ""q"",
        ""{formattedLauncherPath}"",
        ""--"",
        ""--project"",
        ""{formattedProjectPath}""
      ]
    }}";
        }

        public static void UpdateOrWriteVsCodeConfig(string configPath, string relProjectPath)
        {
            UpdateOrWriteServerConfig(configPath, "servers", BuildVsCodeSnippet(relProjectPath));
        }

        public static void UpdateOrWriteCursorConfig(string configPath, string relProjectPath)
        {
            UpdateOrWriteServerConfig(configPath, "mcpServers", BuildCursorSnippet(relProjectPath));
        }

        public static void UpdateOrWriteClaudeCodeConfig(string configPath, string relProjectPath)
        {
            UpdateOrWriteServerConfig(configPath, "mcpServers", BuildClaudeCodeSnippet(relProjectPath));
        }

        public static void UpdateOrWriteAntigravityConfig(string configPath, string launcherPath, string fullProjectPath)
        {
            UpdateOrWriteServerConfig(configPath, "mcpServers", BuildAntigravitySnippet(launcherPath, fullProjectPath));
        }

        public static void UpdateOrWriteServerConfig(string configPath, string rootKey, string serverSnippet)
        {
            string dir = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(configPath))
            {
                string newContent = $@"{{
  ""{rootKey}"": {{
{serverSnippet}
  }}
}}
";
                File.WriteAllText(configPath, newContent, Encoding.UTF8);
                return;
            }

            string existing = File.ReadAllText(configPath, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(existing))
            {
                string newContent = $@"{{
  ""{rootKey}"": {{
{serverSnippet}
  }}
}}
";
                File.WriteAllText(configPath, newContent, Encoding.UTF8);
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
                        File.WriteAllText(configPath, updated, Encoding.UTF8);
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
                    File.WriteAllText(configPath, updated, Encoding.UTF8);
                    return;
                }
            }

            string fallbackContent = $@"{{
  ""{rootKey}"": {{
{serverSnippet}
  }}
}}
";
            File.WriteAllText(configPath, fallbackContent, Encoding.UTF8);
        }

        public static void AppendCodexMcpConfig(string configPath, string launcherPath, string fullProjectPath)
        {
            string dir = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string formattedLauncherPath = launcherPath.Replace('\\', '/');
            string formattedProjectPath = fullProjectPath.Replace('\\', '/');

            string codexTomlSnippet = $@"[mcp_servers.unity-cli]
command = ""dotnet""
args = [""run"", ""-v"", ""q"", ""{formattedLauncherPath}"", ""--"", ""--project"", ""{formattedProjectPath}""]

";


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

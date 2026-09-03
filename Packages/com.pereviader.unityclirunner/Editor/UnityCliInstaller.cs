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

                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string packagePath = packageInfo.resolvedPath;
                string dllPath = Path.Combine(packagePath, "MCP~", "UnityCliRunner.Mcp.dll");

                string relDllPath = MakeRelativePath(projectRoot, dllPath).Replace('\\', '/');

                string[] targetConfigs = new[]
                {
                    Path.Combine(projectRoot, ".agents", "plugins", "unity-cli", "mcp_config.json"),
                    Path.Combine(projectRoot, ".vscode", "mcp.json"),
                    Path.Combine(projectRoot, ".cursor", "mcp.json"),
                    Path.Combine(projectRoot, ".claude", "mcp.json")
                };

                var sb = new StringBuilder();
                sb.AppendLine("Installed MCP configuration to:");

                foreach (string configPath in targetConfigs)
                {
                    UpdateOrWriteMcpConfig(configPath, relDllPath);
                    sb.AppendLine($"• {MakeRelativePath(projectRoot, configPath).Replace('\\', '/')}");
                }

                Debug.Log($"[UnityCliRunner] {sb}");
                EditorUtility.DisplayDialog("UnityCliRunner Success", sb.ToString(), "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliRunner] Failed to install MCP configurations: {ex.Message}");
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("UnityCliRunner Error", $"Failed to install MCP configurations:\n{ex.Message}", "OK");
            }
        }

        private static void UpdateOrWriteMcpConfig(string configPath, string relDllPath)
        {
            string dir = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string serverJsonSnippet =
                "    \"unity-cli\": {\n" +
                "      \"command\": \"dotnet\",\n" +
                "      \"args\": [\n" +
                $"        \"{relDllPath}\"\n" +
                "      ]\n" +
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

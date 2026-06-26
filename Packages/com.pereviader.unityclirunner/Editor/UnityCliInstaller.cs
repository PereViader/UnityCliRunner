using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    public static class UnityCliInstaller
    {
        [MenuItem("Tools/UnityCliRunner/InstallBashScript")]
        public static void InstallBashScript()
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

                string packagePath = packageInfo.resolvedPath;
                string sourcePath = Path.Combine(packagePath, "Templates~", "unitycli.sh");
                
                if (!File.Exists(sourcePath))
                {
                    string errorMsg = $"Source file not found at: {sourcePath}";
                    Debug.LogError($"[UnityCliRunner] {errorMsg}");
                    EditorUtility.DisplayDialog("UnityCliRunner Error", errorMsg, "OK");
                    return;
                }

                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string destPath = Path.Combine(projectRoot, "unitycli.sh");

                File.Copy(sourcePath, destPath, true);
                
                Debug.Log($"[UnityCliRunner] Installed unitycli.sh successfully to: {destPath}");
                EditorUtility.DisplayDialog("UnityCliRunner Success", $"Installed unitycli.sh successfully to:\n{destPath}", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliRunner] Failed to install unitycli.sh: {ex.Message}");
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("UnityCliRunner Error", $"Failed to install unitycli.sh:\n{ex.Message}", "OK");
            }
        }

        [MenuItem("Tools/UnityCliRunner/InstallSkill")]
        public static void InstallSkill()
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

                string packagePath = packageInfo.resolvedPath;
                string sourceDir = Path.Combine(packagePath, "Templates~", ".agents", "skills", "unity-cli");

                if (!Directory.Exists(sourceDir))
                {
                    string errorMsg = $"Source directory not found at: {sourceDir}";
                    Debug.LogError($"[UnityCliRunner] {errorMsg}");
                    EditorUtility.DisplayDialog("UnityCliRunner Error", errorMsg, "OK");
                    return;
                }

                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string destDir = Path.Combine(projectRoot, ".agents", "skills", "unity-cli");

                if (Directory.Exists(destDir))
                {
                    Directory.Delete(destDir, true);
                }

                CopyDirectory(sourceDir, destDir);

                Debug.Log($"[UnityCliRunner] Installed unity-cli skill successfully to: {destDir}");
                EditorUtility.DisplayDialog("UnityCliRunner Success", $"Installed unity-cli skill successfully to:\n{destDir}", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliRunner] Failed to install unity-cli skill: {ex.Message}");
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("UnityCliRunner Error", $"Failed to install unity-cli skill:\n{ex.Message}", "OK");
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, targetFile, true);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, targetSubDir);
            }
        }
    }
}

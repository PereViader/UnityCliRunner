using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    internal static class RoslynCompilerHelper
    {
        private static bool s_Initialized;
        private static bool s_IsSupported;
        private static string s_UnsupportedReason = "";

        private static Assembly s_CodeAnalysisAsm;
        private static Assembly s_CSharpAsm;

        private static MethodInfo s_ParseTextMethod;
        private static MethodInfo s_CreateFromFileMethod;
        private static MethodInfo s_CreateCompMethod;
        private static MethodInfo s_EmitMethod;

        private static Type s_CompilationType;
        private static Type s_CompilationOptionsType;
        private static Type s_SyntaxTreeType;
        private static Type s_MetadataRefType;
        private static object s_CompilationOptions;

        private static List<object> s_CachedMetadataReferences;
        private static readonly object s_Lock = new object();

        public static bool IsSupported
        {
            get
            {
                EnsureInitialized();
                return s_IsSupported;
            }
        }

        public static string UnsupportedReason
        {
            get
            {
                EnsureInitialized();
                return s_UnsupportedReason;
            }
        }

        private static void EnsureInitialized()
        {
            if (s_Initialized) return;

            lock (s_Lock)
            {
                if (s_Initialized) return;
                s_Initialized = true;

                try
                {
                    string dataPath = EditorApplication.applicationContentsPath;
                    if (string.IsNullOrEmpty(dataPath) || !Directory.Exists(dataPath))
                    {
                        s_IsSupported = false;
                        s_UnsupportedReason = "EditorApplication.applicationContentsPath is invalid or does not exist.";
                        return;
                    }

                    string[] candidateDirs = new[]
                    {
                        Path.Combine(dataPath, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn"),
                        Path.Combine(dataPath, "DotNetSdkRoslyn"),
                        Path.Combine(dataPath, "Tools", "Roslyn"),
                        Path.Combine(dataPath, "MonoBleedingEdge", "lib", "mono", "4.5"),
                        Path.Combine(dataPath, "Frameworks", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn")
                    };

                    string roslynDir = null;
                    foreach (var dir in candidateDirs)
                    {
                        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "Microsoft.CodeAnalysis.CSharp.dll")))
                        {
                            // Test if it can be loaded
                            try
                            {
                                var testAsm = Assembly.LoadFrom(Path.Combine(dir, "Microsoft.CodeAnalysis.CSharp.dll"));
                                if (testAsm != null)
                                {
                                    roslynDir = dir;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    if (roslynDir == null)
                    {
                        s_IsSupported = false;
                        s_UnsupportedReason = "Roslyn compiler assemblies (Microsoft.CodeAnalysis.CSharp.dll) could not be found or loaded in the Unity Editor installation.";
                        return;
                    }

                    // Load required dependencies if present in roslynDir
                    string immutablePath = Path.Combine(roslynDir, "System.Collections.Immutable.dll");
                    if (File.Exists(immutablePath))
                    {
                        try { Assembly.LoadFrom(immutablePath); } catch { }
                    }

                    string metadataPath = Path.Combine(roslynDir, "System.Reflection.Metadata.dll");
                    if (File.Exists(metadataPath))
                    {
                        try { Assembly.LoadFrom(metadataPath); } catch { }
                    }

                    s_CodeAnalysisAsm = Assembly.LoadFrom(Path.Combine(roslynDir, "Microsoft.CodeAnalysis.dll"));
                    s_CSharpAsm = Assembly.LoadFrom(Path.Combine(roslynDir, "Microsoft.CodeAnalysis.CSharp.dll"));

                    if (s_CodeAnalysisAsm == null || s_CSharpAsm == null)
                    {
                        s_IsSupported = false;
                        s_UnsupportedReason = "Failed to load Microsoft.CodeAnalysis or Microsoft.CodeAnalysis.CSharp assemblies.";
                        return;
                    }

                    s_SyntaxTreeType = s_CSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                    s_CompilationType = s_CSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
                    s_CompilationOptionsType = s_CSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
                    s_MetadataRefType = s_CodeAnalysisAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
                    var outputKindEnum = s_CodeAnalysisAsm.GetType("Microsoft.CodeAnalysis.OutputKind");

                    if (s_SyntaxTreeType == null || s_CompilationType == null || s_CompilationOptionsType == null || s_MetadataRefType == null || outputKindEnum == null)
                    {
                        s_IsSupported = false;
                        s_UnsupportedReason = "Failed to resolve required Roslyn reflection types.";
                        return;
                    }

                    // ParseText method
                    foreach (var m in s_SyntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name == "ParseText" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string))
                        {
                            s_ParseTextMethod = m;
                            break;
                        }
                    }

                    // CreateFromFile method
                    foreach (var m in s_MetadataRefType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name == "CreateFromFile" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string))
                        {
                            if (s_CreateFromFileMethod == null || m.GetParameters().Length < s_CreateFromFileMethod.GetParameters().Length)
                            {
                                s_CreateFromFileMethod = m;
                            }
                        }
                    }

                    // CSharpCompilationOptions instance
                    object outputKindDynamicallyLinkedLibrary = Enum.Parse(outputKindEnum, "DynamicallyLinkedLibrary");
                    var ctors = s_CompilationOptionsType.GetConstructors();
                    foreach (var ctor in ctors)
                    {
                        var pars = ctor.GetParameters();
                        if (pars.Length >= 1 && pars[0].ParameterType == outputKindEnum)
                        {
                            var args = new object[pars.Length];
                            args[0] = outputKindDynamicallyLinkedLibrary;
                            for (int i = 1; i < pars.Length; i++)
                            {
                                args[i] = pars[i].DefaultValue != DBNull.Value ? pars[i].DefaultValue : null;
                            }
                            try
                            {
                                s_CompilationOptions = ctor.Invoke(args);
                                break;
                            }
                            catch { }
                        }
                    }

                    // CSharpCompilation.Create method
                    foreach (var m in s_CompilationType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name == "Create" && m.GetParameters().Length == 4)
                        {
                            var p = m.GetParameters();
                            if (p[0].ParameterType == typeof(string) && p[3].ParameterType == s_CompilationOptionsType)
                            {
                                s_CreateCompMethod = m;
                                break;
                            }
                        }
                    }

                    // Emit method
                    foreach (var m in s_CompilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name == "Emit" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Stream))
                        {
                            if (s_EmitMethod == null || m.GetParameters().Length == 1)
                            {
                                s_EmitMethod = m;
                                if (m.GetParameters().Length == 1) break;
                            }
                        }
                    }

                    if (s_ParseTextMethod == null || s_CreateFromFileMethod == null || s_CompilationOptions == null || s_CreateCompMethod == null || s_EmitMethod == null)
                    {
                        s_IsSupported = false;
                        s_UnsupportedReason = "Could not bind all required Roslyn methods.";
                        return;
                    }

                    // Build initial metadata references
                    BuildMetadataReferences(dataPath);

                    s_IsSupported = true;
                }
                catch (Exception ex)
                {
                    s_IsSupported = false;
                    s_UnsupportedReason = "Exception initializing Roslyn compiler: " + ex.Message;
                }
            }
        }

        private static void BuildMetadataReferences(string dataPath)
        {
            var refList = new List<object>();
            var addedLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRef(string path)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && addedLocations.Add(path))
                {
                    try
                    {
                        object r;
                        var pars = s_CreateFromFileMethod.GetParameters();
                        if (pars.Length == 1)
                        {
                            r = s_CreateFromFileMethod.Invoke(null, new object[] { path });
                        }
                        else
                        {
                            var args = new object[pars.Length];
                            args[0] = path;
                            for (int i = 1; i < pars.Length; i++)
                            {
                                args[i] = pars[i].DefaultValue != DBNull.Value ? pars[i].DefaultValue : null;
                            }
                            r = s_CreateFromFileMethod.Invoke(null, args);
                        }

                        if (r != null)
                        {
                            refList.Add(r);
                        }
                    }
                    catch { }
                }
            }

            // 1. All currently loaded assemblies in AppDomain
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                    {
                        AddRef(asm.Location);
                    }
                }
                catch { }
            }

            // 2. Core framework types
            try { AddRef(typeof(object).Assembly.Location); } catch { }
            try { AddRef(typeof(System.Linq.Enumerable).Assembly.Location); } catch { }
            try { AddRef(typeof(System.Collections.Generic.List<>).Assembly.Location); } catch { }
            try { AddRef(typeof(UnityEngine.Object).Assembly.Location); } catch { }
            try { AddRef(typeof(UnityEngine.GameObject).Assembly.Location); } catch { }
            try { AddRef(typeof(UnityEditor.Editor).Assembly.Location); } catch { }

            // 3. Unity Reference Assemblies folder
            string refAssembliesDir = Path.Combine(dataPath, "UnityReferenceAssemblies");
            if (Directory.Exists(refAssembliesDir))
            {
                foreach (var dll in Directory.GetFiles(refAssembliesDir, "*.dll", SearchOption.AllDirectories))
                {
                    AddRef(dll);
                }
            }

            // 4. Managed folder in Editor
            string managedDir = Path.Combine(dataPath, "Managed");
            if (Directory.Exists(managedDir))
            {
                foreach (var dll in Directory.GetFiles(managedDir, "*.dll", SearchOption.AllDirectories))
                {
                    AddRef(dll);
                }
            }

            s_CachedMetadataReferences = refList;
        }

        public static bool CompileAndEmit(string sourceCode, out byte[] assemblyBytes, out List<string> errors)
        {
            assemblyBytes = null;
            errors = new List<string>();

            if (!IsSupported)
            {
                errors.Add(UnsupportedReason);
                return false;
            }

            try
            {
                // 1. Parse SyntaxTree
                object syntaxTree;
                var parsePars = s_ParseTextMethod.GetParameters();
                if (parsePars.Length == 1)
                {
                    syntaxTree = s_ParseTextMethod.Invoke(null, new object[] { sourceCode });
                }
                else
                {
                    var parseArgs = new object[parsePars.Length];
                    parseArgs[0] = sourceCode;
                    for (int i = 1; i < parsePars.Length; i++)
                    {
                        parseArgs[i] = parsePars[i].DefaultValue != DBNull.Value ? parsePars[i].DefaultValue : null;
                    }
                    syntaxTree = s_ParseTextMethod.Invoke(null, parseArgs);
                }

                // 2. SyntaxTree array
                var syntaxTreeArray = Array.CreateInstance(s_CodeAnalysisAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree"), 1);
                syntaxTreeArray.SetValue(syntaxTree, 0);

                // 3. Metadata references array
                var refArray = Array.CreateInstance(s_MetadataRefType, s_CachedMetadataReferences.Count);
                for (int i = 0; i < s_CachedMetadataReferences.Count; i++)
                {
                    refArray.SetValue(s_CachedMetadataReferences[i], i);
                }

                // 4. Create compilation
                string assemblyName = "__UnityCliEval_" + Guid.NewGuid().ToString("N");
                var compilation = s_CreateCompMethod.Invoke(null, new object[] { assemblyName, syntaxTreeArray, refArray, s_CompilationOptions });

                // 5. Emit to memory stream
                using (var ms = new MemoryStream())
                {
                    object emitResult;
                    if (s_EmitMethod.GetParameters().Length == 1)
                    {
                        emitResult = s_EmitMethod.Invoke(compilation, new object[] { ms });
                    }
                    else
                    {
                        var emitPars = s_EmitMethod.GetParameters();
                        var emitArgs = new object[emitPars.Length];
                        emitArgs[0] = ms;
                        for (int i = 1; i < emitPars.Length; i++)
                        {
                            emitArgs[i] = emitPars[i].DefaultValue != DBNull.Value ? emitPars[i].DefaultValue : null;
                        }
                        emitResult = s_EmitMethod.Invoke(compilation, emitArgs);
                    }

                    var successProp = emitResult.GetType().GetProperty("Success");
                    bool isSuccess = (bool)successProp.GetValue(emitResult);

                    if (!isSuccess)
                    {
                        var diagsProp = emitResult.GetType().GetProperty("Diagnostics");
                        var diags = (System.Collections.IEnumerable)diagsProp.GetValue(emitResult);
                        foreach (var d in diags)
                        {
                            if (d != null)
                            {
                                errors.Add(d.ToString());
                            }
                        }
                        return false;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    assemblyBytes = ms.ToArray();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errors.Add("Roslyn compilation error: " + ex.Message);
                return false;
            }
        }
    }
}

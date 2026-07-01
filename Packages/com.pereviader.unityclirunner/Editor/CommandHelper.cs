using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace UnityCliRunner
{
    internal static class CommandHelper
    {
        public static string[] SplitArguments(string commandLine)
        {
            var args = new List<string>();
            if (string.IsNullOrEmpty(commandLine))
            {
                return args.ToArray();
            }

            var current = new StringBuilder();
            bool inQuotes = false;
            bool isEscaped = false;
            bool inArg = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (isEscaped)
                {
                    current.Append(c);
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    if (i + 1 < commandLine.Length && (commandLine[i + 1] == '"' || commandLine[i + 1] == '\\'))
                    {
                        isEscaped = true;
                        inArg = true;
                    }
                    else
                    {
                        current.Append(c);
                        inArg = true;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                    inArg = true;
                }
                else if (c == ' ' && !inQuotes)
                {
                    if (inArg)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                        inArg = false;
                    }
                }
                else
                {
                    current.Append(c);
                    inArg = true;
                }
            }

            if (inArg)
            {
                args.Add(current.ToString());
            }
            return args.ToArray();
        }

        public static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if (type != null)
                        return type;
                }
                catch { }
            }
            return null;
        }

        public static MethodInfo FindStaticMethod(Type type, string methodName, int paramCount)
        {
            var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo candidate = null;
            int matchCount = 0;
            foreach (var m in methods)
            {
                if (m.Name == methodName)
                {
                    if (m.GetParameters().Length == paramCount)
                    {
                        candidate = m;
                        matchCount++;
                    }
                }
            }
            if (matchCount == 1)
            {
                return candidate;
            }
            if (matchCount > 1)
            {
                throw new AmbiguousMatchException($"Ambiguous match: multiple static methods named '{methodName}' with {paramCount} parameters found in type '{type.FullName}'.");
            }
            foreach (var m in methods)
            {
                if (m.Name == methodName)
                {
                    return m;
                }
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace UnityLeanMcp
{
    internal static class MethodResolver
    {
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
            return FindStaticMethod(type, methodName, paramCount, allowTrailingCancellationToken: true);
        }

        public static MethodInfo FindStaticMethod(Type type, string methodName, int paramCount, bool allowTrailingCancellationToken)
        {
            var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var candidatesWithCt = new List<MethodInfo>();
            var candidatesWithoutCt = new List<MethodInfo>();

            foreach (var m in methods)
            {
                if (m.Name != methodName) continue;

                var parameters = m.GetParameters();

                if (allowTrailingCancellationToken &&
                    parameters.Length == paramCount + 1 &&
                    parameters[paramCount].ParameterType == typeof(CancellationToken))
                {
                    candidatesWithCt.Add(m);
                }
                else if (parameters.Length == paramCount)
                {
                    candidatesWithoutCt.Add(m);
                }
            }

            if (candidatesWithCt.Count == 1)
            {
                return candidatesWithCt[0];
            }
            if (candidatesWithCt.Count > 1)
            {
                throw new AmbiguousMatchException($"Ambiguous match: multiple static methods named '{methodName}' with {paramCount} arguments and CancellationToken found in type '{type.FullName}'.");
            }

            if (candidatesWithoutCt.Count == 1)
            {
                return candidatesWithoutCt[0];
            }
            if (candidatesWithoutCt.Count > 1)
            {
                throw new AmbiguousMatchException($"Ambiguous match: multiple static methods named '{methodName}' with {paramCount} parameters found in type '{type.FullName}'.");
            }

            return null;
        }
    }
}

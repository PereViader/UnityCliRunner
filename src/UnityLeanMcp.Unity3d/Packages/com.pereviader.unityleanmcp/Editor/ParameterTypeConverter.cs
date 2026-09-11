using System;
using System.Globalization;
using UnityEngine;

namespace UnityLeanMcp
{
    internal static class ParameterTypeConverter
    {
        public static object ConvertParameter(string rawArg, Type targetType)
        {
            if (targetType == typeof(string))
            {
                return rawArg;
            }

            if (rawArg == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            // Support Nullable<T>
            Type underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (rawArg.Equals("null", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(rawArg))
                {
                    return null;
                }
                return ConvertParameter(rawArg, underlying);
            }

            // Support Enums (by name or integral value)
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, rawArg, true);
            }

            // Support Guid
            if (targetType == typeof(Guid))
            {
                return Guid.Parse(rawArg);
            }

            // Support Primitive & Value Types
            if (targetType == typeof(int)) return int.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(rawArg);
            if (targetType == typeof(long)) return long.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint)) return uint.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(ulong)) return ulong.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(byte)) return byte.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(sbyte)) return sbyte.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(short)) return short.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(ushort)) return ushort.Parse(rawArg, CultureInfo.InvariantCulture);
            if (targetType == typeof(char)) return rawArg.Length > 0 ? rawArg[0] : '\0';
            if (targetType == typeof(decimal)) return decimal.Parse(rawArg, CultureInfo.InvariantCulture);

            // Fallback for complex structs/objects via JsonUtility
            return JsonUtility.FromJson(rawArg, targetType);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static string FormatCommandSurfaceArgs(object? target, object[]? args)
        {
            var builder = new StringBuilder(256);
            builder.Append("instance=").Append(FormatCommandSurfaceValue(target));

            if (args == null || args.Length == 0)
            {
                builder.Append(" args=<none>");
                return builder.ToString();
            }

            builder.Append(" args=");
            for (var i = 0; i < args.Length && i < 6; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append("arg")
                    .Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append("=")
                    .Append(FormatCommandSurfaceValue(args[i]));
            }

            if (args.Length > 6)
            {
                builder.Append(",...");
            }

            return builder.ToString();
        }

        private static string FormatCommandSurfaceValue(object? value)
        {
            return FormatCommandSurfaceValue(value, 0);
        }

        private static string FormatCommandSurfaceValue(object? value, int depth)
        {
            if (value == null)
            {
                return "<null>";
            }

            var type = value.GetType();
            if (TryFormatSimpleValue(value, type, out var simple))
            {
                return simple;
            }

            if (value is Vector3 vector3)
            {
                return "Vector3" + FormatUnityVector(vector3);
            }

            if (value is Vector2Int vector2Int)
            {
                return "Vector2Int("
                    + vector2Int.x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + vector2Int.y.ToString(CultureInfo.InvariantCulture)
                    + ")";
            }

            if (value is UnityEngine.Object unityObject)
            {
                return FormatShortTypeName(type) + "{name=" + SafeUnityObjectName(unityObject) + "}";
            }

            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return FormatShortTypeName(type);
            }

            if (depth >= 2)
            {
                return FormatShortTypeName(type);
            }

            var builder = new StringBuilder(96);
            builder.Append(FormatShortTypeName(type));
            var samples = new List<string>(8);
            AppendSurfaceMemberSample(value, type, "id", samples);
            AppendSurfaceMemberSample(value, type, "Id", samples);
            AppendSurfaceMemberSample(value, type, "uniqueId", samples);
            AppendSurfaceMemberSample(value, type, "UniqueId", samples);
            AppendSurfaceMemberSample(value, type, "name", samples);
            AppendSurfaceMemberSample(value, type, "Name", samples);
            AppendSurfaceMemberSample(value, type, "position", samples);
            AppendSurfaceMemberSample(value, type, "Position", samples);
            AppendSurfaceMemberSample(value, type, "gridPosition", samples);
            AppendSurfaceMemberSample(value, type, "GridPosition", samples);
            AppendSurfaceMemberSample(value, type, "blueprintId", samples);
            AppendSurfaceMemberSample(value, type, "BlueprintId", samples);
            AppendSurfaceMemberSample(value, type, "amount", samples);
            AppendSurfaceMemberSample(value, type, "Amount", samples);

            if (samples.Count > 0)
            {
                builder.Append("{").Append(string.Join(",", samples.ToArray())).Append("}");
            }

            return builder.ToString();
        }

        private static void AppendSurfaceMemberSample(object value, Type type, string memberName, List<string> samples)
        {
            if (samples.Count >= 8)
            {
                return;
            }

            if (!TryReadMemberValue(value, type, memberName, out var memberValue) || memberValue == null)
            {
                return;
            }

            samples.Add(memberName + "=" + TrimFingerprintText(FormatCommandSurfaceValue(memberValue, 1), 96));
        }

        private static string TrimFingerprintText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sanitized = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            if (sanitized.Length <= maxLength)
            {
                return sanitized;
            }

            return sanitized.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private static bool IsTypeOrBaseType(Type type, string fullName)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryCreateVec3Int(int x, int y, int z, out object? value, out string detail)
        {
            value = null;
            var vec3IntType = HarmonyLib.AccessTools.TypeByName("NSMedieval.Vec3Int");
            if (vec3IntType == null)
            {
                detail = "vec3int-type-missing";
                return false;
            }

            var constructor = HarmonyLib.AccessTools.Constructor(vec3IntType, new[] { typeof(int), typeof(int), typeof(int) });
            if (constructor == null)
            {
                detail = "vec3int-constructor-missing";
                return false;
            }

            value = constructor.Invoke(new object[] { x, y, z });
            detail = "ok";
            return true;
        }

        private static bool SetInstanceFieldIfPresent(object target, Type targetType, string fieldName, object? value)
        {
            var field = targetType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                return false;
            }

            try
            {
                field.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SetInstancePropertyIfPresent(object target, Type targetType, string propertyName, object? value)
        {
            var property = targetType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                return false;
            }

            try
            {
                property.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using GoingCooperative.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static bool TryAddSimpleValue(object value, Type valueType, ref DeterminismHash hash)
        {
            if (valueType.IsEnum || value is string || value is bool || value is char)
            {
                hash.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                return true;
            }

            switch (Type.GetTypeCode(valueType))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    hash.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return true;
                default:
                    return false;
            }
        }
        private static string FormatUnityVector(Vector3 value)
        {
            return "("
                + value.x.ToString("F3", CultureInfo.InvariantCulture)
                + ","
                + value.y.ToString("F3", CultureInfo.InvariantCulture)
                + ","
                + value.z.ToString("F3", CultureInfo.InvariantCulture)
                + ")";
        }
        private static string SafeUnityObjectName(UnityEngine.Object? unityObject)
        {
            if (unityObject == null)
            {
                return "<null>";
            }

            try
            {
                return TrimFingerprintText(unityObject.name, 96);
            }
            catch
            {
                return "<name-error>";
            }
        }
        private static bool TryGetInstanceFieldValue(object owner, string fieldName, out object? value)
        {
            value = null;
            var field = owner.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                return false;
            }

            try
            {
                value = field.GetValue(owner);
                return true;
            }
            catch
            {
                return false;
            }
        }
        private static bool ContainsOrdinalIgnoreCase(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static string FormatShortTypeName(Type type)
        {
            if (type.IsByRef)
            {
                type = type.GetElementType() ?? type;
            }

            if (!type.IsGenericType)
            {
                return type.Name;
            }

            var name = type.Name;
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
            {
                name = name.Substring(0, tickIndex);
            }

            return name;
        }
        private static bool TryFormatSimpleValue(object value, Type valueType, out string formatted)
        {
            formatted = string.Empty;
            if (valueType.IsEnum || value is string || value is bool || value is char)
            {
                formatted = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
            }

            switch (Type.GetTypeCode(valueType))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    formatted = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    return true;
                default:
                    return false;
            }
        }
        private static bool TryReadMemberValue(object owner, Type type, string memberName, out object value)
        {
            value = null;
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    try
                    {
                        value = field.GetValue(owner);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                var property = current.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        value = property.GetValue(owner, null);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return false;
        }
        private static bool TryReadInstanceMemberValue(object owner, string memberName, out object? value)
        {
            value = null;
            var type = owner.GetType();
            try
            {
                var property = GetCachedInstanceProperty(type, memberName);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(owner, null);
                    return true;
                }

                var field = GetCachedInstanceField(type, memberName);
                if (field != null)
                {
                    value = field.GetValue(owner);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static int applyingRuntimeCommandDepth;
        private static readonly object ReflectionLookupCacheLock = new object();
        private static readonly Dictionary<string, PropertyInfo?> InstancePropertyLookupCache = new Dictionary<string, PropertyInfo?>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FieldInfo?> InstanceFieldLookupCache = new Dictionary<string, FieldInfo?>(StringComparer.Ordinal);

        private int TryPatchPassiveCommandSurfaceMethodsByName(Harmony harmonyInstance, HarmonyMethod postfix, string typeName, string methodName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null || type.ContainsGenericParameters)
                {
                    return 0;
                }

                var patched = 0;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || !IsPatchableImplementedMethod(type, method))
                    {
                        continue;
                    }

                    harmonyInstance.Patch(method, postfix: postfix);
                    patched++;
                }

                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Passive command surface probe patch failed: " + typeName + "." + methodName + " " + ex.GetType().Name + " " + ex.Message);
                return 0;
            }
        }

        private int TryPatchPassiveCommandSurfaceMethodsByNameOnMatchingTypes(Harmony harmonyInstance, HarmonyMethod postfix, string namespacePrefix, string typeNameContains, string methodName)
        {
            var patched = 0;
            var seenMethods = new HashSet<MethodBase>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || !TypeOrBaseMatches(type, namespacePrefix, typeNameContains))
                    {
                        continue;
                    }

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var method in methods)
                    {
                        if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)
                            || seenMethods.Contains(method)
                            || !IsPatchableImplementedMethod(type, method))
                        {
                            continue;
                        }

                        harmonyInstance.Patch(method, postfix: postfix);
                        seenMethods.Add(method);
                        patched++;
                    }
                }
            }

            return patched;
        }

        private static bool IsPatchableImplementedMethod(Type ownerType, MethodInfo method)
        {
            return !method.IsAbstract
                && !method.ContainsGenericParameters
                && method.GetMethodBody() != null
                && method.DeclaringType != null
                && (method.DeclaringType == ownerType || method.DeclaringType.IsAssignableFrom(ownerType));
        }

        private static bool TypeOrBaseMatches(Type type, string namespacePrefix, string typeNameContains)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var namespaceName = current.Namespace ?? string.Empty;
                if (namespaceName.StartsWith(namespacePrefix, StringComparison.Ordinal)
                    && current.Name.IndexOf(typeNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void TryCaptureGameSpeedManagerInstance(string source)
        {
            if (gameSpeedManagerInstance != null)
            {
                return;
            }

            var type = AccessTools.TypeByName("NSMedieval.Manager.GameSpeedManager");
            if (type == null)
            {
                return;
            }

            try
            {
                var managers = UnityEngine.Object.FindObjectsOfType(type) ?? Array.Empty<UnityEngine.Object>();
                if (managers.Length == 0)
                {
                    return;
                }

                Array.Sort(managers, CompareUnityObjectsForFixedStep);
                gameSpeedManagerInstance = managers[0];
                instance?.AppendPluginLog("Captured GameSpeedManager instance from " + source + " selected=" + BuildFixedStepUnityObjectKey(managers[0]));
            }
            catch
            {
            }
        }

        private static bool TrySetInstanceMemberValue(object owner, string memberName, object? value)
        {
            try
            {
                var property = GetCachedInstanceProperty(owner.GetType(), memberName);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(owner, value, null);
                    return true;
                }

                var field = GetCachedInstanceField(owner.GetType(), memberName);
                if (field != null)
                {
                    field.SetValue(owner, value);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadFirstSelectionVec3Int(object owner, out int x, out int y, out int z, out string detail)
        {
            x = 0;
            y = 0;
            z = 0;
            var preferredNames = new[]
            {
                "positionsInSelectedSlopes",
                "positionsInSelectedTiles",
                "positionsInSelectedNodes",
                "selectedPositions",
                "selectionPositions",
                "positionsInSelection",
                "selectedCells",
                "selectedNodes"
            };

            var type = owner.GetType();
            foreach (var name in preferredNames)
            {
                if (TryReadMemberValue(owner, type, name, out var memberValue)
                    && memberValue != null
                    && TryReadFirstVec3IntFromCollectionObject(memberValue, out x, out y, out z, out var memberDetail))
                {
                    detail = name + ":" + memberDetail;
                    return true;
                }
            }

            detail = "no-selection-vector-member type=" + type.FullName;
            return false;
        }

        private static bool TryReadSelectionOrderType(object owner, out string orderType)
        {
            orderType = string.Empty;
            foreach (var name in new[] { "OrderType", "orderType", "currentOrderType", "selectedOrderType", "lastOrderType" })
            {
                if (TryReadMemberValue(owner, owner.GetType(), name, out var value) && value != null)
                {
                    orderType = value.ToString() ?? string.Empty;
                    return orderType.Length > 0;
                }
            }

            return false;
        }

        private static bool TryReadFirstVec3IntFromCollectionObject(object collection, out int x, out int y, out int z, out string detail)
        {
            x = 0;
            y = 0;
            z = 0;
            if (TryReadVec3IntLikeValue(collection, out x, out y, out z))
            {
                detail = "direct";
                return true;
            }

            if (!(collection is IEnumerable enumerable))
            {
                detail = "not-enumerable type=" + collection.GetType().FullName;
                return false;
            }

            var scanned = 0;
            foreach (var item in enumerable)
            {
                scanned++;
                if (item != null && TryReadVec3IntLikeValue(item, out x, out y, out z))
                {
                    detail = "item=" + scanned.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (scanned >= 256)
                {
                    break;
                }
            }

            detail = "no-vector-items scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryReadVec3IntLikeValue(object value, out int x, out int y, out int z)
        {
            x = 0;
            y = 0;
            z = 0;
            if (value == null)
            {
                return false;
            }

            var type = value.GetType();
            if (!TryReadNumberMember(value, type, "x", out var rawX)
                || !TryReadNumberMember(value, type, "y", out var rawY)
                || !TryReadNumberMember(value, type, "z", out var rawZ))
            {
                return false;
            }

            x = (int)Math.Round(rawX);
            y = (int)Math.Round(rawY);
            z = (int)Math.Round(rawZ);
            return true;
        }

        private static bool TryReadVec3IntMember(object owner, Type ownerType, string memberName, out int x, out int y, out int z)
        {
            x = 0;
            y = 0;
            z = 0;
            if (TryReadMemberValue(owner, ownerType, memberName, out var value) && value != null)
            {
                return TryReadVec3IntLikeValue(value, out x, out y, out z);
            }

            return false;
        }

        private static bool TryReadNumberMember(object value, Type type, string memberName, out double number)
        {
            number = 0d;
            if (!TryReadMemberValue(value, type, memberName, out var memberValue) || memberValue == null)
            {
                return false;
            }

            try
            {
                number = Convert.ToDouble(memberValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int NormalizePossibleWorldY(double y)
        {
            var rounded = (int)Math.Round(y);
            return rounded >= 10 ? rounded - 10 : rounded;
        }

        private static bool TryGetPlantAt(int x, int y, int z, out object? plant, out string detail)
        {
            plant = null;
            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Manager.PlantResourceManager");
                var target = plantResourceManagerInstance ?? AccessTools.Property(managerType, "Instance")?.GetValue(null, null);
                if (managerType == null || target == null)
                {
                    detail = "plant-resource-manager-missing";
                    return false;
                }

                if (!TryCreateVec3Int(x, y, z, out var position, out detail) || position == null)
                {
                    return false;
                }

                var getPlant = AccessTools.Method(managerType, "GetPlant", new[] { position.GetType() });
                if (getPlant == null)
                {
                    detail = "get-plant-method-missing";
                    return false;
                }

                plant = getPlant.Invoke(target, new[] { position });
                detail = plant == null ? "plant-missing" : "ok";
                return plant != null;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string SanitizeLogDetail(string message)
        {
            return (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }

        private static PropertyInfo? GetCachedInstanceProperty(Type type, string memberName)
        {
            var key = BuildReflectionLookupCacheKey(type, memberName);
            lock (ReflectionLookupCacheLock)
            {
                if (InstancePropertyLookupCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var property = FindInstanceProperty(type, memberName);
            lock (ReflectionLookupCacheLock)
            {
                InstancePropertyLookupCache[key] = property;
            }

            return property;
        }

        private static FieldInfo? GetCachedInstanceField(Type type, string memberName)
        {
            var key = BuildReflectionLookupCacheKey(type, memberName);
            lock (ReflectionLookupCacheLock)
            {
                if (InstanceFieldLookupCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var field = FindInstanceField(type, memberName);
            lock (ReflectionLookupCacheLock)
            {
                InstanceFieldLookupCache[key] = field;
            }

            return field;
        }

        private static string BuildReflectionLookupCacheKey(Type type, string memberName)
        {
            return (type.FullName ?? type.AssemblyQualifiedName ?? type.Name) + "|" + memberName;
        }

        private static PropertyInfo? FindInstanceProperty(Type type, string memberName)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                {
                    return property;
                }
            }

            return null;
        }

        private static FieldInfo? FindInstanceField(Type type, string memberName)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        private static bool IsActorPathMemberName(string name)
        {
            return ContainsOrdinalIgnoreCase(name, "id")
                || ContainsOrdinalIgnoreCase(name, "name")
                || ContainsOrdinalIgnoreCase(name, "position")
                || ContainsOrdinalIgnoreCase(name, "grid")
                || ContainsOrdinalIgnoreCase(name, "destination")
                || ContainsOrdinalIgnoreCase(name, "target")
                || ContainsOrdinalIgnoreCase(name, "path")
                || ContainsOrdinalIgnoreCase(name, "job")
                || ContainsOrdinalIgnoreCase(name, "goal")
                || ContainsOrdinalIgnoreCase(name, "order")
                || ContainsOrdinalIgnoreCase(name, "state")
                || ContainsOrdinalIgnoreCase(name, "agent")
                || ContainsOrdinalIgnoreCase(name, "view")
                || ContainsOrdinalIgnoreCase(name, "transform");
        }

        private static int CompareUnityObjectsForFixedStep(UnityEngine.Object? left, UnityEngine.Object? right)
        {
            return string.Compare(BuildFixedStepUnityObjectKey(left), BuildFixedStepUnityObjectKey(right), StringComparison.Ordinal);
        }

        private static string BuildFixedStepUnityObjectKey(UnityEngine.Object? value)
        {
            if (value == null)
            {
                return "<null>";
            }

            return value.GetType().FullName + "|" + SafeUnityObjectName(value) + "|" + value.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }
    }
}

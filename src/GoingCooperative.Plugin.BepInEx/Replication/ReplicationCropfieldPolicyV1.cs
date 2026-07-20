using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string CropfieldCropTypePolicy = "CropfieldCropType";
        private const string CropfieldDontSowPolicy = "CropfieldDontSow";
        private const string CropfieldPriorityPolicy = "CropfieldPriority";
        private const string CropfieldSowRangePolicy = "CropfieldSowRange";
        private const string CropfieldHarvestPhasePolicy = "CropfieldHarvestPhase";
        private const string CropfieldCutPhasePolicy = "CropfieldCutPhase";
        private static int replicationCropfieldPolicyAuthoritativeApplyDepth;
        private static int replicationCropfieldPolicyUiRefreshDepth;
        private static float replicationNextCropfieldPolicyHostReconcileRealtime;
        private static readonly Dictionary<object, string> ReplicationCropfieldPolicyHostSignatures =
            new Dictionary<object, string>(ReferenceObjectComparer.Instance);

        private int TryInstallReplicationCropfieldPolicyV1Hooks(Harmony harmony)
        {
            var type = AccessTools.TypeByName("NSMedieval.Crops.CropfieldInstance");
            if (type == null)
            {
                LogReplicationWarning("Going Cooperative cropfield-policy-v1 type missing");
                return 0;
            }

            var count = 0;
            count += PatchCropfieldPolicyMethod(harmony, type, "ChangeCropType", 1, nameof(ReplicationCropfieldPolicyValuePrefix), nameof(ReplicationCropfieldPolicyValuePostfix));
            count += PatchCropfieldPolicyMethod(harmony, type, "SetDontSow", 1, nameof(ReplicationCropfieldPolicyValuePrefix), nameof(ReplicationCropfieldPolicyValuePostfix));
            count += PatchCropfieldPolicyMethod(harmony, type, "SetPriority", 1, nameof(ReplicationCropfieldPolicyValuePrefix), nameof(ReplicationCropfieldPolicyValuePostfix));
            count += PatchCropfieldPolicyMethod(harmony, type, "SetHarvestPhase", 1, nameof(ReplicationCropfieldPolicyValuePrefix), nameof(ReplicationCropfieldPolicyValuePostfix));
            count += PatchCropfieldPolicyMethod(harmony, type, "SetCutPhase", 1, nameof(ReplicationCropfieldPolicyValuePrefix), nameof(ReplicationCropfieldPolicyValuePostfix));
            count += PatchCropfieldPolicyMethod(harmony, type, "SetSowRange", 2, nameof(ReplicationCropfieldSowRangePrefix), nameof(ReplicationCropfieldPolicyValuePostfix));
            count += PatchCropfieldPolicyMethod(harmony, type, "PasteCropfieldSettings", 1, nameof(ReplicationCropfieldPastePrefix), nameof(ReplicationCropfieldPastePostfix));
            LogReplicationInfo("Going Cooperative cropfield-policy-v1 patches=" + count.ToString(CultureInfo.InvariantCulture));
            return count;
        }

        private int PatchReplicationCropfieldPanelActions(Harmony harmony)
        {
            var panelType = AccessTools.TypeByName("NSMedieval.UI.SelectionExtraCropfield");
            var postfix = typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationCropfieldPanelActionPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            var closurePostfix = typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationCropfieldPanelClosureActionPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (panelType == null || postfix == null || closurePostfix == null) return 0;

            var count = 0;
            foreach (var methodName in new[] { "OnDoNotSowToggle", "OnPriorityChanged", "OnSliderValueChange" })
            {
                var method = AccessTools.Method(panelType, methodName);
                if (method == null) continue;
                try { harmony.Patch(method, postfix: new HarmonyMethod(postfix)); count++; }
                catch (Exception ex) { LogReplicationWarning("Going Cooperative cropfield-policy-v1 UI callback patch failed method=" + methodName + " error=" + FormatReflectionExceptionDetail(ex)); }
            }

            foreach (var nested in panelType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var method in nested.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name.IndexOf("<SetupCropPlantSelection>b__", StringComparison.Ordinal) < 0
                        && method.Name.IndexOf("<SetupHarvestPhaseCheckboxListener>b__", StringComparison.Ordinal) < 0
                        && method.Name.IndexOf("<SetupCutPhaseCheckboxListener>b__", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    try { harmony.Patch(method, postfix: new HarmonyMethod(closurePostfix)); count++; }
                    catch (Exception ex) { LogReplicationWarning("Going Cooperative cropfield-policy-v1 listener patch failed method=" + method.Name + " error=" + FormatReflectionExceptionDetail(ex)); }
                }
            }

            return count;
        }

        private static void ReplicationCropfieldPanelActionPostfix(object __instance)
        {
            SendReplicationHostCropfieldPanelSelection(__instance, "cropfield-policy-host-ui-action");
        }

        private static void ReplicationCropfieldPanelClosureActionPostfix(object __instance)
        {
            if (__instance == null) return;
            if (TryReadInstanceMemberValue(__instance, "cropfieldInstance", out var cropfield) && cropfield != null)
            {
                SendReplicationHostCropfieldPolicyDirect(cropfield, "cropfield-policy-host-ui-listener");
                return;
            }

            if (TryReadInstanceMemberValue(__instance, "<>4__this", out var panel) && panel != null)
                SendReplicationHostCropfieldPanelSelection(panel, "cropfield-policy-host-ui-listener");
        }

        private static void SendReplicationHostCropfieldPanelSelection(object panel, string source)
        {
            if (!ReplicationCropfieldPolicyHostCaptureReady() || panel == null) return;
            var sent = false;
            if (TryReadInstanceMemberValue(panel, "selectedCropfields", out var selectedValue)
                && selectedValue is IEnumerable selected)
            {
                foreach (var cropfield in selected)
                {
                    if (cropfield == null) continue;
                    sent = true;
                    SendReplicationHostCropfieldPolicyDirect(cropfield, source);
                }
            }

            if (!sent && TryReadInstanceMemberValue(panel, "currentCropfield", out var current) && current != null)
                SendReplicationHostCropfieldPolicyDirect(current, source);
        }

        private static void SendReplicationHostCropfieldPolicyDirect(object cropfield, string source)
        {
            if (!ReplicationCropfieldPolicyHostCaptureReady() || cropfield == null) return;
            var target = FormatReplicationCropfieldTarget(cropfield);
            ReplicationCropfieldPolicyHostSignatures[cropfield] = FormatReplicationCropfieldPolicySignature(cropfield, target);
            SendReplicationCropfieldCompletePolicy(cropfield, target, sendAsIntent: false, source);
        }

        private static void ObserveReplicationHostCropfieldPanelSelection(object panel, string source)
        {
            if (!ReplicationCropfieldPolicyHostCaptureReady() || panel == null) return;
            var observed = false;
            if (TryReadInstanceMemberValue(panel, "selectedCropfields", out var selectedValue)
                && selectedValue is IEnumerable selected)
            {
                foreach (var cropfield in selected)
                {
                    if (cropfield == null) continue;
                    observed = true;
                    ObserveReplicationHostCropfieldPolicy(cropfield, source);
                }
            }

            if (!observed && TryReadInstanceMemberValue(panel, "currentCropfield", out var current) && current != null)
                ObserveReplicationHostCropfieldPolicy(current, source);
        }

        private static void ObserveReplicationHostCropfieldPolicyIfReady(object cropfield, string source)
        {
            if (ReplicationCropfieldPolicyHostCaptureReady()) ObserveReplicationHostCropfieldPolicy(cropfield, source);
        }

        private static bool ReplicationCropfieldPolicyHostCaptureReady()
        {
            return replicationConfigCropfieldPolicyV1
                && replicationConfigHostMode
                && replicationRuntimeStarted
                && replicationRemoteHelloReceived
                && replicationCropfieldPolicyAuthoritativeApplyDepth == 0
                && replicationCropfieldPolicyUiRefreshDepth == 0;
        }

        private int PatchReplicationCropfieldPanelUpdate(Harmony harmony)
        {
            var panelType = AccessTools.TypeByName("NSMedieval.UI.SelectionExtraCropfield");
            var infoType = AccessTools.TypeByName("NSMedieval.UI.InfoPanelCropfield");
            var method = panelType == null || infoType == null
                ? null
                : AccessTools.Method(panelType, "UpdatePanel", new[] { infoType });
            var postfix = typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationCropfieldPanelUpdatePostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null || postfix == null)
            {
                LogReplicationWarning("Going Cooperative cropfield-policy-v1 panel update patch missing");
                return 0;
            }

            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative cropfield-policy-v1 panel update patch failed error="
                    + FormatReflectionExceptionDetail(ex));
                return 0;
            }
        }

        private static void ReplicationCropfieldPanelUpdatePostfix(object __instance)
        {
            if (!ReplicationCropfieldPolicyHostCaptureReady() || __instance == null)
            {
                return;
            }
            ObserveReplicationHostCropfieldPanelSelection(__instance, "cropfield-policy-host-panel");
        }

        private int PatchCropfieldPolicyMethod(Harmony harmony, Type type, string methodName, int arity, string prefixName, string postfixName)
        {
            MethodInfo? target = null;
            foreach (var candidate in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (candidate.Name == methodName && candidate.GetParameters().Length == arity)
                {
                    target = candidate;
                    break;
                }
            }

            var prefix = typeof(GoingCooperativePlugin).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(GoingCooperativePlugin).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null || postfix == null)
            {
                LogReplicationWarning("Going Cooperative cropfield-policy-v1 patch missing method=" + methodName);
                return 0;
            }

            try
            {
                harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative cropfield-policy-v1 patch failed method=" + methodName + " error=" + FormatReflectionExceptionDetail(ex));
                return 0;
            }
        }

        private static bool ReplicationCropfieldPolicyValuePrefix(object __instance, object __0, MethodBase __originalMethod, ref string? __state)
        {
            __state = null;
            if (replicationConfigCropfieldPolicyDiagnostics)
            {
                instance?.LogReplicationInfo(
                    "Going Cooperative cropfield-policy setter entered method=" + __originalMethod.Name
                    + " side=" + (replicationConfigHostMode ? "host" : "client")
                    + " enabled=" + replicationConfigCropfieldPolicyV1
                    + " applyDepth=" + replicationCropfieldPolicyAuthoritativeApplyDepth.ToString(CultureInfo.InvariantCulture));
            }
            if (!replicationConfigCropfieldPolicyV1
                || replicationCropfieldPolicyAuthoritativeApplyDepth > 0
                || __instance == null)
            {
                return true;
            }

            var target = FormatReplicationCropfieldTarget(__instance);
            var methodName = __originalMethod.Name;
            string payload;
            if (string.Equals(methodName, "ChangeCropType", StringComparison.Ordinal))
            {
                if (!TryResolveReplicationModelId(__0, out var cropTypeId)) return true;
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldCropTypePolicy, target, cropTypeId, 0, 0, true);
            }
            else if (string.Equals(methodName, "SetDontSow", StringComparison.Ordinal))
            {
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldDontSowPolicy, target, string.Empty, 0, 0, Convert.ToBoolean(__0, CultureInfo.InvariantCulture));
            }
            else
            {
                var policy = string.Equals(methodName, "SetPriority", StringComparison.Ordinal)
                    ? CropfieldPriorityPolicy
                    : string.Equals(methodName, "SetHarvestPhase", StringComparison.Ordinal)
                        ? CropfieldHarvestPhasePolicy
                        : CropfieldCutPhasePolicy;
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(policy, target, string.Empty, 0, Convert.ToInt32(__0, CultureInfo.InvariantCulture), true);
            }

            __state = payload;
            if (replicationConfigHostMode)
            {
                // The host uses the exact same semantic capture and payload as the
                // client. It executes vanilla locally, then the peer applies this
                // absolute state under the authoritative-apply guard.
                BroadcastHostManagementMutation(payload, "cropfield-policy-host-capture:" + methodName);
                return true;
            }
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(payload, "cropfield-policy:" + methodName);
                return false;
            }

            return true;
        }

        private static bool ReplicationCropfieldSowRangePrefix(object __instance, int __0, int __1, ref string? __state)
        {
            __state = null;
            if (replicationConfigCropfieldPolicyDiagnostics)
            {
                instance?.LogReplicationInfo(
                    "Going Cooperative cropfield-policy setter entered method=SetSowRange"
                    + " side=" + (replicationConfigHostMode ? "host" : "client")
                    + " enabled=" + replicationConfigCropfieldPolicyV1
                    + " applyDepth=" + replicationCropfieldPolicyAuthoritativeApplyDepth.ToString(CultureInfo.InvariantCulture));
            }
            if (!replicationConfigCropfieldPolicyV1
                || replicationCropfieldPolicyAuthoritativeApplyDepth > 0
                || __instance == null)
            {
                return true;
            }

            __state = LockstepCommandPayloads.CreateManagementPolicyPayload(
                CropfieldSowRangePolicy,
                FormatReplicationCropfieldTarget(__instance),
                string.Empty,
                __0,
                __1,
                true);
            if (replicationConfigHostMode)
            {
                BroadcastHostManagementMutation(__state, "cropfield-policy-host-capture:SetSowRange");
                return true;
            }
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "cropfield-policy:SetSowRange");
                return false;
            }

            return true;
        }

        private static void ReplicationCropfieldPolicyValuePostfix(object __instance, MethodBase __originalMethod, string? __state)
        {
            if (!replicationConfigCropfieldPolicyV1
                || replicationCropfieldPolicyAuthoritativeApplyDepth > 0
                || !replicationConfigHostMode
                || string.IsNullOrWhiteSpace(__state))
            {
                return;
            }

            if (TryCreateReplicationCropfieldPolicyPayload(__instance, __originalMethod.Name, __state!, out var actual))
            {
                BroadcastHostManagementMutation(actual, "cropfield-policy-local:" + __originalMethod.Name);
            }
        }

        private static bool ReplicationCropfieldPastePrefix(object __instance, object __0, ref string? __state)
        {
            __state = __instance == null ? null : FormatReplicationCropfieldTarget(__instance);
            if (replicationConfigCropfieldPolicyV1
                && replicationConfigHostMode
                && replicationCropfieldPolicyAuthoritativeApplyDepth == 0
                && __instance != null
                && __0 != null
                && !string.IsNullOrWhiteSpace(__state))
            {
                SendReplicationCropfieldCompletePolicy(
                    __0,
                    __state!,
                    sendAsIntent: false,
                    "cropfield-policy-host-capture:PasteCropfieldSettings");
                return true;
            }
            if (!replicationConfigCropfieldPolicyV1
                || replicationCropfieldPolicyAuthoritativeApplyDepth > 0
                || __instance == null
                || __0 == null
                || !ShouldSendReplicationLocalCommandIntent())
            {
                return true;
            }

            // Scalar policies first and crop type last: a type change can alter the
            // target's blueprint/topology, while every preceding intent still resolves
            // against the stable pre-change key.
            SendReplicationCropfieldCompletePolicy(__0, __state!, sendAsIntent: true, "cropfield-policy:paste");
            return false;
        }

        private static void ReplicationCropfieldPastePostfix(object __instance, string? __state)
        {
            if (!replicationConfigCropfieldPolicyV1
                || replicationCropfieldPolicyAuthoritativeApplyDepth > 0
                || !replicationConfigHostMode
                || string.IsNullOrWhiteSpace(__state))
            {
                return;
            }

            SendReplicationCropfieldCompletePolicy(__instance, __state!, sendAsIntent: false, "cropfield-policy-local:paste");
        }

        private static bool TryCreateReplicationCropfieldPolicyPayload(object cropfield, string methodName, string priorPayload, out string payload)
        {
            payload = priorPayload;
            if (!LockstepCommandPayloads.TryReadManagementPolicyPayload(priorPayload, out _, out var target, out _, out _, out _, out _)) return false;
            if (string.Equals(methodName, "ChangeCropType", StringComparison.Ordinal)
                && TryResolveReplicationCropfieldBlueprintId(cropfield, out var cropTypeId))
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldCropTypePolicy, target, cropTypeId, 0, 0, true);
            else if (string.Equals(methodName, "SetDontSow", StringComparison.Ordinal))
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldDontSowPolicy, target, string.Empty, 0, 0, ReadReplicationCropfieldBool(cropfield, "DontSow"));
            else if (string.Equals(methodName, "SetPriority", StringComparison.Ordinal))
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldPriorityPolicy, target, string.Empty, 0, ReadReplicationCropfieldInt(cropfield, "Priority"), true);
            else if (string.Equals(methodName, "SetSowRange", StringComparison.Ordinal))
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldSowRangePolicy, target, string.Empty, ReadReplicationCropfieldInt(cropfield, "MinSowDate"), ReadReplicationCropfieldInt(cropfield, "MaxSowDate"), true);
            else if (string.Equals(methodName, "SetHarvestPhase", StringComparison.Ordinal))
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldHarvestPhasePolicy, target, string.Empty, 0, ReadReplicationCropfieldInt(cropfield, "HarvestPhase"), true);
            else if (string.Equals(methodName, "SetCutPhase", StringComparison.Ordinal))
                payload = LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldCutPhasePolicy, target, string.Empty, 0, ReadReplicationCropfieldInt(cropfield, "CutPhase"), true);
            return true;
        }

        private static void SendReplicationCropfieldCompletePolicy(object source, string target, bool sendAsIntent, string sourceName)
        {
            var payloads = new System.Collections.Generic.List<string>(6)
            {
                LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldDontSowPolicy, target, string.Empty, 0, 0, ReadReplicationCropfieldBool(source, "DontSow")),
                LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldPriorityPolicy, target, string.Empty, 0, ReadReplicationCropfieldInt(source, "Priority"), true),
                LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldSowRangePolicy, target, string.Empty, ReadReplicationCropfieldInt(source, "MinSowDate"), ReadReplicationCropfieldInt(source, "MaxSowDate"), true),
                LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldHarvestPhasePolicy, target, string.Empty, 0, ReadReplicationCropfieldInt(source, "HarvestPhase"), true),
                LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldCutPhasePolicy, target, string.Empty, 0, ReadReplicationCropfieldInt(source, "CutPhase"), true)
            };
            if (TryResolveReplicationCropfieldBlueprintId(source, out var cropTypeId))
                payloads.Add(LockstepCommandPayloads.CreateManagementPolicyPayload(CropfieldCropTypePolicy, target, cropTypeId, 0, 0, true));

            for (var i = 0; i < payloads.Count; i++)
            {
                if (sendAsIntent) SendReplicationManagementIntent(payloads[i], sourceName);
                else BroadcastHostManagementMutation(payloads[i], sourceName);
            }
        }

        private static int ReadReplicationCropfieldInt(object cropfield, string name)
        {
            return TryReadInstanceMemberValue(cropfield, name, out var raw) && raw != null
                ? Convert.ToInt32(raw, CultureInfo.InvariantCulture)
                : 0;
        }

        private static bool ReadReplicationCropfieldBool(object cropfield, string name)
        {
            return TryReadInstanceMemberValue(cropfield, name, out var raw) && raw != null
                && Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }

        private static bool TryResolveReplicationCropfieldBlueprintId(object cropfield, out string cropfieldId)
        {
            cropfieldId = string.Empty;
            return cropfield != null
                && TryReadInstanceMemberValue(cropfield, "Blueprint", out var blueprint)
                && blueprint != null
                && TryResolveReplicationModelId(blueprint, out cropfieldId)
                && !string.IsNullOrWhiteSpace(cropfieldId);
        }

        private static void UpdateReplicationCropfieldPolicyV1Host()
        {
            if (!replicationConfigCropfieldPolicyV1
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationCropfieldPolicyAuthoritativeApplyDepth > 0
                || Time.realtimeSinceStartup < replicationNextCropfieldPolicyHostReconcileRealtime)
            {
                return;
            }
            replicationNextCropfieldPolicyHostReconcileRealtime = Time.realtimeSinceStartup + 0.25f;

            var managerType = AccessTools.TypeByName("NSMedieval.Crops.CropsManager");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            if (manager == null
                || !TryReadInstanceMemberValue(manager, "cropfields", out var cropfieldsValue)
                || !(cropfieldsValue is IEnumerable cropfields))
            {
                return;
            }

            foreach (var cropfield in cropfields)
            {
                if (cropfield == null) continue;
                ObserveReplicationHostCropfieldPolicy(cropfield, "cropfield-policy-host-reconcile");
            }
        }

        private static void ObserveReplicationHostCropfieldPolicy(object cropfield, string source)
        {
            var target = FormatReplicationCropfieldTarget(cropfield);
            var signature = FormatReplicationCropfieldPolicySignature(cropfield, target);
            if (ReplicationCropfieldPolicyHostSignatures.TryGetValue(cropfield, out var previous)
                && string.Equals(previous, signature, StringComparison.Ordinal))
            {
                return;
            }

            ReplicationCropfieldPolicyHostSignatures[cropfield] = signature;
            SendReplicationCropfieldCompletePolicy(cropfield, target, sendAsIntent: false, source);
        }

        private static string FormatReplicationCropfieldPolicySignature(object cropfield, string target)
        {
            var cropTypeId = string.Empty;
            TryResolveReplicationCropfieldBlueprintId(cropfield, out cropTypeId);
            return target + "|type=" + cropTypeId
                + "|dontSow=" + (ReadReplicationCropfieldBool(cropfield, "DontSow") ? "1" : "0")
                + "|priority=" + ReadReplicationCropfieldInt(cropfield, "Priority").ToString(CultureInfo.InvariantCulture)
                + "|sow=" + ReadReplicationCropfieldInt(cropfield, "MinSowDate").ToString(CultureInfo.InvariantCulture)
                + "," + ReadReplicationCropfieldInt(cropfield, "MaxSowDate").ToString(CultureInfo.InvariantCulture)
                + "|harvest=" + ReadReplicationCropfieldInt(cropfield, "HarvestPhase").ToString(CultureInfo.InvariantCulture)
                + "|cut=" + ReadReplicationCropfieldInt(cropfield, "CutPhase").ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsReplicationCropfieldPolicy(string policy)
        {
            return string.Equals(policy, CropfieldCropTypePolicy, StringComparison.Ordinal)
                || string.Equals(policy, CropfieldDontSowPolicy, StringComparison.Ordinal)
                || string.Equals(policy, CropfieldPriorityPolicy, StringComparison.Ordinal)
                || string.Equals(policy, CropfieldSowRangePolicy, StringComparison.Ordinal)
                || string.Equals(policy, CropfieldHarvestPhasePolicy, StringComparison.Ordinal)
                || string.Equals(policy, CropfieldCutPhasePolicy, StringComparison.Ordinal);
        }

        private static bool TryApplyReplicationCropfieldPolicy(string policy, string targetId, string key, int index, int value, bool enabled, out string detail)
        {
            if (!replicationConfigCropfieldPolicyV1)
            {
                detail = "cropfield-policy-v1-disabled";
                return false;
            }

            var managerType = AccessTools.TypeByName("NSMedieval.Crops.CropsManager");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            var lookup = manager == null ? "manager-missing" : string.Empty;
            object? cropfield = null;
            if (manager == null || !TryResolveReplicationCropfieldTarget(manager, targetId, out cropfield, out lookup) || cropfield == null)
            {
                detail = "cropfield-policy-target-missing " + lookup;
                return false;
            }

            try
            {
                MethodInfo? method;
                object[] args;
                if (string.Equals(policy, CropfieldCropTypePolicy, StringComparison.Ordinal))
                {
                    var repositoryType = AccessTools.TypeByName("NSMedieval.Crops.CropfieldRepository");
                    var repository = repositoryType == null ? null : ResolveReplicationUnityManagerInstance(repositoryType);
                    var cropType = repository == null ? null : AccessTools.Method(repository.GetType(), "GetByID", new[] { typeof(string) })?.Invoke(repository, new object[] { key });
                    method = cropType == null ? null : AccessTools.Method(cropfield.GetType(), "ChangeCropType", new[] { cropType.GetType() });
                    if (cropType == null || method == null)
                    {
                        detail = "cropfield-crop-type-missing id=" + key;
                        return false;
                    }
                    args = new[] { cropType };
                }
                else if (string.Equals(policy, CropfieldDontSowPolicy, StringComparison.Ordinal))
                {
                    method = AccessTools.Method(cropfield.GetType(), "SetDontSow", new[] { typeof(bool) });
                    args = new object[] { enabled };
                }
                else if (string.Equals(policy, CropfieldSowRangePolicy, StringComparison.Ordinal))
                {
                    method = AccessTools.Method(cropfield.GetType(), "SetSowRange", new[] { typeof(int), typeof(int) });
                    args = new object[] { index, value };
                }
                else
                {
                    var methodName = string.Equals(policy, CropfieldPriorityPolicy, StringComparison.Ordinal)
                        ? "SetPriority"
                        : string.Equals(policy, CropfieldHarvestPhasePolicy, StringComparison.Ordinal)
                            ? "SetHarvestPhase"
                            : "SetCutPhase";
                    method = AccessTools.Method(cropfield.GetType(), methodName);
                    if (method == null)
                    {
                        detail = "cropfield-policy-method-missing method=" + methodName;
                        return false;
                    }
                    var parameterType = method.GetParameters()[0].ParameterType;
                    args = new[] { parameterType.IsEnum ? Enum.ToObject(parameterType, value) : Convert.ChangeType(value, parameterType, CultureInfo.InvariantCulture) };
                }

                if (method == null)
                {
                    detail = "cropfield-policy-method-missing policy=" + policy;
                    return false;
                }

                replicationCropfieldPolicyAuthoritativeApplyDepth++;
                try { method.Invoke(cropfield, args); }
                finally { replicationCropfieldPolicyAuthoritativeApplyDepth--; }
                var ui = RefreshReplicationCropfieldPolicyUi();
                detail = "ok cropfield-policy-v1 policy=" + policy + " target=" + targetId + " ui=" + ui;
                return true;
            }
            catch (Exception ex)
            {
                detail = "cropfield-policy-v1-apply-failed policy=" + policy + " error=" + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string RefreshReplicationCropfieldPolicyUi()
        {
            if (replicationConfigHostMode || replicationCropfieldPolicyUiRefreshDepth > 0) return "not-client";
            var type = AccessTools.TypeByName("NSMedieval.UI.SelectionExtraCropfield");
            if (type == null) return "surface-missing";
            var refreshed = 0;
            replicationCropfieldPolicyUiRefreshDepth++;
            replicationCropfieldPolicyAuthoritativeApplyDepth++;
            try
            {
                foreach (var panel in Resources.FindObjectsOfTypeAll(type))
                {
                    if (panel == null) continue;
                    AccessTools.Field(type, "cropfieldPreviousTick")?.SetValue(panel, null);
                    AccessTools.Field(type, "cropfieldsInPreviousTick")?.SetValue(panel, -1);
                    var infoPanel = AccessTools.Field(type, "infoPanelCropfield")?.GetValue(panel);
                    var updatePanel = infoPanel == null
                        ? null
                        : AccessTools.Method(type, "UpdatePanel", new[] { infoPanel.GetType() });
                    updatePanel?.Invoke(panel, new[] { infoPanel });
                    refreshed++;
                }
            }
            finally
            {
                replicationCropfieldPolicyAuthoritativeApplyDepth--;
                replicationCropfieldPolicyUiRefreshDepth--;
            }
            return "invalidated=" + refreshed.ToString(CultureInfo.InvariantCulture);
        }

        private static void ResetReplicationCropfieldPolicyV1State()
        {
            replicationCropfieldPolicyAuthoritativeApplyDepth = 0;
            replicationCropfieldPolicyUiRefreshDepth = 0;
            replicationNextCropfieldPolicyHostReconcileRealtime = 0f;
            ReplicationCropfieldPolicyHostSignatures.Clear();
        }
    }
}

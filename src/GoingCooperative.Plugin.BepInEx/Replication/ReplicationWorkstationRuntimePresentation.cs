using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationWorkstationRuntimeDeltaKind = "WorkstationRuntimePresentation";
        private const float ReplicationWorkstationRuntimeSampleSeconds = 0.5f;
        private static readonly Dictionary<string, string> ReplicationWorkstationRuntimeLastHostState =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationWorkstationRuntimeLastClientProgress =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static float replicationNextWorkstationRuntimeSampleRealtime;

        private void UpdateReplicationWorkstationRuntimePresentation()
        {
            if (!replicationConfigWorkstationRuntimePresentation
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || IsReplicationWorldObjectDeltaModeOff()
                || Time.realtimeSinceStartup < replicationNextWorkstationRuntimeSampleRealtime)
            {
                return;
            }

            replicationNextWorkstationRuntimeSampleRealtime = Time.realtimeSinceStartup + ReplicationWorkstationRuntimeSampleSeconds;
            var viewType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
            var componentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.ProductionComponentInstance");
            if (viewType == null || componentType == null) return;

            var views = Resources.FindObjectsOfTypeAll(viewType);
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || !TryResolveReplicationBuildingCandidateInstance(view, out var building, out _) || building == null) continue;
                if (!TryGetReplicationProductionSystem(building, componentType, out var system) || system == null) continue;
                if (!TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _)
                    || !TryGetListMember(system, "productions", out var queue)) continue;

                // The game exposes one physical progress display per
                // workstation. CurrentStep.IsActive is not exclusive across
                // queued tickets, so use the system's authoritative current
                // ticket identity instead.
                var currentTicket = AccessTools.Property(system.GetType(), "CurrentProduction")?.GetValue(system, null);
                if (currentTicket == null) continue;

                for (var ticketIndex = 0; ticketIndex < queue.Count; ticketIndex++)
                {
                    var ticket = queue[ticketIndex];
                    if (ticket == null || !ReferenceEquals(ticket, currentTicket)) continue;
                    var state = Convert.ToString(AccessTools.Property(ticket.GetType(), "State")?.GetValue(ticket, null), CultureInfo.InvariantCulture) ?? "None";
                    var stepIndex = ReadIntMember(ticket, "CurrentStepIndex", "currentStepIndex");
                    var progress = 0f;
                    var step = AccessTools.Property(ticket.GetType(), "CurrentStep")?.GetValue(ticket, null);
                    if (step != null)
                    {
                        var raw = AccessTools.Property(step.GetType(), "Progress")?.GetValue(step, null);
                        if (raw != null) progress = Mathf.Clamp01(Convert.ToSingle(raw, CultureInfo.InvariantCulture));
                    }

                    var key = x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture) + ":" + ticketIndex.ToString(CultureInfo.InvariantCulture);
                    var permille = Mathf.RoundToInt(progress * 1000f);
                    var fingerprint = state + ":" + stepIndex.ToString(CultureInfo.InvariantCulture) + ":" + permille.ToString(CultureInfo.InvariantCulture);
                    if (ReplicationWorkstationRuntimeLastHostState.TryGetValue(key, out var previous) && string.Equals(previous, fingerprint, StringComparison.Ordinal)) continue;
                    ReplicationWorkstationRuntimeLastHostState[key] = fingerprint;

                    SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                        ++replicationWorldObjectDeltaSequence,
                        Time.realtimeSinceStartup,
                        ReplicationWorkstationRuntimeDeltaKind,
                        0,
                        string.Empty,
                        x, y, z,
                        "x=" + x.ToString(CultureInfo.InvariantCulture)
                            + " y=" + y.ToString(CultureInfo.InvariantCulture)
                            + " z=" + z.ToString(CultureInfo.InvariantCulture)
                            + " ticketIndex=" + ticketIndex.ToString(CultureInfo.InvariantCulture)
                            + " state=" + FormatReplicationWorldObjectDetailToken(state)
                            + " active=true"
                            + " stepIndex=" + stepIndex.ToString(CultureInfo.InvariantCulture)
                            + " progressPermille=" + permille.ToString(CultureInfo.InvariantCulture)
                            + " source=host-production-runtime"));
                }
            }
        }

        private static bool TryGetReplicationProductionSystem(object building, Type componentType, out object? system)
        {
            system = null;
            foreach (var method in building.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "GetComponentInstance" || !method.IsGenericMethodDefinition) continue;
                var component = method.MakeGenericMethod(componentType).Invoke(building, null);
                system = component == null ? null : AccessTools.Property(componentType, "ProductionSystemInstance")?.GetValue(component, null);
                return system != null;
            }
            return false;
        }

        private static bool TryApplyReplicationWorkstationRuntimePresentation(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigWorkstationRuntimePresentation)
            {
                detail = "workstation-runtime-gated-off";
                return true;
            }
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "x", out var x)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "y", out var y)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "z", out var z)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "ticketIndex", out var ticketIndex)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "progressPermille", out var permille))
            {
                detail = "workstation-runtime-payload-invalid";
                return false;
            }

            var progress = Mathf.Clamp01(permille / 1000f);
            var key = FormatReplicationWorkstationRuntimeKey(x, y, z, ticketIndex);
            ReplicationWorkstationRuntimeLastClientProgress[key] = progress;
            var viewType = AccessTools.TypeByName("NSMedieval.UI.ProductionLayoutItemView");
            if (viewType == null)
            {
                detail = "workstation-runtime-ui-type-missing";
                return true;
            }

            var views = Resources.FindObjectsOfTypeAll(viewType);
            var updated = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || !TryReadInstanceMemberValue(view, "production", out var ticket) || ticket == null
                    || !TryReadInstanceMemberValue(ticket, "ownerSystem", out var system) || system == null
                    || !TryResolveProductionSystemGrid(system, out var tx, out var ty, out var tz, out _)
                    || tx != x || ty != y || tz != z || !TryGetListMember(system, "productions", out var queue) || queue.IndexOf(ticket) != ticketIndex)
                {
                    continue;
                }

                ApplyReplicationWorkstationTicketProjection(view, progress);
                updated++;
            }
            var circleDetail = ApplyReplicationWorkstationProductionCircle(x, y, z, ticketIndex, progress);
            detail = "ok workstation-runtime projected=" + updated.ToString(CultureInfo.InvariantCulture)
                + " progressPermille=" + permille.ToString(CultureInfo.InvariantCulture)
                + " " + circleDetail;
            return true;
        }

        // ProductionLayoutItemView normally derives this value from the
        // client's non-authoritative ProductionInstance every UI refresh.  In
        // this gated lane that local value is stale, so it must not overwrite
        // the host projection between packets.
        private static bool ReplicationWorkstationRuntimeProgressBarPrefix()
        {
            return !replicationConfigWorkstationRuntimePresentation
                || replicationConfigHostMode
                || applyingRuntimeCommandDepth > 0;
        }

        // A newly opened row initializes itself from the client-side ticket
        // before the next network sample. Reapply the cached host value after
        // either native setup route so it never visibly flashes 100%.
        private static void ReplicationWorkstationRuntimeItemViewPostfix(object __instance)
        {
            if (!replicationConfigWorkstationRuntimePresentation || replicationConfigHostMode || __instance == null
                || !TryReadInstanceMemberValue(__instance, "production", out var ticket) || ticket == null
                || !TryReadInstanceMemberValue(ticket, "ownerSystem", out var system) || system == null
                || !TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _)
                || !TryGetListMember(system, "productions", out var queue))
            {
                return;
            }

            var ticketIndex = queue.IndexOf(ticket);
            if (ticketIndex < 0 || !ReplicationWorkstationRuntimeLastClientProgress.TryGetValue(
                    FormatReplicationWorkstationRuntimeKey(x, y, z, ticketIndex), out var progress))
            {
                return;
            }

            ApplyReplicationWorkstationTicketProjection(__instance, progress);
        }

        private static void ApplyReplicationWorkstationTicketProjection(object view, float progress)
        {
            var slider = AccessTools.Property(view.GetType(), "GetProgressBar")?.GetValue(view, null);
            var sliderValue = slider == null ? null : AccessTools.Property(slider.GetType(), "value");
            sliderValue?.SetValue(slider, progress, null);
            var text = AccessTools.Property(view.GetType(), "ProgressBarText")?.GetValue(view, null);
            var textValue = text == null ? null : AccessTools.Property(text.GetType(), "text");
            textValue?.SetValue(text, Mathf.RoundToInt(progress * 100f).ToString(CultureInfo.InvariantCulture) + "%", null);
        }

        private static string FormatReplicationWorkstationRuntimeKey(int x, int y, int z, int ticketIndex)
        {
            return x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture)
                + ":" + ticketIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static string ApplyReplicationWorkstationProductionCircle(int x, int y, int z, int ticketIndex, float progress)
        {
            var componentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.ProductionComponent");
            if (componentType == null)
            {
                return "production-circle-component-type-missing";
            }

            try
            {
                var components = Resources.FindObjectsOfTypeAll(componentType);
                for (var i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null
                        || !TryReadInstanceMemberValue(component, "componentInstance", out var componentInstance) || componentInstance == null
                        || !TryReadInstanceMemberValue(componentInstance, "ProductionSystemInstance", out var system) || system == null
                        || !TryResolveProductionSystemGrid(system, out var sx, out var sy, out var sz, out _)
                        || sx != x || sy != y || sz != z
                        || !TryGetListMember(system, "productions", out var queue)
                        || ticketIndex < 0 || ticketIndex >= queue.Count
                        || !ReferenceEquals(AccessTools.Property(system.GetType(), "CurrentProduction")?.GetValue(system, null), queue[ticketIndex]))
                    {
                        continue;
                    }

                    var ticket = queue[ticketIndex];
                    var step = ticket == null ? null : AccessTools.Property(ticket.GetType(), "CurrentStep")?.GetValue(ticket, null);
                    if (step == null) return "production-circle-step-missing";
                    var stepType = step.GetType();
                    var projected = false;
                    if (stepType.Name == "ProductionStepWorker")
                    {
                        var duration = AccessTools.Property(stepType, "ProductionTime")?.GetValue(step, null);
                        var field = AccessTools.Field(stepType, "workDone");
                        if (duration != null && field != null)
                        {
                            field.SetValue(step, progress * Mathf.Max(0f, Convert.ToSingle(duration, CultureInfo.InvariantCulture)));
                            projected = true;
                        }
                    }
                    else if (stepType.Name == "ProductionStepPassive")
                    {
                        var blueprint = AccessTools.Property(stepType.BaseType, "Blueprint")?.GetValue(step, null);
                        var duration = blueprint == null ? null : AccessTools.Property(blueprint.GetType(), "ProductionTime")?.GetValue(blueprint, null);
                        var field = AccessTools.Field(stepType, "timePassed");
                        if (duration != null && field != null)
                        {
                            field.SetValue(step, progress * Mathf.Max(0f, Convert.ToSingle(duration, CultureInfo.InvariantCulture)));
                            projected = true;
                        }
                    }

                    if (stepType.Name == "ProductionStepCollect")
                    {
                        // Collect progress is derived from the building's local
                        // ingredient storage, which is not authoritative on the
                        // client. Write the same shader values vanilla uses for
                        // WaitingForResources, rather than fabricating storage.
                        var renderer = AccessTools.Field(component.GetType(), "productionCircle")?.GetValue(component) as MeshRenderer;
                        if (renderer == null) return "production-circle-collect-renderer-missing";
                        var block = AccessTools.Field(component.GetType(), "circleMaterialBlock")?.GetValue(component) as MaterialPropertyBlock
                            ?? new MaterialPropertyBlock();
                        block.SetFloat("_Warning", 0f);
                        block.SetFloat("_CompleteFillbar", 0f);
                        block.SetFloat("_ResourceFillbar", progress);
                        block.SetFloat("_PausedFillbar", 0f);
                        renderer.gameObject.SetActive(true);
                        renderer.SetPropertyBlock(block);
                        AccessTools.Field(component.GetType(), "circleMaterialBlock")?.SetValue(component, block);
                        return "production-circle-collect-updated";
                    }

                    if (!projected) return "production-circle-step-unsupported type=" + stepType.Name;
                    var update = FindReplicationInstanceMethod(component.GetType(), "UpdateProductionCircle", Type.EmptyTypes);
                    if (update == null) return "production-circle-update-method-missing";
                    applyingRuntimeCommandDepth++;
                    try
                    {
                        update.Invoke(component, null);
                        return "production-circle-updated";
                    }
                    finally
                    {
                        applyingRuntimeCommandDepth--;
                    }
                }

                return "production-circle-component-pending";
            }
            catch (Exception ex)
            {
                return "production-circle-error " + FormatReflectionExceptionDetail(ex);
            }
        }

        private static void ResetReplicationWorkstationRuntimePresentation()
        {
            replicationNextWorkstationRuntimeSampleRealtime = 0f;
            ReplicationWorkstationRuntimeLastHostState.Clear();
            ReplicationWorkstationRuntimeLastClientProgress.Clear();
        }
    }
}

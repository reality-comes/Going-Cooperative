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
        private const int ReplicationPendingWorkerJobsDirtyTargetLimit = 128;
        private const float ReplicationWorkerJobsDirtyRetrySeconds = 1f;
        private const float ReplicationWorkerJobsDirtyDormantRetrySeconds = 5f;
        private static int replicationWorkerJobsAuthoritativeApplyDepth;
        private static readonly Dictionary<string, SortedDictionary<int, ReplicationWorkerJobValue>>
            ReplicationPendingWorkerJobChangesByTarget =
                new Dictionary<string, SortedDictionary<int, ReplicationWorkerJobValue>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationHostWorkerJobsIntentSequenceByJob =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationWorkerJobsDirtyRetryAtByTarget =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ReplicationWorkerJobsDirtyFailureCountByTarget =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private sealed class ReplicationWorkerJobsMutationCapture
        {
            public ReplicationWorkerJobsMutationCapture(
                object behaviour,
                string targetId,
                int jobType,
                int previousPriority,
                bool previousActive)
            {
                Behaviour = behaviour;
                TargetId = targetId;
                JobType = jobType;
                PreviousPriority = previousPriority;
                PreviousActive = previousActive;
            }

            public object Behaviour { get; }
            public string TargetId { get; }
            public int JobType { get; }
            public int PreviousPriority { get; }
            public bool PreviousActive { get; }
        }

        private readonly struct ReplicationWorkerJobValue
        {
            public ReplicationWorkerJobValue(int priority, bool active)
            {
                Priority = priority;
                Active = active;
            }

            public int Priority { get; }
            public bool Active { get; }
        }

        private readonly struct ReplicationWorkerJobApplyEntry
        {
            public ReplicationWorkerJobApplyEntry(
                int jobType,
                int previousPriority,
                bool previousActive,
                int desiredPriority,
                bool desiredActive)
            {
                JobType = jobType;
                PreviousPriority = previousPriority;
                PreviousActive = previousActive;
                DesiredPriority = desiredPriority;
                DesiredActive = desiredActive;
            }

            public int JobType { get; }
            public int PreviousPriority { get; }
            public bool PreviousActive { get; }
            public int DesiredPriority { get; }
            public bool DesiredActive { get; }
        }

        private int TryInstallReplicationWorkerJobsCapture(Harmony harmony)
        {
            var jobType = AccessTools.TypeByName("NSMedieval.State.WorkerJobs.JobType");
            if (jobType == null)
            {
                LogReplicationWarning("Going Cooperative worker-jobs JobType surface missing");
                return 0;
            }

            // ModifyJobPriority is the single model boundary reached by cell edits,
            // row/column bulk actions, and every paste path. Capturing the UI click as
            // well would turn one native mutation into two network intents.
            return PatchManagementMethod(
                harmony,
                "NSMedieval.State.WorkerBehaviour",
                "ModifyJobPriority",
                new[] { jobType, typeof(int), typeof(bool) },
                nameof(ReplicationWorkerJobsModelPrefix),
                nameof(ReplicationWorkerJobsModelPostfix));
        }

        private static void ReplicationWorkerJobsModelPrefix(
            object __instance,
            object __0,
            ref ReplicationWorkerJobsMutationCapture? __state)
        {
            __state = null;
            if (replicationWorkerJobsAuthoritativeApplyDepth > 0
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || (!replicationConfigHostMode
                    && (IsReplicationRegionOrderStateCaptureSuppressed()
                        || replicationSnapshotsReceived <= 0
                        || replicationLastSnapshotEntities <= 0))
                || !TryGetReplicationWorkerBehaviourEntityId(__instance, out var targetId, out _))
            {
                return;
            }

            int jobType;
            try
            {
                jobType = Convert.ToInt32(__0, CultureInfo.InvariantCulture);
            }
            catch
            {
                return;
            }

            if (jobType <= 0
                || !TryReadReplicationWorkerJobValue(
                    __instance,
                    jobType,
                    out var previousPriority,
                    out var previousActive,
                    out _))
            {
                return;
            }

            __state = new ReplicationWorkerJobsMutationCapture(
                __instance,
                targetId,
                jobType,
                previousPriority,
                previousActive);
        }

        private static void ReplicationWorkerJobsModelPostfix(
            ReplicationWorkerJobsMutationCapture? __state)
        {
            var readDetail = string.Empty;
            if (__state == null
                || !TryReadReplicationWorkerJobValue(
                    __state.Behaviour,
                    __state.JobType,
                    out var actualPriority,
                    out var actualActive,
                    out readDetail))
            {
                if (__state != null)
                {
                    instance?.LogReplicationWarning(
                        "Going Cooperative worker-jobs post-state read failed target="
                        + __state.TargetId + " job="
                        + __state.JobType.ToString(CultureInfo.InvariantCulture)
                        + " detail=" + readDetail);
                }
                return;
            }

            if (actualPriority == __state.PreviousPriority
                && actualActive == __state.PreviousActive)
            {
                return;
            }

            if (!ReplicationPendingWorkerJobChangesByTarget.TryGetValue(
                    __state.TargetId,
                    out var changes))
            {
                if (ReplicationPendingWorkerJobChangesByTarget.Count
                    >= ReplicationPendingWorkerJobsDirtyTargetLimit)
                {
                    instance?.LogReplicationWarning(
                        "Going Cooperative worker-jobs dirty target cap reached target="
                        + __state.TargetId);
                    return;
                }

                changes = new SortedDictionary<int, ReplicationWorkerJobValue>();
                ReplicationPendingWorkerJobChangesByTarget.Add(__state.TargetId, changes);
            }

            // A complete native gesture is synchronous. Keeping only the final value
            // per cell until the next runtime pump batches cell, row, column, and paste
            // storms without reimplementing any vanilla UI flow.
            changes[__state.JobType] = new ReplicationWorkerJobValue(actualPriority, actualActive);
            ReplicationWorkerJobsDirtyRetryAtByTarget.Remove(__state.TargetId);
            ReplicationWorkerJobsDirtyFailureCountByTarget.Remove(__state.TargetId);
        }

        private static void FlushReplicationWorkerJobsChanges()
        {
            if (ReplicationPendingWorkerJobChangesByTarget.Count == 0
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || instance == null
                || (!replicationConfigHostMode && !ShouldSendReplicationLocalCommandIntent()))
            {
                return;
            }

            var targets = new List<string>(ReplicationPendingWorkerJobChangesByTarget.Keys);
            for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                var targetId = targets[targetIndex];
                if (ReplicationWorkerJobsDirtyRetryAtByTarget.TryGetValue(
                        targetId,
                        out var retryAt)
                    && Time.realtimeSinceStartup < retryAt)
                {
                    continue;
                }
                if (!ReplicationPendingWorkerJobChangesByTarget.TryGetValue(targetId, out var changes)
                    || changes.Count == 0)
                {
                    ReplicationPendingWorkerJobChangesByTarget.Remove(targetId);
                    ReplicationWorkerJobsDirtyRetryAtByTarget.Remove(targetId);
                    ReplicationWorkerJobsDirtyFailureCountByTarget.Remove(targetId);
                    continue;
                }

                if (replicationConfigHostMode)
                {
                    if (!TryCreateReplicationWorkerJobsStatePayload(
                            targetId,
                            out var statePayload,
                            out var stateDetail))
                    {
                        ReplicationWorkerJobsDirtyFailureCountByTarget.TryGetValue(
                            targetId,
                            out var failureCount);
                        failureCount++;
                        ReplicationWorkerJobsDirtyFailureCountByTarget[targetId] = failureCount;
                        ReplicationWorkerJobsDirtyRetryAtByTarget[targetId] =
                            Time.realtimeSinceStartup
                            + (failureCount >= 5
                                ? ReplicationWorkerJobsDirtyDormantRetrySeconds
                                : ReplicationWorkerJobsDirtyRetrySeconds);
                        if (failureCount == 1 || (failureCount & (failureCount - 1)) == 0)
                        {
                            instance?.LogReplicationWarning(
                                "Going Cooperative worker-jobs dirty state read failed; retained for retry target="
                                + targetId
                                + " failures=" + failureCount.ToString(CultureInfo.InvariantCulture)
                                + " detail=" + stateDetail);
                        }
                        continue;
                    }

                    ReplicationPendingWorkerJobChangesByTarget.Remove(targetId);
                    ReplicationWorkerJobsDirtyRetryAtByTarget.Remove(targetId);
                    ReplicationWorkerJobsDirtyFailureCountByTarget.Remove(targetId);
                    instance?.SendReplicationManagementDelta(
                        statePayload,
                        "worker-jobs-model-batch");
                    continue;
                }

                var jobTypes = new int[changes.Count];
                var priorities = new int[changes.Count];
                var active = new bool[changes.Count];
                var index = 0;
                foreach (var pair in changes)
                {
                    jobTypes[index] = pair.Key;
                    priorities[index] = pair.Value.Priority;
                    active[index] = pair.Value.Active;
                    index++;
                }

                ReplicationPendingWorkerJobChangesByTarget.Remove(targetId);
                ReplicationWorkerJobsDirtyRetryAtByTarget.Remove(targetId);
                ReplicationWorkerJobsDirtyFailureCountByTarget.Remove(targetId);
                SendReplicationManagementIntent(
                    LockstepCommandPayloads.CreateWorkerJobsUpdatePayload(
                        targetId,
                        jobTypes,
                        priorities,
                        active),
                    "worker-jobs-model-batch");
            }
        }

        private static bool TryReadReplicationWorkerJobValue(
            object behaviour,
            int jobType,
            out int priority,
            out bool active,
            out string detail)
        {
            priority = 0;
            active = false;
            var nativeJobType = AccessTools.TypeByName("NSMedieval.State.WorkerJobs.JobType");
            var getPriority = nativeJobType == null
                ? null
                : AccessTools.Method(
                    behaviour.GetType(),
                    "GetJobPriorityTruncated",
                    new[] { nativeJobType });
            var isActive = nativeJobType == null
                ? null
                : AccessTools.Method(
                    behaviour.GetType(),
                    "IsJobActive",
                    new[] { nativeJobType });
            if (nativeJobType == null || getPriority == null || isActive == null)
            {
                detail = "worker-jobs-read-surface-missing";
                return false;
            }

            try
            {
                var nativeValue = Enum.ToObject(nativeJobType, jobType);
                priority = Convert.ToInt32(
                    getPriority.Invoke(behaviour, new[] { nativeValue }),
                    CultureInfo.InvariantCulture);
                active = Convert.ToBoolean(
                    isActive.Invoke(behaviour, new[] { nativeValue }),
                    CultureInfo.InvariantCulture);
                if (priority < LockstepCommandPayloads.MinimumWorkerJobPriority
                    || priority > LockstepCommandPayloads.MaximumWorkerJobPriority)
                {
                    detail = "worker-jobs-priority-out-of-contract job="
                        + jobType.ToString(CultureInfo.InvariantCulture)
                        + " priority=" + priority.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                detail = "ok";
                return true;
            }
            catch (Exception ex)
            {
                detail = "worker-jobs-read-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryReadReplicationWorkerJobsState(
            object behaviour,
            out int[] jobTypes,
            out int[] priorities,
            out bool[] active,
            out string detail)
        {
            jobTypes = Array.Empty<int>();
            priorities = Array.Empty<int>();
            active = Array.Empty<bool>();
            if (!TryGetReplicationWorkerJobsCatalog(out var catalog, out detail))
            {
                return false;
            }

            var values = new int[catalog.Length];
            var enabled = new bool[catalog.Length];
            for (var i = 0; i < catalog.Length; i++)
            {
                if (!TryReadReplicationWorkerJobValue(
                        behaviour,
                        catalog[i],
                        out values[i],
                        out enabled[i],
                        out var readDetail))
                {
                    detail = "worker-jobs-state-read-failed job="
                        + catalog[i].ToString(CultureInfo.InvariantCulture)
                        + " " + readDetail;
                    return false;
                }
            }

            jobTypes = catalog;
            priorities = values;
            active = enabled;
            detail = "ok jobs=" + catalog.Length.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryGetReplicationWorkerJobsCatalog(
            out int[] jobTypes,
            out string detail)
        {
            jobTypes = Array.Empty<int>();
            var marker = AccessTools.TypeByName("NSMedieval.Repository.JobRepository");
            var model = AccessTools.TypeByName("NSMedieval.Model.Job");
            var repositoryDefinition = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
            if (marker == null || model == null || repositoryDefinition == null)
            {
                detail = "worker-jobs-repository-types-missing";
                return false;
            }

            try
            {
                var repositoryType = repositoryDefinition.MakeGenericType(marker, model);
                var repository = AccessTools.Property(repositoryType, "Instance")?.GetValue(null, null);
                var getWorkerJobs = AccessTools.Method(marker, "GetWorkerJobs", Type.EmptyTypes);
                var jobs = repository == null || getWorkerJobs == null
                    ? null
                    : getWorkerJobs.Invoke(repository, null) as IEnumerable;
                if (jobs == null)
                {
                    detail = "worker-jobs-repository-list-missing";
                    return false;
                }

                var values = new SortedSet<int>();
                foreach (var job in jobs)
                {
                    if (job == null)
                    {
                        continue;
                    }

                    var raw = AccessTools.Property(job.GetType(), "JobType")?.GetValue(job, null);
                    if (raw == null)
                    {
                        detail = "worker-jobs-repository-entry-type-missing";
                        return false;
                    }

                    var value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                    if (value <= 0 || !values.Add(value))
                    {
                        detail = "worker-jobs-repository-entry-invalid-or-duplicate job="
                            + value.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }

                if (values.Count == 0 || values.Count > LockstepCommandPayloads.MaximumWorkerJobs)
                {
                    detail = "worker-jobs-repository-count-invalid count="
                        + values.Count.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                jobTypes = new int[values.Count];
                values.CopyTo(jobTypes);
                detail = "ok";
                return true;
            }
            catch (Exception ex)
            {
                detail = "worker-jobs-repository-read-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryCreateReplicationWorkerJobsStatePayload(
            string targetId,
            out string payload,
            out string detail)
        {
            payload = string.Empty;
            if (!TryResolveReplicationWorkerBehaviour(
                    targetId,
                    out _,
                    out var behaviour,
                    out var workerLookup)
                || behaviour == null)
            {
                detail = "worker-jobs-state-target-missing " + workerLookup;
                return false;
            }

            if (!TryReadReplicationWorkerJobsState(
                    behaviour,
                    out var jobTypes,
                    out var priorities,
                    out var active,
                    out detail))
            {
                return false;
            }

            payload = LockstepCommandPayloads.CreateWorkerJobsStatePayload(
                targetId,
                jobTypes,
                priorities,
                active);
            detail = "ok jobs=" + jobTypes.Length.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryApplyReplicationWorkerJobsUpdate(
            string targetId,
            int[] jobTypes,
            int[] priorities,
            bool[] active,
            out string detail)
        {
            if (!TryResolveReplicationWorkerBehaviour(
                    targetId,
                    out _,
                    out var behaviour,
                    out var workerLookup)
                || behaviour == null)
            {
                detail = "worker-jobs-update-target-missing " + workerLookup;
                return false;
            }

            return TryApplyReplicationWorkerJobsUpdate(
                behaviour,
                targetId,
                jobTypes,
                priorities,
                active,
                applyIntentOrdering: true,
                out detail);
        }

        private static bool TryApplyReplicationWorkerJobsUpdate(
            object behaviour,
            string targetId,
            int[] jobTypes,
            int[] priorities,
            bool[] active,
            bool applyIntentOrdering,
            out string detail)
        {
            detail = string.Empty;
            if (jobTypes == null
                || priorities == null
                || active == null
                || jobTypes.Length == 0
                || jobTypes.Length != priorities.Length
                || jobTypes.Length != active.Length
                || jobTypes.Length > LockstepCommandPayloads.MaximumWorkerJobs
                || !TryGetReplicationWorkerJobsCatalog(out var catalog, out detail))
            {
                detail = "worker-jobs-update-invalid " + detail;
                return false;
            }

            var nativeJobType = AccessTools.TypeByName("NSMedieval.State.WorkerJobs.JobType");
            var modify = nativeJobType == null
                ? null
                : AccessTools.Method(
                    behaviour.GetType(),
                    "ModifyJobPriority",
                    new[] { nativeJobType, typeof(int), typeof(bool) });
            if (nativeJobType == null || modify == null)
            {
                detail = "worker-jobs-modify-surface-missing";
                return false;
            }

            var known = new HashSet<int>(catalog);
            var seen = new HashSet<int>();
            var eligible = new List<ReplicationWorkerJobApplyEntry>();
            var remoteSequence = replicationConfigHostMode && applyIntentOrdering
                ? replicationApplyingRemoteManagementCommandSequence
                : 0L;
            for (var i = 0; i < jobTypes.Length; i++)
            {
                var jobType = jobTypes[i];
                var desiredPriority = priorities[i];
                if (!known.Contains(jobType)
                    || !seen.Add(jobType)
                    || desiredPriority < LockstepCommandPayloads.MinimumWorkerJobPriority
                    || desiredPriority > LockstepCommandPayloads.MaximumWorkerJobPriority)
                {
                    detail = "worker-jobs-update-entry-invalid job="
                        + jobType.ToString(CultureInfo.InvariantCulture)
                        + " priority=" + desiredPriority.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (!TryReadReplicationWorkerJobValue(
                        behaviour,
                        jobType,
                        out var previousPriority,
                        out var previousActive,
                        out var readDetail))
                {
                    detail = "worker-jobs-update-prior-read-failed job="
                        + jobType.ToString(CultureInfo.InvariantCulture)
                        + " " + readDetail;
                    return false;
                }

                var orderingKey = FormatReplicationWorkerJobsIntentOrderingKey(targetId, jobType);
                if (remoteSequence > 0L
                    && ReplicationHostWorkerJobsIntentSequenceByJob.TryGetValue(
                        orderingKey,
                        out var highWater)
                    && remoteSequence <= highWater)
                {
                    continue;
                }

                eligible.Add(new ReplicationWorkerJobApplyEntry(
                    jobType,
                    previousPriority,
                    previousActive,
                    desiredPriority,
                    active[i]));
            }

            if (eligible.Count == 0)
            {
                detail = "ok worker-jobs stale-noop entityId=" + targetId
                    + " sequence=" + remoteSequence.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            var changed = new List<ReplicationWorkerJobApplyEntry>();
            replicationWorkerJobsAuthoritativeApplyDepth++;
            try
            {
                for (var i = 0; i < eligible.Count; i++)
                {
                    var entry = eligible[i];
                    if (entry.PreviousPriority == entry.DesiredPriority
                        && entry.PreviousActive == entry.DesiredActive)
                    {
                        continue;
                    }

                    // ModifyJobPriority accepts a delta against the observable
                    // truncated priority. It stores a separate baseline-relative save
                    // value internally, so adding 5 here would corrupt every row.
                    changed.Add(entry);
                    modify.Invoke(behaviour, new object[]
                    {
                        Enum.ToObject(nativeJobType, entry.JobType),
                        entry.DesiredPriority - entry.PreviousPriority,
                        !entry.DesiredActive
                    });
                }

                for (var i = 0; i < eligible.Count; i++)
                {
                    var entry = eligible[i];
                    if (!TryReadReplicationWorkerJobValue(
                            behaviour,
                            entry.JobType,
                            out var actualPriority,
                            out var actualActive,
                            out var readDetail)
                        || actualPriority != entry.DesiredPriority
                        || actualActive != entry.DesiredActive)
                    {
                        TryRollbackReplicationWorkerJobsUpdate(
                            behaviour,
                            modify,
                            nativeJobType,
                            changed);
                        var uiDetail = RefreshReplicationWorkerJobsUi(targetId);
                        detail = "worker-jobs-update-readback-failed job="
                            + entry.JobType.ToString(CultureInfo.InvariantCulture)
                            + " expectedPriority="
                            + entry.DesiredPriority.ToString(CultureInfo.InvariantCulture)
                            + " actualPriority="
                            + actualPriority.ToString(CultureInfo.InvariantCulture)
                            + " expectedActive=" + entry.DesiredActive.ToString().ToLowerInvariant()
                            + " actualActive=" + actualActive.ToString().ToLowerInvariant()
                            + " read=" + readDetail + " rollbackUi=" + uiDetail;
                        return false;
                    }
                }

                var refreshDetail = RefreshReplicationWorkerJobsUi(targetId);
                if (remoteSequence > 0L)
                {
                    for (var i = 0; i < eligible.Count; i++)
                    {
                        ReplicationHostWorkerJobsIntentSequenceByJob[
                            FormatReplicationWorkerJobsIntentOrderingKey(
                                targetId,
                                eligible[i].JobType)] = remoteSequence;
                    }
                }

                detail = "ok worker-jobs-update entityId=" + targetId
                    + " accepted=" + eligible.Count.ToString(CultureInfo.InvariantCulture)
                    + " changed=" + changed.Count.ToString(CultureInfo.InvariantCulture)
                    + " stale=" + (jobTypes.Length - eligible.Count).ToString(CultureInfo.InvariantCulture)
                    + " ui=" + refreshDetail
                    + " sequence=" + remoteSequence.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                TryRollbackReplicationWorkerJobsUpdate(
                    behaviour,
                    modify,
                    nativeJobType,
                    changed);
                var uiDetail = RefreshReplicationWorkerJobsUi(targetId);
                detail = "worker-jobs-update-apply-failed "
                    + FormatReflectionExceptionDetail(ex)
                    + " rollbackUi=" + uiDetail;
                return false;
            }
            finally
            {
                replicationWorkerJobsAuthoritativeApplyDepth--;
            }
        }

        private static void TryRollbackReplicationWorkerJobsUpdate(
            object behaviour,
            MethodInfo modify,
            Type nativeJobType,
            List<ReplicationWorkerJobApplyEntry> changed)
        {
            for (var i = changed.Count - 1; i >= 0; i--)
            {
                var entry = changed[i];
                try
                {
                    if (!TryReadReplicationWorkerJobValue(
                            behaviour,
                            entry.JobType,
                            out var currentPriority,
                            out _,
                            out _))
                    {
                        continue;
                    }

                    modify.Invoke(behaviour, new object[]
                    {
                        Enum.ToObject(nativeJobType, entry.JobType),
                        entry.PreviousPriority - currentPriority,
                        !entry.PreviousActive
                    });
                }
                catch
                {
                    // The authoritative correction/retry path exposes any residual
                    // drift; preserve the original transaction failure as primary.
                }
            }
        }

        private static bool TryApplyReplicationWorkerJobsState(
            string targetId,
            int[] jobTypes,
            int[] priorities,
            bool[] active,
            out string detail)
        {
            if (!TryResolveReplicationWorkerBehaviour(
                    targetId,
                    out _,
                    out var behaviour,
                    out var workerLookup)
                || behaviour == null)
            {
                detail = "worker-jobs-state-target-missing " + workerLookup;
                return false;
            }

            var catalog = Array.Empty<int>();
            var catalogDetail = string.Empty;
            if (jobTypes == null
                || priorities == null
                || active == null
                || jobTypes.Length != priorities.Length
                || jobTypes.Length != active.Length
                || !TryGetReplicationWorkerJobsCatalog(out catalog, out catalogDetail)
                || jobTypes.Length != catalog.Length)
            {
                detail = "worker-jobs-state-shape-mismatch local="
                    + (catalog?.Length ?? 0).ToString(CultureInfo.InvariantCulture)
                    + " remote=" + (jobTypes?.Length ?? 0).ToString(CultureInfo.InvariantCulture)
                    + " catalog=" + catalogDetail;
                return false;
            }

            var received = new Dictionary<int, ReplicationWorkerJobValue>();
            for (var i = 0; i < jobTypes.Length; i++)
            {
                if (!received.TryAdd(
                        jobTypes[i],
                        new ReplicationWorkerJobValue(priorities[i], active[i])))
                {
                    detail = "worker-jobs-state-duplicate job="
                        + jobTypes[i].ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            var orderedPriorities = new int[catalog.Length];
            var orderedActive = new bool[catalog.Length];
            for (var i = 0; i < catalog.Length; i++)
            {
                if (!received.TryGetValue(catalog[i], out var value))
                {
                    detail = "worker-jobs-state-missing-local-job job="
                        + catalog[i].ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                orderedPriorities[i] = value.Priority;
                orderedActive[i] = value.Active;
            }

            return TryApplyReplicationWorkerJobsUpdate(
                behaviour,
                targetId,
                catalog,
                orderedPriorities,
                orderedActive,
                applyIntentOrdering: false,
                out detail);
        }

        private void SendReplicationWorkerJobsState(
            string targetId,
            string source,
            long originCommandSequence)
        {
            if (!TryCreateReplicationWorkerJobsStatePayload(
                    targetId,
                    out var payload,
                    out var stateDetail))
            {
                LogReplicationWarning(
                    "Going Cooperative worker-jobs state send failed target="
                    + targetId + " detail=" + stateDetail);
                return;
            }

            SendReplicationManagementDelta(
                payload,
                source + ":worker-jobs-state",
                originCommandSequence);
        }

        private static bool CompleteReplicationWorkerJobsStateProof(
            string targetId,
            long originCommandSequence,
            out string detail)
        {
            var completedKeys = new List<string>();
            var newer = new List<PendingReplicationCommandIntent>();
            foreach (var pair in ReplicationPendingCommandIntents)
            {
                var pending = pair.Value;
                if (!LockstepCommandPayloads.TryReadWorkerJobsUpdatePayload(
                        pending.Command.PayloadJson,
                        out var pendingTargetId,
                        out _,
                        out _,
                        out _)
                    || !string.Equals(targetId, pendingTargetId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (originCommandSequence > 0L
                    && pending.Command.Sequence <= originCommandSequence)
                {
                    completedKeys.Add(pair.Key);
                }
                else
                {
                    newer.Add(pending);
                }
            }

            for (var i = 0; i < completedKeys.Count; i++)
            {
                ReplicationPendingCommandIntents.Remove(completedKeys[i]);
            }

            if (newer.Count == 0)
            {
                detail = "ok completed="
                    + completedKeys.Count.ToString(CultureInfo.InvariantCulture)
                    + " newer=0";
                return true;
            }

            newer.Sort((left, right) => left.Command.Sequence.CompareTo(right.Command.Sequence));
            var valuesByJob = new SortedDictionary<int, ReplicationWorkerJobValue>();
            for (var i = 0; i < newer.Count; i++)
            {
                if (!LockstepCommandPayloads.TryReadWorkerJobsUpdatePayload(
                        newer[i].Command.PayloadJson,
                        out _,
                        out var jobTypes,
                        out var priorities,
                        out var active))
                {
                    continue;
                }

                for (var cell = 0; cell < jobTypes.Length; cell++)
                {
                    valuesByJob[jobTypes[cell]] = new ReplicationWorkerJobValue(
                        priorities[cell],
                        active[cell]);
                }
            }

            var overlayTypes = new int[valuesByJob.Count];
            var overlayPriorities = new int[valuesByJob.Count];
            var overlayActive = new bool[valuesByJob.Count];
            var index = 0;
            foreach (var pair in valuesByJob)
            {
                overlayTypes[index] = pair.Key;
                overlayPriorities[index] = pair.Value.Priority;
                overlayActive[index] = pair.Value.Active;
                index++;
            }

            if (overlayTypes.Length > 0
                && !TryApplyReplicationWorkerJobsUpdate(
                    targetId,
                    overlayTypes,
                    overlayPriorities,
                    overlayActive,
                    out var overlayDetail))
            {
                detail = "newer-overlay-failed " + overlayDetail;
                return false;
            }

            detail = "ok completed="
                + completedKeys.Count.ToString(CultureInfo.InvariantCulture)
                + " newer=" + newer.Count.ToString(CultureInfo.InvariantCulture)
                + " overlayJobs=" + overlayTypes.Length.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static string RefreshReplicationWorkerJobsUi(string entityId)
        {
            try
            {
                return RefreshReplicationWorkerJobsUiUnsafe(entityId);
            }
            catch (Exception ex)
            {
                return "refresh-failed:" + FormatReflectionExceptionDetail(ex);
            }
        }

        private static string RefreshReplicationWorkerJobsUiUnsafe(string entityId)
        {
            var managerType = AccessTools.TypeByName("NSMedieval.UI.WorkerJobManager");
            var updateToggles = managerType == null
                ? null
                : AccessTools.Method(managerType, "UpdateToggles", Type.EmptyTypes);
            var clampPriority = managerType == null
                ? null
                : AccessTools.Method(managerType, "ClampPriority");
            if (managerType == null || updateToggles == null)
            {
                return "surface-missing";
            }

            var refreshed = 0;
            var failed = 0;
            var managers = Resources.FindObjectsOfTypeAll(managerType);
            for (var i = 0; i < managers.Length; i++)
            {
                var manager = managers[i];
                if (manager == null
                    || !TryGetWorkerPanelHumanoid(manager, out var humanoid)
                    || humanoid == null
                    || !TryGetReplicationAgentOwnerEntityId(
                        humanoid,
                        out var rowEntityId,
                        out _)
                    || !string.Equals(rowEntityId, entityId, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    updateToggles.Invoke(manager, null);
                    RefreshReplicationWorkerJobsBulkButtons(manager, clampPriority);
                    refreshed++;
                }
                catch
                {
                    failed++;
                }
            }

            return "rows:" + refreshed.ToString(CultureInfo.InvariantCulture)
                + " failed:" + failed.ToString(CultureInfo.InvariantCulture);
        }

        private static void RefreshReplicationWorkerJobsBulkButtons(
            object manager,
            MethodInfo? clampPriority)
        {
            if (clampPriority == null
                || !TryReadInstanceMemberValue(manager, "workerSkills", out var skills)
                || !(skills is IEnumerable entries))
            {
                return;
            }

            var canMoveDown = false;
            var canMoveUp = false;
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                var jobType = AccessTools.Property(entry.GetType(), "Key")?.GetValue(entry, null);
                if (jobType == null)
                {
                    continue;
                }

                canMoveDown |= !Convert.ToBoolean(
                    clampPriority.Invoke(manager, new[] { jobType, (object)(-1) }),
                    CultureInfo.InvariantCulture);
                canMoveUp |= !Convert.ToBoolean(
                    clampPriority.Invoke(manager, new[] { jobType, (object)1 }),
                    CultureInfo.InvariantCulture);
            }

            SetReplicationWorkerJobsButtonInteractable(manager, "allDownButton", canMoveDown);
            SetReplicationWorkerJobsButtonInteractable(manager, "allUpButton", canMoveUp);
        }

        private static void SetReplicationWorkerJobsButtonInteractable(
            object manager,
            string fieldName,
            bool interactable)
        {
            if (!TryReadInstanceMemberValue(manager, fieldName, out var button)
                || button == null)
            {
                return;
            }

            var property = AccessTools.Property(button.GetType(), "interactable");
            if (property != null && property.CanWrite)
            {
                property.SetValue(button, interactable, null);
            }
        }

        private static string FormatReplicationWorkerJobsIntentOrderingKey(
            string targetId,
            int jobType)
        {
            return targetId + "|job=" + jobType.ToString(CultureInfo.InvariantCulture);
        }

        private static void ResetReplicationWorkerJobsRuntimeState()
        {
            replicationWorkerJobsAuthoritativeApplyDepth = 0;
            ReplicationPendingWorkerJobChangesByTarget.Clear();
            ReplicationHostWorkerJobsIntentSequenceByJob.Clear();
            ReplicationWorkerJobsDirtyRetryAtByTarget.Clear();
            ReplicationWorkerJobsDirtyFailureCountByTarget.Clear();
        }
    }
}

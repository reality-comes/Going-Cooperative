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
        private const string WeatherRegistryBeginDeltaKind = "WeatherRegistryBegin";
        private const string WeatherStateDeltaKind = "WeatherState";
        private const string WeatherForecastPageDeltaKind = "WeatherForecastPage";
        private const string WeatherRegistryEndDeltaKind = "WeatherRegistryEnd";
        private const string WeatherEnvironmentStateDeltaKind = "WeatherEnvironmentState";
        private const int ReplicationWeatherForecastPageHours = 24;
        private const float ReplicationWeatherCheckpointSeconds = 30f;
        private const float ReplicationWeatherEnvironmentSeconds = 1f;

        private static readonly Dictionary<string, object> ReplicationClientWeatherInstances =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationClientWeatherCheckpointSeen =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<int> ReplicationClientWeatherForecastPagesSeen =
            new HashSet<int>();
        private static long replicationWeatherCheckpointSequence;
        private static long replicationWeatherClientCheckpointId = -1L;
        private static long replicationWeatherClientCompletedCheckpointId = -1L;
        private static long replicationLastWeatherEnvironmentAppliedSequence = -1L;
        private static int replicationWeatherClientExpectedSchedules;
        private static int replicationWeatherClientExpectedForecastPages;
        private static int replicationWeatherClientExpectedForecastHours;
        private static float replicationNextWeatherCheckpointRealtime;
        private static float replicationNextWeatherEnvironmentRealtime;
        private static string replicationLastWeatherScheduleSignature = string.Empty;
        private static string replicationLastWeatherEnvironmentSignature = string.Empty;
        private static float replicationLastWeatherFullCheckpointRealtime = -120f;
        private static float replicationLastWeatherEnvironmentSendRealtime = -30f;
        private static long replicationWeatherStatesSent;
        private static long replicationWeatherStatesApplied;
        private static long replicationWeatherMutationsSuppressed;
        private static string replicationLastWeatherSummary = string.Empty;
        private static bool replicationWeatherHooksReady;
        private static bool replicationClientWeatherAuthorityParked;

        private int TryInstallReplicationWeatherHooks(Harmony harmonyInstance)
        {
            var schedulePrefix = EventHarmony(nameof(ReplicationWeatherScheduleMutationPrefix));
            var schedulePostfix = EventHarmony(nameof(ReplicationWeatherScheduleMutationPostfix));
            var forceStartPrefix = EventHarmony(nameof(ReplicationWeatherForceStartPrefix));
            var forceStartPostfix = EventHarmony(nameof(ReplicationWeatherForceStartPostfix));
            var effectorPrefix = EventHarmony(nameof(ReplicationWeatherEffectorPrefix));
            var lifecyclePostfix = EventHarmony(nameof(ReplicationWeatherLifecyclePostfix));
            var loadedPostfix = EventHarmony(nameof(ReplicationWeatherLoadedPostfix));
            var count = 0;
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.Manager.WeatherManager", "AddEventsForSeason", new[]
            {
                EventType("NSMedieval.Season")
            }, schedulePrefix, schedulePostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.Manager.WeatherManager", "ForceStartEvent", new[]
            {
                typeof(string), typeof(long), typeof(long), typeof(bool)
            }, forceStartPrefix, forceStartPostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.Manager.WeatherManager", "OnStartEvent", new[]
            {
                EventType("NSMedieval.Weather.WeatherEventInstance")
            }, null, lifecyclePostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.Manager.WeatherManager", "OnEndEvent", new[]
            {
                EventType("NSMedieval.Weather.WeatherEventInstance")
            }, null, lifecyclePostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.Manager.WeatherManager", "OnGameLoaded", new[] { typeof(bool) }, null, loadedPostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.Weather.WeatherEventInstance", "RunEffectors", new[] { typeof(bool) }, effectorPrefix, null);
            replicationWeatherHooksReady = count == 6;
            if (!replicationWeatherHooksReady)
                LogReplicationWarning("Going Cooperative weather authority failed closed because critical hooks are incomplete expected=6 actual=" + count.ToString(CultureInfo.InvariantCulture));
            return count;
        }

        private static bool WeatherScheduleLaneEnabled()
        {
            return replicationConfigWeatherReplication
                && replicationConfigWeatherSchedulerAuthority
                && replicationConfigEventEnvironmentMutationReplication
                && ReplicationEventEnvironmentMutationImplemented()
                && replicationWeatherHooksReady
                && string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase);
        }

        private static bool WeatherEnvironmentLaneEnabled()
        {
            return replicationConfigWeatherReplication
                && replicationConfigWeatherTemperatureReplication
                && replicationWeatherHooksReady
                && string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSuppressReplicationClientWeatherMutation()
        {
            if (replicationConfigHostMode
                || !WeatherScheduleLaneEnabled()
                || replicationEventApplicationDepth > 0)
            {
                return false;
            }
            if (multiplayerLoadingInProgress) return true;
            return replicationClientWeatherAuthorityParked
                || (replicationConfigEnabled && replicationRuntimeStarted);
        }

        private static bool ReplicationWeatherScheduleMutationPrefix()
        {
            if (!ShouldSuppressReplicationClientWeatherMutation()) return true;
            replicationWeatherMutationsSuppressed++;
            return false;
        }

        private static void ReplicationWeatherScheduleMutationPostfix()
        {
            if (!replicationConfigHostMode || !WeatherScheduleLaneEnabled()) return;
            replicationNextWeatherCheckpointRealtime = 0f;
        }

        private static bool ReplicationWeatherForceStartPrefix()
        {
            if (!ShouldSuppressReplicationClientWeatherMutation()) return true;
            replicationWeatherMutationsSuppressed++;
            return false;
        }

        private static void ReplicationWeatherForceStartPostfix()
        {
            if (!replicationConfigHostMode || !WeatherScheduleLaneEnabled()) return;
            replicationNextWeatherCheckpointRealtime = 0f;
        }

        private static bool ReplicationWeatherEffectorPrefix()
        {
            if (replicationConfigHostMode
                || !WeatherScheduleLaneEnabled())
            {
                return true;
            }
            if (!multiplayerLoadingInProgress
                && !replicationClientWeatherAuthorityParked
                && (!replicationConfigEnabled || !replicationRuntimeStarted)) return true;
            replicationWeatherMutationsSuppressed++;
            return false;
        }

        private static void ReplicationWeatherLifecyclePostfix()
        {
            if (!replicationConfigHostMode || !WeatherScheduleLaneEnabled()) return;
            replicationNextWeatherCheckpointRealtime = 0f;
        }

        private static void ReplicationWeatherLoadedPostfix()
        {
            if (!replicationConfigHostMode || !WeatherScheduleLaneEnabled()) return;
            replicationNextWeatherCheckpointRealtime = 0f;
        }

        private void ProcessReplicationWeatherRuntime()
        {
            if (!replicationConfigHostMode) return;
            if (WeatherScheduleLaneEnabled() && Time.realtimeSinceStartup >= replicationNextWeatherCheckpointRealtime)
            {
                replicationNextWeatherCheckpointRealtime = Time.realtimeSinceStartup + ReplicationWeatherCheckpointSeconds;
                SendHostReplicationWeatherCheckpoint("periodic");
            }
            if (WeatherEnvironmentLaneEnabled() && Time.realtimeSinceStartup >= replicationNextWeatherEnvironmentRealtime)
            {
                replicationNextWeatherEnvironmentRealtime = Time.realtimeSinceStartup + ReplicationWeatherEnvironmentSeconds;
                SendHostReplicationWeatherEnvironmentState();
            }
        }

        private void SendHostReplicationWeatherCheckpoint(string source)
        {
            if (!WeatherScheduleLaneEnabled() || !replicationRemoteHelloReceived) return;
            EnsureReplicationEventHostScope();
            if (!TryResolveReplicationWeatherManager(out var manager) || manager == null
                || !TryGetReplicationWeatherScheduledEvents(manager, out var scheduled)
                || !TryGetReplicationWeatherForecast(manager, out var forecast))
            {
                return;
            }

            var signature = BuildReplicationWeatherScheduleSignature(scheduled, forecast);
            if (string.Equals(signature, replicationLastWeatherScheduleSignature, StringComparison.Ordinal)
                && !string.Equals(source, "hello", StringComparison.Ordinal)
                && Time.realtimeSinceStartup - replicationLastWeatherFullCheckpointRealtime < 120f)
            {
                return;
            }
            replicationLastWeatherScheduleSignature = signature;
            replicationLastWeatherFullCheckpointRealtime = Time.realtimeSinceStartup;
            var snapshotId = ++replicationWeatherCheckpointSequence;
            var pageCount = (forecast.Length + ReplicationWeatherForecastPageHours - 1) / ReplicationWeatherForecastPageHours;
            var scheduleRows = new List<ReplicationWeatherScheduleRow>();
            var scheduleOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < scheduled.Count; i++)
            {
                var weather = scheduled[i];
                if (weather != null && TryReadReplicationWeatherTuple(weather, out var rowBlueprintId, out var rowStart, out var rowEnd))
                {
                    var tupleKey = FormatReplicationWeatherKey(rowBlueprintId, rowStart, rowEnd);
                    scheduleOccurrences.TryGetValue(tupleKey, out var occurrence);
                    scheduleOccurrences[tupleKey] = occurrence + 1;
                    scheduleRows.Add(new ReplicationWeatherScheduleRow(rowBlueprintId, rowStart, rowEnd, occurrence));
                }
            }
            SendReplicationEventDelta(
                WeatherRegistryBeginDeltaKind,
                0L,
                string.Empty,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " scheduleCount=" + scheduleRows.Count.ToString(CultureInfo.InvariantCulture)
                    + " forecastPages=" + pageCount.ToString(CultureInfo.InvariantCulture)
                    + " forecastHours=" + forecast.Length.ToString(CultureInfo.InvariantCulture)
                    + " source=" + FormatReplicationWorldObjectDetailToken(source));

            for (var i = 0; i < scheduleRows.Count; i++)
            {
                var row = scheduleRows[i];
                SendReplicationEventDelta(
                    WeatherStateDeltaKind,
                    row.Start,
                    row.BlueprintId,
                    "scope=" + replicationEventHostSessionNonce
                        + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                        + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " start=" + row.Start.ToString(CultureInfo.InvariantCulture)
                        + " end=" + row.End.ToString(CultureInfo.InvariantCulture)
                        + " occurrence=" + row.Occurrence.ToString(CultureInfo.InvariantCulture));
                replicationWeatherStatesSent++;
            }

            for (var page = 0; page < pageCount; page++)
            {
                var data = EncodeReplicationWeatherForecastPage(forecast, page * ReplicationWeatherForecastPageHours, ReplicationWeatherForecastPageHours);
                SendReplicationEventDelta(
                    WeatherForecastPageDeltaKind,
                    page,
                    string.Empty,
                    "scope=" + replicationEventHostSessionNonce
                        + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                        + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " page=" + page.ToString(CultureInfo.InvariantCulture)
                        + " dataB64=" + EncodeReplicationDetailBase64(data));
                replicationWeatherStatesSent++;
            }

            SendReplicationEventDelta(
                WeatherRegistryEndDeltaKind,
                0L,
                string.Empty,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " scheduleCount=" + scheduleRows.Count.ToString(CultureInfo.InvariantCulture)
                    + " forecastPages=" + pageCount.ToString(CultureInfo.InvariantCulture));
            replicationLastWeatherSummary = "checkpoint-sent id=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " schedules=" + scheduleRows.Count.ToString(CultureInfo.InvariantCulture)
                + " pages=" + pageCount.ToString(CultureInfo.InvariantCulture);
        }

        private void SendHostReplicationWeatherEnvironmentState()
        {
            if (!WeatherEnvironmentLaneEnabled() || !replicationRemoteHelloReceived) return;
            EnsureReplicationEventHostScope();
            if (!TryResolveReplicationWeatherManager(out var manager) || manager == null) return;
            if (!TryReadReplicationWeatherEnvironment(manager, out var state)) return;
            var signature = state.ToString();
            if (string.Equals(signature, replicationLastWeatherEnvironmentSignature, StringComparison.Ordinal)
                && Time.realtimeSinceStartup - replicationLastWeatherEnvironmentSendRealtime < 30f) return;
            replicationLastWeatherEnvironmentSignature = signature;
            replicationLastWeatherEnvironmentSendRealtime = Time.realtimeSinceStartup;
            SendReplicationEventDelta(
                WeatherEnvironmentStateDeltaKind,
                0L,
                string.Empty,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " temperature=" + state.Temperature.ToString("R", CultureInfo.InvariantCulture)
                    + " soil=" + state.SoilTemperature.ToString("R", CultureInfo.InvariantCulture)
                    + " water=" + state.WaterTemperature.ToString("R", CultureInfo.InvariantCulture)
                    + " overrideActive=" + FormatReplicationBool(state.OverrideActive)
                    + " overrideTemperature=" + state.OverrideTemperature.ToString("R", CultureInfo.InvariantCulture)
                    + " overrideSun=" + state.OverrideSunStrength.ToString("R", CultureInfo.InvariantCulture)
                    + " overrideRain=" + state.OverrideRainWeight.ToString("R", CultureInfo.InvariantCulture));
            replicationWeatherStatesSent++;
        }

        private static bool IsReplicationWeatherDeltaKind(string deltaKind)
        {
            return string.Equals(deltaKind, WeatherRegistryBeginDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, WeatherStateDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, WeatherForecastPageDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, WeatherRegistryEndDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, WeatherEnvironmentStateDeltaKind, StringComparison.Ordinal);
        }

        private static bool TryApplyReplicationWeatherWorldDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (replicationConfigHostMode)
            {
                detail = "weather-delta-ignored-on-host";
                return false;
            }
            if (string.Equals(delta.DeltaKind, WeatherEnvironmentStateDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationWeatherEnvironment(delta, out detail);
            if (!WeatherScheduleLaneEnabled())
            {
                detail = "weather-schedule-lane-disabled";
                return false;
            }
            if (string.Equals(delta.DeltaKind, WeatherRegistryBeginDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationWeatherRegistryBegin(delta, out detail);
            if (string.Equals(delta.DeltaKind, WeatherStateDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationWeatherState(delta, out detail);
            if (string.Equals(delta.DeltaKind, WeatherForecastPageDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationWeatherForecastPage(delta, out detail);
            if (string.Equals(delta.DeltaKind, WeatherRegistryEndDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationWeatherRegistryEnd(delta, out detail);
            detail = "weather-delta-unsupported";
            return false;
        }

        private static bool TryApplyReplicationWeatherRegistryBegin(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventLong(delta.Detail, "snapshotId", out var snapshotId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "scheduleCount", out var scheduleCount)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "forecastPages", out var forecastPages)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "forecastHours", out var forecastHours)
                || snapshotId < 0L || scheduleCount < 0 || scheduleCount > 4096
                || forecastPages < 0 || forecastPages > 64 || forecastHours < 0 || forecastHours > 4096
                || forecastPages != (forecastHours + ReplicationWeatherForecastPageHours - 1) / ReplicationWeatherForecastPageHours)
            {
                detail = "weather-registry-begin-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            if (!TryResolveReplicationWeatherManager(out var manager) || manager == null
                || !TryGetReplicationWeatherForecast(manager, out var localForecast)
                || localForecast.Length != forecastHours)
            {
                detail = "weather-registry-forecast-shape-mismatch expected=" + forecastHours.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            lock (ReplicationEventLock)
            {
                if (snapshotId <= replicationWeatherClientCompletedCheckpointId)
                {
                    detail = "ok weather-registry-begin-completed id=" + snapshotId.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                if (snapshotId == replicationWeatherClientCheckpointId)
                {
                    detail = "ok weather-registry-begin-duplicate id=" + snapshotId.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                if (replicationWeatherClientCheckpointId >= 0L && snapshotId < replicationWeatherClientCheckpointId)
                {
                    detail = "ok weather-registry-begin-stale id=" + snapshotId.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                replicationWeatherClientCheckpointId = snapshotId;
                replicationWeatherClientExpectedSchedules = scheduleCount;
                replicationWeatherClientExpectedForecastPages = forecastPages;
                replicationWeatherClientExpectedForecastHours = forecastHours;
                ReplicationClientWeatherCheckpointSeen.Clear();
                ReplicationClientWeatherForecastPagesSeen.Clear();
            }
            detail = "ok weather-registry-begin id=" + snapshotId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryApplyReplicationWeatherState(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventLong(delta.Detail, "snapshotId", out var snapshotId)
                || !TryReadReplicationEventLong(delta.Detail, "start", out var start)
                || !TryReadReplicationEventLong(delta.Detail, "end", out var end)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "occurrence", out var occurrence)
                || string.IsNullOrWhiteSpace(delta.BlueprintId)
                || start < 0 || end <= start || start > uint.MaxValue || end > uint.MaxValue
                || occurrence < 0 || occurrence > 4095)
            {
                detail = "weather-state-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            var key = FormatReplicationWeatherRowKey(delta.BlueprintId, start, end, occurrence);
            lock (ReplicationEventLock)
            {
                if (snapshotId <= replicationWeatherClientCompletedCheckpointId
                    || (replicationWeatherClientCheckpointId >= 0L && snapshotId < replicationWeatherClientCheckpointId))
                {
                    detail = "ok weather-state-stale key=" + key;
                    return true;
                }
                if (snapshotId != replicationWeatherClientCheckpointId)
                {
                    detail = "weather-state-snapshot-not-started";
                    return false;
                }
                if (ReplicationClientWeatherCheckpointSeen.Contains(key))
                {
                    detail = "ok weather-state-duplicate key=" + key;
                    return true;
                }
            }
            if (!TryResolveReplicationWeatherManager(out var manager) || manager == null
                || !TryFindOrCreateReplicationWeatherInstance(manager, delta.BlueprintId, start, end, occurrence, out var weather, out detail)
                || weather == null)
            {
                return false;
            }
            lock (ReplicationEventLock)
            {
                ReplicationClientWeatherInstances[key] = weather;
                ReplicationClientWeatherCheckpointSeen.Add(key);
            }
            replicationWeatherStatesApplied++;
            detail = "ok weather-state key=" + key;
            return true;
        }

        private static bool TryApplyReplicationWeatherForecastPage(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventLong(delta.Detail, "snapshotId", out var snapshotId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "page", out var page)
                || !TryReadReplicationEventText(delta.Detail, "dataB64", out var data)
                || page < 0 || page >= 64)
            {
                detail = "weather-forecast-page-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            lock (ReplicationEventLock)
            {
                if (snapshotId <= replicationWeatherClientCompletedCheckpointId
                    || (replicationWeatherClientCheckpointId >= 0L && snapshotId < replicationWeatherClientCheckpointId))
                {
                    detail = "ok weather-forecast-stale page=" + page.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                if (snapshotId != replicationWeatherClientCheckpointId)
                {
                    detail = "weather-forecast-snapshot-not-started";
                    return false;
                }
                if (ReplicationClientWeatherForecastPagesSeen.Contains(page))
                {
                    detail = "ok weather-forecast-duplicate page=" + page.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }
            if (!TryResolveReplicationWeatherManager(out var manager) || manager == null
                || !TryGetReplicationWeatherForecast(manager, out var forecast)
                || forecast.Length != replicationWeatherClientExpectedForecastHours)
            {
                detail = string.IsNullOrEmpty(detail) ? "weather-forecast-shape-mismatch" : detail;
                return false;
            }
            var expectedStart = page * ReplicationWeatherForecastPageHours;
            var expectedCount = Math.Min(
                ReplicationWeatherForecastPageHours,
                replicationWeatherClientExpectedForecastHours - expectedStart);
            if (expectedCount <= 0
                || !TryApplyReplicationWeatherForecastData(forecast, data, expectedStart, expectedCount, out detail)) return false;
            lock (ReplicationEventLock) ReplicationClientWeatherForecastPagesSeen.Add(page);
            replicationWeatherStatesApplied++;
            detail = "ok weather-forecast-page page=" + page.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryApplyReplicationWeatherRegistryEnd(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventLong(delta.Detail, "snapshotId", out var snapshotId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "scheduleCount", out var endScheduleCount)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "forecastPages", out var endForecastPages)
                || snapshotId < 0L || endScheduleCount < 0 || endScheduleCount > 4096
                || endForecastPages < 0 || endForecastPages > 64)
            {
                detail = "weather-registry-end-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            lock (ReplicationEventLock)
            {
                if (snapshotId <= replicationWeatherClientCompletedCheckpointId)
                {
                    detail = "ok weather-registry-end-completed id=" + snapshotId.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                if (replicationWeatherClientCheckpointId >= 0L && snapshotId < replicationWeatherClientCheckpointId)
                {
                    detail = "ok weather-registry-end-stale id=" + snapshotId.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                if (snapshotId != replicationWeatherClientCheckpointId
                    || endScheduleCount != replicationWeatherClientExpectedSchedules
                    || endForecastPages != replicationWeatherClientExpectedForecastPages
                    || ReplicationClientWeatherCheckpointSeen.Count != replicationWeatherClientExpectedSchedules
                    || ReplicationClientWeatherForecastPagesSeen.Count != replicationWeatherClientExpectedForecastPages)
                {
                    detail = "weather-registry-incomplete id=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " schedules=" + ReplicationClientWeatherCheckpointSeen.Count.ToString(CultureInfo.InvariantCulture)
                        + "/" + replicationWeatherClientExpectedSchedules.ToString(CultureInfo.InvariantCulture)
                        + " pages=" + ReplicationClientWeatherForecastPagesSeen.Count.ToString(CultureInfo.InvariantCulture)
                        + "/" + replicationWeatherClientExpectedForecastPages.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }
            if (!TryResolveReplicationWeatherManager(out var manager) || manager == null
                || !TryGetReplicationWeatherScheduledEvents(manager, out var scheduled))
            {
                detail = "weather-registry-manager-missing";
                return false;
            }
            var stale = new List<object>();
            var scheduledOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < scheduled.Count; i++)
            {
                var weather = scheduled[i];
                if (weather == null || !TryReadReplicationWeatherTuple(weather, out var id, out var start, out var end)) continue;
                var tupleKey = FormatReplicationWeatherKey(id, start, end);
                scheduledOccurrences.TryGetValue(tupleKey, out var occurrence);
                scheduledOccurrences[tupleKey] = occurrence + 1;
                var key = FormatReplicationWeatherRowKey(id, start, end, occurrence);
                lock (ReplicationEventLock)
                {
                    if (!ReplicationClientWeatherCheckpointSeen.Contains(key)) stale.Add(weather);
                }
            }
            for (var i = 0; i < stale.Count; i++)
            {
                try
                {
                    if (!TryReadInstanceMemberValue(stale[i], "firstTick", out var firstTickValue)
                        || firstTickValue == null)
                    {
                        detail = "weather-stale-first-tick-missing";
                        return false;
                    }
                    var neverStarted = Convert.ToBoolean(firstTickValue, CultureInfo.InvariantCulture);
                    var cleanup = neverStarted
                        ? AccessTools.Method(stale[i].GetType(), "Destroy", Type.EmptyTypes)
                        : AccessTools.Method(stale[i].GetType(), "ForceEnd", Type.EmptyTypes);
                    if (cleanup == null)
                    {
                        detail = "weather-stale-cleanup-method-missing started=" + (!neverStarted);
                        return false;
                    }
                    replicationEventApplicationDepth++;
                    cleanup.Invoke(stale[i], null);
                }
                catch (Exception ex)
                {
                    detail = "weather-stale-cleanup-failed " + FormatReflectionExceptionDetail(ex);
                    return false;
                }
                finally { replicationEventApplicationDepth = Math.Max(0, replicationEventApplicationDepth - 1); }
            }
            for (var i = 0; i < stale.Count; i++)
            {
                if (scheduled.Contains(stale[i]))
                {
                    detail = "weather-stale-cleanup-not-removed";
                    return false;
                }
            }
            // Reassert desired active keywords on every completing checkpoint. This is
            // deliberately unconditional so an End retry can repair a prior keyword
            // failure even after stale instances were already removed.
            if (scheduled.Count > 0)
            {
                try
                {
                    for (var i = 0; i < scheduled.Count; i++)
                    {
                        var desired = scheduled[i];
                        if (desired == null
                            || !TryReadInstanceMemberValue(desired, "firstTick", out var firstTickValue)
                            || firstTickValue == null
                            || Convert.ToBoolean(firstTickValue, CultureInfo.InvariantCulture))
                        {
                            continue;
                        }
                        var restoreKeywords = AccessTools.Method(desired.GetType(), "SetKeywordsOnStart", Type.EmptyTypes);
                        if (restoreKeywords == null)
                        {
                            detail = "weather-keyword-restore-method-missing";
                            return false;
                        }
                        restoreKeywords.Invoke(desired, null);
                    }
                }
                catch (Exception ex)
                {
                    detail = "weather-keyword-restore-failed " + FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }
            lock (ReplicationEventLock)
            {
                var remove = new List<string>();
                foreach (var key in ReplicationClientWeatherInstances.Keys)
                    if (!ReplicationClientWeatherCheckpointSeen.Contains(key)) remove.Add(key);
                for (var i = 0; i < remove.Count; i++) ReplicationClientWeatherInstances.Remove(remove[i]);
                ReplicationClientWeatherCheckpointSeen.Clear();
                ReplicationClientWeatherForecastPagesSeen.Clear();
                replicationWeatherClientCompletedCheckpointId = snapshotId;
                replicationWeatherClientCheckpointId = -1L;
            }
            replicationLastWeatherSummary = "checkpoint-applied id=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " staleRemoved=" + stale.Count.ToString(CultureInfo.InvariantCulture);
            detail = "ok " + replicationLastWeatherSummary;
            return true;
        }

        private static bool TryApplyReplicationWeatherEnvironment(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!WeatherEnvironmentLaneEnabled())
            {
                detail = "weather-environment-lane-disabled";
                return false;
            }
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadFiniteReplicationWeatherFloat(delta.Detail, "temperature", out var temperature)
                || !TryReadFiniteReplicationWeatherFloat(delta.Detail, "soil", out var soil)
                || !TryReadFiniteReplicationWeatherFloat(delta.Detail, "water", out var water)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "overrideActive", out var overrideActive)
                || !TryReadFiniteReplicationWeatherFloat(delta.Detail, "overrideTemperature", out var overrideTemperature)
                || !TryReadFiniteReplicationWeatherFloat(delta.Detail, "overrideSun", out var overrideSun)
                || !TryReadFiniteReplicationWeatherFloat(delta.Detail, "overrideRain", out var overrideRain))
            {
                detail = "weather-environment-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            lock (ReplicationEventLock)
            {
                if (delta.Sequence <= replicationLastWeatherEnvironmentAppliedSequence)
                {
                    detail = "ok weather-environment-stale sequence=" + delta.Sequence.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }
            if (!TryResolveReplicationWeatherManager(out var manager) || manager == null)
            {
                detail = "weather-environment-manager-missing";
                return false;
            }
            try
            {
                if (!TryReadInstanceMemberValue(manager, "DateAndTime", out var date) || date == null
                    || !TryReadInstanceMemberValue(manager, "Overrides", out var overrides) || overrides == null)
                {
                    detail = "weather-environment-runtime-not-ready";
                    return false;
                }
                var soilField = AccessTools.Field(manager.GetType(), "soilTemperature");
                var waterField = AccessTools.Field(manager.GetType(), "waterTemperature");
                var temperatureProperty = AccessTools.Property(date.GetType(), "TemperatureCelsius");
                var setTemperature = AccessTools.Method(overrides.GetType(), "SetTemperature", new[] { typeof(float) });
                var setSun = AccessTools.Method(overrides.GetType(), "SetSunStrengthMultiplier", new[] { typeof(float) });
                var setRain = AccessTools.Method(overrides.GetType(), "SetRainEffectWeight", new[] { typeof(float) });
                var activeProperty = AccessTools.Property(overrides.GetType(), "IsActive");
                if (soilField == null || waterField == null || temperatureProperty == null || !temperatureProperty.CanWrite
                    || setTemperature == null || setSun == null || setRain == null
                    || activeProperty == null || !activeProperty.CanWrite)
                {
                    detail = "weather-environment-native-surface-missing";
                    return false;
                }
                soilField.SetValue(manager, soil);
                waterField.SetValue(manager, water);
                temperatureProperty.SetValue(date, temperature, null);
                setTemperature.Invoke(overrides, new object[] { overrideTemperature });
                setSun.Invoke(overrides, new object[] { overrideSun });
                setRain.Invoke(overrides, new object[] { overrideRain });
                activeProperty.SetValue(overrides, overrideActive, null);
                lock (ReplicationEventLock) replicationLastWeatherEnvironmentAppliedSequence = delta.Sequence;
                replicationWeatherStatesApplied++;
                detail = "ok weather-environment temperature=" + temperature.ToString("R", CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = "weather-environment-apply-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationWeatherManager(out object? manager)
        {
            manager = null;
            var type = AccessTools.TypeByName("NSMedieval.Manager.WeatherManager");
            if (type == null) return false;
            manager = ResolveReplicationUnityManagerInstance(type);
            return manager != null;
        }

        private static bool TryGetReplicationWeatherScheduledEvents(object manager, out IList scheduled)
        {
            scheduled = new ArrayList();
            if (!TryReadInstanceMemberValue(manager, "scheduledEvents", out var value) || !(value is IList list)) return false;
            scheduled = list;
            return true;
        }

        private static bool TryGetReplicationWeatherForecast(object manager, out Array forecast)
        {
            forecast = Array.Empty<object>();
            if (!TryReadInstanceMemberValue(manager, "weatherEvents", out var value) || !(value is Array array)) return false;
            forecast = array;
            return true;
        }

        private static bool TryReadReplicationWeatherTuple(object weather, out string blueprintId, out long start, out long end)
        {
            blueprintId = string.Empty;
            start = end = 0L;
            if (!TryReadInstanceMemberValue(weather, "Blueprint", out var blueprint) || blueprint == null) return false;
            var getId = AccessTools.Method(blueprint.GetType(), "GetID", Type.EmptyTypes);
            blueprintId = getId?.Invoke(blueprint, null)?.ToString() ?? string.Empty;
            if (!TryReadInstanceMemberValue(weather, "StartTime", out var startValue) || startValue == null
                || !TryReadInstanceMemberValue(weather, "EndTime", out var endValue) || endValue == null) return false;
            try
            {
                start = Convert.ToInt64(startValue, CultureInfo.InvariantCulture);
                end = Convert.ToInt64(endValue, CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(blueprintId) && start >= 0 && end > start;
            }
            catch { return false; }
        }

        private static string BuildReplicationWeatherScheduleSignature(IList scheduled, Array forecast)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < scheduled.Count; i++)
            {
                if (scheduled[i] != null && TryReadReplicationWeatherTuple(scheduled[i]!, out var id, out var start, out var end))
                    builder.Append(id).Append('@').Append(start).Append('-').Append(end).Append(';');
            }
            builder.Append("forecast=").Append(forecast.Length);
            for (var i = 0; i < forecast.Length; i++)
            {
                var entry = forecast.GetValue(i);
                if (entry == null) continue;
                builder.Append('|').Append(i).Append(':').Append(ReadReplicationWeatherForecastTemperature(entry).ToString("R", CultureInfo.InvariantCulture));
                var ids = ReadReplicationWeatherForecastEventIds(entry);
                for (var j = 0; j < ids.Count; j++) builder.Append(',').Append(ids[j]);
            }
            return builder.ToString();
        }

        private static string EncodeReplicationWeatherForecastPage(Array forecast, int startIndex, int count)
        {
            var builder = new System.Text.StringBuilder();
            var end = Math.Min(forecast.Length, startIndex + count);
            for (var i = startIndex; i < end; i++)
            {
                if (builder.Length > 0) builder.Append(';');
                var entry = forecast.GetValue(i);
                builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
                builder.Append((entry == null ? 0f : ReadReplicationWeatherForecastTemperature(entry)).ToString("R", CultureInfo.InvariantCulture));
                if (entry == null) continue;
                var ids = ReadReplicationWeatherForecastEventIds(entry);
                for (var j = 0; j < ids.Count; j++) builder.Append(',').Append(ids[j].Replace(",", string.Empty).Replace(";", string.Empty));
            }
            return builder.ToString();
        }

        private static float ReadReplicationWeatherForecastTemperature(object entry)
        {
            if (!TryReadInstanceMemberValue(entry, "Temperature", out var value) || value == null) return 0f;
            try { return Convert.ToSingle(value, CultureInfo.InvariantCulture); }
            catch { return 0f; }
        }

        private static List<string> ReadReplicationWeatherForecastEventIds(object entry)
        {
            var result = new List<string>();
            if (!TryReadInstanceMemberValue(entry, "Events", out var events) || !(events is IEnumerable enumerable)) return result;
            foreach (var blueprint in enumerable)
            {
                if (blueprint == null) continue;
                var id = AccessTools.Method(blueprint.GetType(), "GetID", Type.EmptyTypes)?.Invoke(blueprint, null)?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id!);
            }
            return result;
        }

        private static bool TryApplyReplicationWeatherForecastData(
            Array forecast,
            string data,
            int expectedStart,
            int expectedCount,
            out string detail)
        {
            detail = string.Empty;
            var rows = data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (rows.Length != expectedCount)
            {
                detail = "weather-forecast-page-row-count expected=" + expectedCount.ToString(CultureInfo.InvariantCulture)
                    + " actual=" + rows.Length.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var parts = rows[rowIndex].Split(new[] { ',' }, StringSplitOptions.None);
                if (parts.Length < 2
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    || index != expectedStart + rowIndex
                    || index < 0 || index >= forecast.Length
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
                    || float.IsNaN(temperature) || float.IsInfinity(temperature))
                {
                    detail = "weather-forecast-row-malformed row=" + rowIndex.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                var entry = forecast.GetValue(index);
                if (entry == null)
                {
                    detail = "weather-forecast-entry-missing index=" + index.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                var clearEvents = AccessTools.Method(entry.GetType(), "ClearEvents", Type.EmptyTypes);
                var setTemperature = AccessTools.Method(entry.GetType(), "SetTemperature", new[] { typeof(float) });
                if (clearEvents == null || setTemperature == null)
                {
                    detail = "weather-forecast-entry-surface-missing index=" + index.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                clearEvents.Invoke(entry, null);
                setTemperature.Invoke(entry, new object[] { temperature });
                if (!TryReadInstanceMemberValue(entry, "Events", out var events) || !(events is IList eventList))
                {
                    detail = "weather-forecast-events-list-missing index=" + index.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                for (var i = 2; i < parts.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(parts[i])) continue;
                    var blueprint = ResolveReplicationWeatherBlueprint(parts[i]);
                    if (blueprint == null)
                    {
                        detail = "weather-blueprint-missing id=" + parts[i];
                        return false;
                    }
                    eventList.Add(blueprint);
                }
            }
            return true;
        }

        private static object? ResolveReplicationWeatherBlueprint(string id)
        {
            try
            {
                var repositoryType = AccessTools.TypeByName("NSMedieval.Weather.WeatherEventRepository");
                var weatherType = AccessTools.TypeByName("NSMedieval.Weather.WeatherEvent");
                var openRepository = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
                if (repositoryType == null || weatherType == null || openRepository == null) return null;
                var closed = openRepository.MakeGenericType(repositoryType, weatherType);
                var repository = AccessTools.Property(closed, "Instance")?.GetValue(null, null);
                return repository == null ? null : AccessTools.Method(closed, "GetByID", new[] { typeof(string) })?.Invoke(repository, new object[] { id });
            }
            catch { return null; }
        }

        private static bool TryFindOrCreateReplicationWeatherInstance(
            object manager,
            string blueprintId,
            long start,
            long end,
            int occurrence,
            out object? weather,
            out string detail)
        {
            weather = null;
            detail = string.Empty;
            if (!TryGetReplicationWeatherScheduledEvents(manager, out var scheduled))
            {
                detail = "weather-scheduled-list-missing";
                return false;
            }
            var matching = 0;
            for (var i = 0; i < scheduled.Count; i++)
            {
                var candidate = scheduled[i];
                if (candidate != null
                    && TryReadReplicationWeatherTuple(candidate, out var id, out var candidateStart, out var candidateEnd)
                    && string.Equals(id, blueprintId, StringComparison.Ordinal)
                    && candidateStart == start
                    && candidateEnd == end)
                {
                    if (matching == occurrence)
                    {
                        weather = candidate;
                        detail = "weather-existing occurrence=" + occurrence.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    matching++;
                }
            }
            try
            {
                var blueprint = ResolveReplicationWeatherBlueprint(blueprintId);
                var instanceType = AccessTools.TypeByName("NSMedieval.Weather.WeatherEventInstance");
                if (blueprint == null || instanceType == null)
                {
                    detail = "weather-constructor-input-missing blueprint=" + blueprintId;
                    return false;
                }
                while (matching <= occurrence)
                {
                    var created = Activator.CreateInstance(instanceType, new object[] { blueprint, (uint)start, (uint)end });
                    if (created == null)
                    {
                        detail = "weather-constructor-returned-null blueprint=" + blueprintId;
                        return false;
                    }
                    scheduled.Add(created);
                    if (matching == occurrence) weather = created;
                    matching++;
                }
            }
            catch (Exception ex)
            {
                detail = "weather-construction-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            detail = weather == null
                ? "weather-created-instance-not-found"
                : "weather-created occurrence=" + occurrence.ToString(CultureInfo.InvariantCulture);
            return weather != null;
        }

        private static string FormatReplicationWeatherKey(string blueprintId, long start, long end)
        {
            return blueprintId + "@" + start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatReplicationWeatherRowKey(string blueprintId, long start, long end, int occurrence)
        {
            return FormatReplicationWeatherKey(blueprintId, start, end)
                + "#" + occurrence.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryReadReplicationWeatherEnvironment(object manager, out ReplicationWeatherEnvironment state)
        {
            state = new ReplicationWeatherEnvironment();
            try
            {
                if (!TryReadInstanceMemberValue(manager, "SoilTemperature", out var soil) || soil == null
                    || !TryReadInstanceMemberValue(manager, "WaterTemperature", out var water) || water == null
                    || !TryReadInstanceMemberValue(manager, "DateAndTime", out var date) || date == null
                    || !TryReadInstanceMemberValue(date, "TemperatureCelsius", out var temperature) || temperature == null
                    || !TryReadInstanceMemberValue(manager, "Overrides", out var overrides) || overrides == null)
                    return false;
                state.SoilTemperature = Convert.ToSingle(soil, CultureInfo.InvariantCulture);
                state.WaterTemperature = Convert.ToSingle(water, CultureInfo.InvariantCulture);
                state.Temperature = Convert.ToSingle(temperature, CultureInfo.InvariantCulture);
                state.OverrideActive = ReadReplicationBoolMember(overrides, "IsActive");
                state.OverrideTemperature = InvokeReplicationWeatherFloat(overrides, "GetTemperature", float.NaN, state.Temperature);
                state.OverrideSunStrength = InvokeReplicationWeatherFloat(overrides, "GetSunStrengthMultiplier", null, 1f);
                state.OverrideRainWeight = InvokeReplicationWeatherFloat(overrides, "GetRainEffectWeight", float.NaN, 0f);
                return IsFiniteReplicationWeatherFloat(state.Temperature)
                    && IsFiniteReplicationWeatherFloat(state.SoilTemperature)
                    && IsFiniteReplicationWeatherFloat(state.WaterTemperature)
                    && IsFiniteReplicationWeatherFloat(state.OverrideTemperature)
                    && IsFiniteReplicationWeatherFloat(state.OverrideSunStrength)
                    && IsFiniteReplicationWeatherFloat(state.OverrideRainWeight);
            }
            catch { return false; }
        }

        private static float InvokeReplicationWeatherFloat(object owner, string name, float? argument, float fallback)
        {
            try
            {
                var method = argument.HasValue
                    ? AccessTools.Method(owner.GetType(), name, new[] { typeof(float) })
                    : AccessTools.Method(owner.GetType(), name, Type.EmptyTypes);
                var value = method?.Invoke(owner, argument.HasValue ? new object[] { argument.Value } : null);
                var parsed = value == null ? fallback : Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return IsFiniteReplicationWeatherFloat(parsed) ? parsed : fallback;
            }
            catch { return fallback; }
        }

        private static bool TryReadFiniteReplicationWeatherFloat(string detail, string name, out float value)
        {
            value = 0f;
            return TryReadReplicationWorldObjectDetailToken(detail, name, out var token)
                && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && IsFiniteReplicationWeatherFloat(value);
        }

        private static bool IsFiniteReplicationWeatherFloat(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void ResetReplicationWeatherRuntimeState()
        {
            lock (ReplicationEventLock)
            {
                ReplicationClientWeatherInstances.Clear();
                ReplicationClientWeatherCheckpointSeen.Clear();
                ReplicationClientWeatherForecastPagesSeen.Clear();
            }
            replicationWeatherCheckpointSequence = 0L;
            replicationWeatherClientCheckpointId = -1L;
            replicationWeatherClientCompletedCheckpointId = -1L;
            replicationLastWeatherEnvironmentAppliedSequence = -1L;
            replicationWeatherClientExpectedSchedules = 0;
            replicationWeatherClientExpectedForecastPages = 0;
            replicationWeatherClientExpectedForecastHours = 0;
            replicationNextWeatherCheckpointRealtime = 0f;
            replicationNextWeatherEnvironmentRealtime = 0f;
            replicationLastWeatherScheduleSignature = string.Empty;
            replicationLastWeatherEnvironmentSignature = string.Empty;
            replicationLastWeatherFullCheckpointRealtime = -120f;
            replicationLastWeatherEnvironmentSendRealtime = -30f;
            replicationWeatherStatesSent = 0L;
            replicationWeatherStatesApplied = 0L;
            replicationWeatherMutationsSuppressed = 0L;
            replicationClientWeatherAuthorityParked = false;
            replicationLastWeatherSummary = string.Empty;
        }

        private static string FormatReplicationWeatherStatus()
        {
            return "weatherSent=" + replicationWeatherStatesSent.ToString(CultureInfo.InvariantCulture)
                + " weatherApplied=" + replicationWeatherStatesApplied.ToString(CultureInfo.InvariantCulture)
                + " weatherSuppressed=" + replicationWeatherMutationsSuppressed.ToString(CultureInfo.InvariantCulture)
                + " lastWeather=" + (string.IsNullOrEmpty(replicationLastWeatherSummary) ? "<none>" : replicationLastWeatherSummary);
        }

        private sealed class ReplicationWeatherEnvironment
        {
            public float Temperature;
            public float SoilTemperature;
            public float WaterTemperature;
            public bool OverrideActive;
            public float OverrideTemperature;
            public float OverrideSunStrength;
            public float OverrideRainWeight;

            public override string ToString()
            {
                return Temperature.ToString("R", CultureInfo.InvariantCulture) + "|"
                    + SoilTemperature.ToString("R", CultureInfo.InvariantCulture) + "|"
                    + WaterTemperature.ToString("R", CultureInfo.InvariantCulture) + "|"
                    + OverrideActive + "|"
                    + OverrideTemperature.ToString("R", CultureInfo.InvariantCulture) + "|"
                    + OverrideSunStrength.ToString("R", CultureInfo.InvariantCulture) + "|"
                    + OverrideRainWeight.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        private sealed class ReplicationWeatherScheduleRow
        {
            public ReplicationWeatherScheduleRow(string blueprintId, long start, long end, int occurrence)
            {
                BlueprintId = blueprintId;
                Start = start;
                End = end;
                Occurrence = occurrence;
            }

            public string BlueprintId { get; }
            public long Start { get; }
            public long End { get; }
            public int Occurrence { get; }
        }
    }
}

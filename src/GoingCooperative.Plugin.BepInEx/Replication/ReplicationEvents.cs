using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string GameEventRegistryBeginDeltaKind = "GameEventRegistryBegin";
        private const string GameEventStateDeltaKind = "GameEventState";
        private const string GameEventRegistryEndDeltaKind = "GameEventRegistryEnd";
        private const string GameEventTombstoneDeltaKind = "GameEventTombstone";
        private const string GameEventSpeedStateDeltaKind = "GameEventSpeedState";
        private const string GameEventNoticeDeltaKind = "GameEventNotice";
        private const float ReplicationEventPollSeconds = 0.25f;
        private const float ReplicationEventCheckpointSeconds = 5f;
        private const float ReplicationEventChoiceRetrySeconds = 0.75f;
        private const int ReplicationEventChoiceMaxSends = 8;
        private const int ReplicationEventMaxPendingCheckpoints = 16;
        private const int ReplicationEventMaxCheckpointStates = 4096;
        private const char ReplicationEventOptionSeparator = '\u001f';
        private const char ReplicationEventWarningFieldSeparator = '\u001e';
        private const char ReplicationEventWarningEntrySeparator = '\u001d';

        private static readonly object ReplicationEventLock = new object();
        private static readonly Dictionary<object, HostReplicationEventRecord> ReplicationHostEvents =
            new Dictionary<object, HostReplicationEventRecord>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, HostReplicationEventRecord> ReplicationHostEventsById =
            new Dictionary<string, HostReplicationEventRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ClientReplicationEventState> ReplicationClientEvents =
            new Dictionary<string, ClientReplicationEventState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationClientEventRevisionHighWater =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly EventCheckpointTracker ReplicationClientEventCheckpoints =
            new EventCheckpointTracker(ReplicationEventMaxPendingCheckpoints, ReplicationEventMaxCheckpointStates);
        private static readonly HashSet<string> ReplicationRetiredEventScopes =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> ReplicationRetiredEventScopeOrder = new Queue<string>();
        private static readonly Stack<object> ReplicationEventNoticeCaptureContext = new Stack<object>();
        private static readonly HashSet<string> ReplicationClientEventNoticeSeen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> ReplicationClientEventNoticeSeenOrder = new Queue<string>();

        private static string replicationEventHostSessionNonce = string.Empty;
        private static string replicationEventClientSessionNonce = string.Empty;
        private static int replicationEventHostEpoch = -1;
        private static int replicationEventClientEpoch = -1;
        private static long replicationEventHostCounter;
        private static long replicationEventHostNoticeCounter;
        private static long replicationEventCheckpointSequence;
        private static long replicationLastEventSpeedAppliedSequence = -1L;
        private static int replicationHostEventSpeedIndex = -1;
        private static int replicationLastHostEventSpeedSentIndex = -1;
        private static float replicationNextEventPollRealtime;
        private static float replicationNextEventCheckpointRealtime;
        private static float replicationNextClientEventQuarantineRealtime;
        private static int replicationEventApplicationDepth;
        private static bool replicationEventHooksReady;
        private static bool replicationTraderEventStartHooksReady;
        private static bool replicationEventSpeedHookReady;
        private static bool replicationEventNoticeHooksReady;
        private static bool replicationEventWarningSurfacesReady;
        private static bool replicationClientEventAuthorityParked;
        private static PendingReplicationEventChoice? replicationPendingEventChoice;
        private static long replicationEventsSent;
        private static long replicationEventsApplied;
        private static long replicationEventsSuppressed;
        private static long replicationEventChoicesSent;
        private static long replicationEventChoicesApplied;
        private static long replicationEventChoicesRejected;
        private static long replicationEventNoticesSent;
        private static long replicationEventNoticesApplied;
        private static string replicationLastEventSummary = string.Empty;

        private GameObject? multiplayerEventPresentationPanel;
        private string multiplayerEventPresentationKey = string.Empty;
        private GameObject? multiplayerEventWarningPanel;
        private string multiplayerEventWarningKey = string.Empty;

        private void TryInstallReplicationEventHooks(Harmony harmonyInstance)
        {
            var suppressVoid = EventHarmony(nameof(ReplicationEventSuppressVoidPrefix));
            var suppressStart = EventHarmony(nameof(ReplicationEventStartEventPrefix));
            var suppressOnLoaded = EventHarmony(nameof(ReplicationEventOnGameLoadedPrefix));
            var startPrefix = EventHarmony(nameof(ReplicationEventInstanceStartPrefix));
            var startPostfix = EventHarmony(nameof(ReplicationEventInstanceStartPostfix));
            var phasePostfix = EventHarmony(nameof(ReplicationEventSwitchPhasePostfix));
            var endPrefix = EventHarmony(nameof(ReplicationEventInstanceEndPrefix));
            var endPostfix = EventHarmony(nameof(ReplicationEventInstanceEndPostfix));
            var dialogPrefix = EventHarmony(nameof(ReplicationEventDialogClosePrefix));
            var dialogPostfix = EventHarmony(nameof(ReplicationEventDialogClosePostfix));
            var blockingPrefix = EventHarmony(nameof(ReplicationEventBlockingQueryPrefix));
            var objectivePrefix = EventHarmony(nameof(ReplicationEventObjectiveBlockingQueryPrefix));
            var weatherTextPrefix = EventHarmony(nameof(ReplicationEventWeatherTextQueryPrefix));
            var runningPrefix = EventHarmony(nameof(ReplicationEventRunningQueryPrefix));
            var count = 0;

            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.EventScheduler", "OnDateUpdate", Type.EmptyTypes, suppressVoid, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.EventScheduler", "OnTimeUpdate", Type.EmptyTypes, suppressVoid, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.EventScheduler", "ScheduleEventGroup", new[]
            {
                EventType("NSMedieval.GameEventSystem.EventGroupInstance"),
                typeof(long)
            }, suppressVoid, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.EventScheduler", "OnRaidEndedEvent", new[]
            {
                EventType("NSMedieval.Manager.ActiveRaidInfo")
            }, suppressVoid, null);
            var traderStartEventHookCount = PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventSystem", "StartEvent", new[] { typeof(string) }, suppressStart, null);
            count += traderStartEventHookCount;
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventSystem", "OnGameLoaded", new[] { typeof(bool) }, suppressOnLoaded, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventSystem", "IsBlockingEventRunning", Type.EmptyTypes, blockingPrefix, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventSystem", "IsBlockingObjectiveButton", Type.EmptyTypes, objectivePrefix, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventSystem", "RunningEventsWeatherTextKey", Type.EmptyTypes, weatherTextPrefix, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventSystem", "IsEventRunning", new[] { typeof(string) }, runningPrefix, null);
            var traderInstanceStartHookCount = PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventInstance", "Start", Type.EmptyTypes, startPrefix, startPostfix);
            count += traderInstanceStartHookCount;
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventInstance", "Tick", new[] { typeof(float) }, suppressVoid, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventInstance", "ForceEnd", Type.EmptyTypes, suppressVoid, null);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventInstance", "OnEnd", Type.EmptyTypes, endPrefix, endPostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventStateMachine", "SwitchPhase", new[]
            {
                EventType("NSMedieval.GameEventSystem.GameEventPhaseBase")
            }, suppressVoid, phasePostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.Events.ShowDialogPhase", "OnClose", new[] { typeof(int) }, dialogPrefix, dialogPostfix);
            count += PatchReplicationEventMethod(harmonyInstance, "NSMedieval.GameEventSystem.ShowDialogPhaseBranching", "OnClose", new[] { typeof(int) }, dialogPrefix, dialogPostfix);
            var eventHookCount = count;
            replicationEventHooksReady = eventHookCount == 17;
            replicationTraderEventStartHooksReady = traderStartEventHookCount == 1
                && traderInstanceStartHookCount == 1;
            replicationEventWarningSurfacesReady = ValidateReplicationEventWarningSurfaces();
            var noticeHookCount = TryInstallReplicationEventNoticeHooks(harmonyInstance);
            count += noticeHookCount;
            var weatherHookCount = TryInstallReplicationWeatherHooks(harmonyInstance);
            count += weatherHookCount;

            LogReplicationInfo("Going Cooperative event replication hooks installed count="
                + count.ToString(CultureInfo.InvariantCulture)
                + " master=" + replicationConfigEventReplication
                + " schedulerAuthorityConfigured=" + replicationConfigEventSchedulerAuthority
                + " traderAuthorityConfigured=" + replicationConfigEventTraderAuthority
                + " traderStartHooks=" + replicationTraderEventStartHooksReady
                + " traderAuthority=" + TraderEventAuthorityEnabled()
                + " fullGraphAuthority=" + FullEventGraphAuthorityEnabled()
                + " lifecycle=" + replicationConfigEventLifecycleReplication
                + " dialogs=" + replicationConfigEventDialogReplication
                + " choices=" + replicationConfigEventChoiceCommands);
            if (!replicationEventHooksReady)
                LogReplicationWarning("Going Cooperative event authority failed closed because critical hooks are incomplete expected=17 actual=" + eventHookCount.ToString(CultureInfo.InvariantCulture));
            if (replicationConfigEventTraderAuthority && !replicationTraderEventStartHooksReady)
                LogReplicationWarning("Going Cooperative trader event authority failed closed because its exact StartEvent/instance Start hooks are incomplete.");
            if (!replicationEventWarningSurfacesReady)
                LogReplicationWarning("Going Cooperative event warning replication failed closed because native warning surfaces are incomplete.");
        }

        private static bool ValidateReplicationEventWarningSurfaces()
        {
            try
            {
                var eventInstanceType = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEventInstance");
                var warningType = AccessTools.TypeByName("NSMedieval.Model.WarningMessageData");
                var delayType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.DelayCountdownPhase");
                var waitType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.WaitUntilTimeframePhase");
                var helperType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.CountdownWithWarningMessage");
                var negotiationType = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.NegotiationPhase");
                if (eventInstanceType == null || warningType == null || delayType == null
                    || waitType == null || helperType == null || negotiationType == null)
                {
                    return false;
                }
                if (AccessTools.Field(eventInstanceType, "warningMessage") == null
                    || AccessTools.Property(eventInstanceType, "WarningTooltipPrefixLines") == null
                    || AccessTools.Property(delayType, "CountdownMessage") == null
                    || AccessTools.Field(delayType, "additionalTooltipLines") == null
                    || AccessTools.Property(waitType, "CountdownMessage") == null
                    || AccessTools.Field(waitType, "additionalTooltipLines") == null
                    || AccessTools.Property(helperType, "CountdownMessage") == null
                    || AccessTools.Field(helperType, "additionalTooltipLines") == null
                    || AccessTools.Field(negotiationType, "countdown") == null)
                {
                    return false;
                }
                foreach (var property in new[] { "ID", "Text", "Tooltip", "Icon", "Timer", "ShouldShow", "FactionInstance" })
                    if (AccessTools.Property(warningType, property) == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int TryInstallReplicationEventNoticeHooks(Harmony harmonyInstance)
        {
            var contextPrefix = EventHarmony(nameof(ReplicationEventNoticeContextPrefix));
            var contextFinalizer = EventHarmony(nameof(ReplicationEventNoticeContextFinalizer));
            var capturePrefix = EventHarmony(nameof(ReplicationEventNoticeCapturePrefix));
            var count = 0;
            try
            {
                var eventType = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEventInstance");
                if (eventType != null)
                {
                    foreach (var method in new[]
                    {
                        AccessTools.Method(eventType, "Start", Type.EmptyTypes),
                        AccessTools.Method(eventType, "Tick", new[] { typeof(float) }),
                        AccessTools.Method(eventType, "OnEnd", Type.EmptyTypes)
                    })
                    {
                        if (method == null) continue;
                        harmonyInstance.Patch(method, prefix: contextPrefix, finalizer: contextFinalizer);
                        count++;
                    }
                }

                var controllerType = AccessTools.TypeByName("NSMedieval.BlackBarMessageController");
                var vectorType = typeof(Vector3);
                var selectableType = AccessTools.TypeByName("NSMedieval.View.SelectableObject");
                var methods = new[]
                {
                    controllerType == null ? null : AccessTools.Method(controllerType, "ShowBlackBarMessage", new[] { typeof(string) }),
                    controllerType == null ? null : AccessTools.Method(controllerType, "ShowClickableBlackBarMessage", new[] { typeof(string), vectorType }),
                    controllerType == null || selectableType == null ? null : AccessTools.Method(controllerType, "ShowClickableBlackBarMessage", new[] { typeof(string), selectableType, typeof(bool) })
                };
                for (var i = 0; i < methods.Length; i++)
                {
                    if (methods[i] == null) continue;
                    harmonyInstance.Patch(methods[i], prefix: capturePrefix);
                    count++;
                }
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative event notice hook installation failed error=" + FormatReflectionExceptionDetail(ex));
            }
            replicationEventNoticeHooksReady = count == 6;
            if (!replicationEventNoticeHooksReady)
                LogReplicationWarning("Going Cooperative event notice replication failed closed because hooks are incomplete expected=6 actual=" + count.ToString(CultureInfo.InvariantCulture));
            return count;
        }

        private static HarmonyMethod EventHarmony(string methodName)
        {
            return new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static Type EventType(string typeName)
        {
            return AccessTools.TypeByName(typeName) ?? typeof(object);
        }

        private int PatchReplicationEventMethod(
            Harmony harmonyInstance,
            string typeName,
            string methodName,
            Type[] parameterTypes,
            HarmonyMethod? prefix,
            HarmonyMethod? postfix)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    LogReplicationWarning("Going Cooperative event hook type missing " + typeName);
                    return 0;
                }

                var method = AccessTools.Method(type, methodName, parameterTypes);
                if (method == null)
                {
                    LogReplicationWarning("Going Cooperative event hook method missing " + typeName + "." + methodName);
                    return 0;
                }

                harmonyInstance.Patch(method, prefix, postfix);
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative event hook failed " + typeName + "." + methodName + " error=" + FormatReflectionExceptionDetail(ex));
                return 0;
            }
        }

        private static bool EventLifecycleLaneEnabled()
        {
            return replicationConfigEventReplication
                && replicationConfigEventLifecycleReplication
                && replicationEventHooksReady
                && string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase);
        }

        private static bool EventRegistryReplicationLaneEnabled()
        {
            return EventLifecycleLaneEnabled() || TraderEventAuthorityEnabled();
        }

        // This lane is intentionally exact: only trader events whose complete party
        // can be reconstructed are eligible. All other event families remain native.
        private static bool TraderEventAuthorityEnabled()
        {
            return replicationConfigEventReplication
                && replicationConfigEventLifecycleReplication
                && replicationConfigEventTraderAuthority
                && replicationTraderEventStartHooksReady
                && string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase)
                && ReplicationTraderPartySurfacesReady();
        }

        // Full graph replacement remains fail-closed until every presentation and
        // mutation output required to replace native event execution is implemented.
        private static bool FullEventGraphAuthorityEnabled()
        {
            return EventLifecycleLaneEnabled()
                && replicationConfigEventSchedulerAuthority
                && replicationConfigEventDialogReplication
                && replicationConfigEventChoiceCommands
                && replicationConfigEventSpeedReplication
                && replicationConfigEventWarningReplication
                && replicationConfigEventNoticeReplication
                && replicationEventSpeedHookReady
                && replicationEventNoticeHooksReady
                && replicationEventWarningSurfacesReady
                && replicationConfigEventExternalAgentLifecycle
                && replicationConfigCombatExternalAgentLifecycle
                && replicationConfigEventEnvironmentMutationReplication
                && ReplicationEventExternalAgentLifecycleImplemented()
                && ReplicationCombatExternalAgentLifecycleImplemented()
                && ReplicationEventEnvironmentMutationImplemented()
                && ReplicationEventWarningReplicationImplemented()
                && ReplicationEventNoticeReplicationImplemented()
                && replicationConfigMultiplayerMenuEnabled
                && IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode);
        }

        private static bool ReplicationEventExternalAgentLifecycleImplemented()
        {
            return false;
        }

        private static bool ReplicationCombatExternalAgentLifecycleImplemented()
        {
            return false;
        }

        private static bool ReplicationEventEnvironmentMutationImplemented()
        {
            return false;
        }

        private static bool ReplicationEventWarningReplicationImplemented()
        {
            return true;
        }

        private static bool ReplicationEventNoticeReplicationImplemented()
        {
            return true;
        }

        private static bool EventNoticeLaneEnabled()
        {
            return FullEventGraphAuthorityEnabled()
                && replicationConfigEventNoticeReplication
                && replicationEventNoticeHooksReady;
        }

        private static bool EventWarningLaneEnabled()
        {
            return FullEventGraphAuthorityEnabled()
                && replicationConfigEventWarningReplication
                && replicationEventWarningSurfacesReady;
        }

        private static bool EventDialogLaneEnabled()
        {
            return FullEventGraphAuthorityEnabled() && replicationConfigEventDialogReplication;
        }

        private static bool EventChoiceLaneEnabled()
        {
            return EventDialogLaneEnabled()
                && replicationConfigEventChoiceCommands
                && IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode);
        }

        private static bool ShouldSuppressReplicationClientEvents()
        {
            if (replicationConfigHostMode
                || !FullEventGraphAuthorityEnabled()
                || replicationEventApplicationDepth > 0)
            {
                return false;
            }

            if (multiplayerLoadingInProgress)
            {
                return true;
            }

            return replicationClientEventAuthorityParked
                || (replicationConfigEnabled && replicationRuntimeStarted);
        }

        private static bool ShouldSuppressReplicationClientTraderEventStart(bool traderEvent)
        {
            return TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
                traderEvent,
                replicationConfigHostMode,
                TraderEventAuthorityEnabled(),
                replicationConfigEnabled,
                replicationRuntimeStarted,
                replicationRemoteHelloReceived,
                multiplayerLoadingInProgress,
                replicationEventApplicationDepth > 0);
        }

        private static bool IsReplicationTraderEventInstance(object? nativeEvent)
        {
            var typeName = nativeEvent?.GetType().FullName;
            return string.Equals(typeName, "NSMedieval.GameEventSystem.Events.TraderEvent", StringComparison.Ordinal)
                || string.Equals(typeName, "NSMedieval.GameEventSystem.Events.MultiTraderEvent", StringComparison.Ordinal);
        }

        private static bool IsReplicationTraderEventBlueprintId(string blueprintId)
        {
            if (string.IsNullOrWhiteSpace(blueprintId)) return false;
            try
            {
                var repositoryType = AccessTools.TypeByName("NSMedieval.Repository.GameEventSettingsRepository");
                var eventType = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEvent");
                var openRepository = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
                if (repositoryType == null || eventType == null || openRepository == null) return false;
                var closedRepository = openRepository.MakeGenericType(repositoryType, eventType);
                var repository = AccessTools.Property(closedRepository, "Instance")?.GetValue(null, null);
                var blueprint = repository == null
                    ? null
                    : AccessTools.Method(closedRepository, "GetByID", new[] { typeof(string) })
                        ?.Invoke(repository, new object[] { blueprintId });
                if (blueprint == null) return false;
                var className = AccessTools.Property(blueprint.GetType(), "ClassName")?.GetValue(blueprint, null) as string;
                return string.Equals(className, "TraderEvent", StringComparison.Ordinal)
                    || string.Equals(className, "MultiTraderEvent", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative trader event blueprint classification failed id="
                    + blueprintId
                    + " error="
                    + FormatReflectionExceptionDetail(ex));
                return false;
            }
        }

        private static bool ReplicationEventSuppressVoidPrefix()
        {
            if (!ShouldSuppressReplicationClientEvents()) return true;
            replicationEventsSuppressed++;
            return false;
        }

        private static void ReplicationEventNoticeContextPrefix(object __instance)
        {
            if (!replicationConfigHostMode || !EventNoticeLaneEnabled() || __instance == null) return;
            ReplicationEventNoticeCaptureContext.Push(__instance);
        }

        private static Exception? ReplicationEventNoticeContextFinalizer(object __instance, Exception? __exception)
        {
            if (ReplicationEventNoticeCaptureContext.Count > 0
                && ReferenceEquals(ReplicationEventNoticeCaptureContext.Peek(), __instance))
            {
                ReplicationEventNoticeCaptureContext.Pop();
            }
            return __exception;
        }

        private static void ReplicationEventNoticeCapturePrefix(string __0)
        {
            if (!replicationConfigHostMode
                || !EventNoticeLaneEnabled()
                || ReplicationEventNoticeCaptureContext.Count == 0
                || string.IsNullOrWhiteSpace(__0))
            {
                return;
            }
            instance?.CaptureAndSendReplicationEventNotice(
                ReplicationEventNoticeCaptureContext.Peek(),
                __0);
        }

        private static bool ReplicationEventStartEventPrefix(string __0, ref bool __result)
        {
            if (!ShouldSuppressReplicationClientTraderEventStart(IsReplicationTraderEventBlueprintId(__0))) return true;
            __result = false;
            replicationEventsSuppressed++;
            replicationLastEventSummary = "client-native-trader-start-suppressed blueprintId=" + __0;
            return false;
        }

        private static bool ReplicationEventOnGameLoadedPrefix(object __instance)
        {
            if (!ShouldSuppressReplicationClientEvents()) return true;
            replicationEventsSuppressed++;
            QuarantineReplicationClientNativeEvents(__instance, "load");
            replicationLastEventSummary = "client-native-active-events-parked-on-load";
            instance?.LogReplicationInfo("Going Cooperative event authority parked client native active event state machines during synchronized load.");
            return false;
        }

        private static bool ReplicationEventInstanceStartPrefix(object __instance, ref bool __result)
        {
            var traderEvent = IsReplicationTraderEventInstance(__instance);
            if (ShouldSuppressReplicationClientTraderEventStart(traderEvent))
            {
                __result = false;
                replicationEventsSuppressed++;
                replicationLastEventSummary = "client-native-trader-instance-start-suppressed type=" + __instance.GetType().Name;
                return false;
            }
            if (replicationConfigHostMode
                && (EventLifecycleLaneEnabled() || (traderEvent && TraderEventAuthorityEnabled())))
                EnsureHostReplicationEventRecord(__instance, "start-prefix");
            return true;
        }

        private static void ReplicationEventInstanceStartPostfix(object __instance, bool __result)
        {
            if (!replicationConfigHostMode
                || (!EventLifecycleLaneEnabled()
                    && !(IsReplicationTraderEventInstance(__instance) && TraderEventAuthorityEnabled()))) return;
            if (!__result)
            {
                DiscardHostReplicationTraderPartyCapture(__instance, "native-start-failed");
                RemoveFailedHostReplicationEventRecord(__instance);
                return;
            }

            if (IsReplicationTraderEventInstance(__instance) && TraderEventAuthorityEnabled())
            {
                HostReplicationEventRecord? record;
                lock (ReplicationEventLock) ReplicationHostEvents.TryGetValue(__instance, out record);
                if (record != null)
                {
                    if (!PrepareHostReplicationTraderParty(__instance, record.EventId, "native-start", out var detail))
                    {
                        AbortHostReplicationTraderParty(__instance, record.EventId, detail);
                        RemoveFailedHostReplicationEventRecord(__instance);
                        return;
                    }
                }
                else
                {
                    instance?.LogReplicationWarning("Going Cooperative trader party publish failed closed: host event identity is missing after native Start.");
                    return;
                }
            }
            instance?.CaptureAndSendHostReplicationEvent(__instance, "start", true);
        }

        private static void ReplicationEventSwitchPhasePostfix(object __instance)
        {
            if (!replicationConfigHostMode || !EventLifecycleLaneEnabled()) return;
            if (TryReadInstanceMemberValue(__instance, "parentEventInstance", out var eventInstance) && eventInstance != null)
            {
                instance?.CaptureAndSendHostReplicationEvent(eventInstance, "phase-switch", true);
            }
        }

        private static bool ReplicationEventInstanceEndPrefix(object __instance, ref string? __state)
        {
            __state = null;
            if (ShouldSuppressReplicationClientEvents())
            {
                replicationEventsSuppressed++;
                replicationLastEventSummary = "client-native-instance-end-suppressed";
                return false;
            }
            if (!replicationConfigHostMode
                || (!EventLifecycleLaneEnabled()
                    && !(IsReplicationTraderEventInstance(__instance) && TraderEventAuthorityEnabled()))) return true;
            if (IsReplicationTraderEventInstance(__instance) && IsHostReplicationTraderPartyAborted(__instance)) return true;
            var record = EnsureHostReplicationEventRecord(__instance, "end-prefix");
            __state = record?.EventId;
            return true;
        }

        private static void ReplicationEventInstanceEndPostfix(object __instance, string? __state)
        {
            if (!replicationConfigHostMode
                || string.IsNullOrWhiteSpace(__state)
                || (!EventLifecycleLaneEnabled()
                    && !(IsReplicationTraderEventInstance(__instance) && TraderEventAuthorityEnabled()))) return;
            if (IsReplicationTraderEventInstance(__instance))
                CompleteHostReplicationTraderParty(__instance, __state!, "native-end");
            instance?.SendHostReplicationEventTombstone(__instance, __state!, "native-end");
        }

        private static bool ReplicationEventDialogClosePrefix(object __instance, int __0)
        {
            if (!ShouldSuppressReplicationClientEvents()) return true;
            if (!EventChoiceLaneEnabled()) return false;
            if (TryFindClientReplicationEventForNativePhase(__instance, out var state) && state != null)
            {
                instance?.SendReplicationEventChoice(state, __0, "native-dialog");
            }
            return false;
        }

        private static void ReplicationEventDialogClosePostfix(object __instance)
        {
            if (!replicationConfigHostMode || !EventDialogLaneEnabled()) return;
            if (TryGetReplicationEventInstanceFromPhase(__instance, out var eventInstance) && eventInstance != null)
            {
                instance?.CaptureAndSendHostReplicationEvent(eventInstance, "dialog-close", false);
            }
        }

        private static bool ReplicationEventBlockingQueryPrefix(ref bool __result)
        {
            if (!ShouldUseReplicationClientEventQueries()) return true;
            __result = false;
            lock (ReplicationEventLock)
            {
                foreach (var state in ReplicationClientEvents.Values)
                {
                    if (state.Active && state.Blocking)
                    {
                        __result = true;
                        break;
                    }
                }
            }
            return false;
        }

        private static bool ReplicationEventObjectiveBlockingQueryPrefix(ref bool __result)
        {
            if (!ShouldUseReplicationClientEventQueries()) return true;
            __result = false;
            lock (ReplicationEventLock)
            {
                foreach (var state in ReplicationClientEvents.Values)
                {
                    if (state.Active && state.BlockingObjectiveButton)
                    {
                        __result = true;
                        break;
                    }
                }
            }
            return false;
        }

        private static bool ReplicationEventWeatherTextQueryPrefix(ref string __result)
        {
            if (!ShouldUseReplicationClientEventQueries()) return true;
            __result = string.Empty;
            lock (ReplicationEventLock)
            {
                foreach (var state in ReplicationClientEvents.Values)
                {
                    if (state.Active && !string.IsNullOrWhiteSpace(state.WeatherTextKey))
                    {
                        __result = state.WeatherTextKey;
                        break;
                    }
                }
            }
            return false;
        }

        private static bool ReplicationEventRunningQueryPrefix(string __0, ref bool __result)
        {
            if (!ShouldUseReplicationClientEventQueries()) return true;
            __result = false;
            lock (ReplicationEventLock)
            {
                foreach (var state in ReplicationClientEvents.Values)
                {
                    if (state.Active && string.Equals(state.BlueprintId, __0, StringComparison.Ordinal))
                    {
                        __result = true;
                        break;
                    }
                }
            }
            return false;
        }

        private static bool ShouldUseReplicationClientEventQueries()
        {
            return !replicationConfigHostMode
                && FullEventGraphAuthorityEnabled()
                && (replicationRuntimeStarted || multiplayerLoadingInProgress);
        }

        private static HostReplicationEventRecord? EnsureHostReplicationEventRecord(object nativeEvent, string source)
        {
            if (nativeEvent == null) return null;
            if (IsReplicationTraderEventInstance(nativeEvent) && IsHostReplicationTraderPartyAborted(nativeEvent)) return null;
            EnsureReplicationEventHostScope();
            lock (ReplicationEventLock)
            {
                if (ReplicationHostEvents.TryGetValue(nativeEvent, out var existing)) return existing;
                var blueprintId = ReadReplicationEventBlueprintId(nativeEvent);
                var record = new HostReplicationEventRecord
                {
                    NativeEvent = nativeEvent,
                    EventId = replicationEventHostSessionNonce
                        + ":" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                        + ":" + (++replicationEventHostCounter).ToString(CultureInfo.InvariantCulture),
                    BlueprintId = blueprintId,
                    Revision = 0L,
                    Source = source
                };
                ReplicationHostEvents[nativeEvent] = record;
                ReplicationHostEventsById[record.EventId] = record;
                return record;
            }
        }

        private static void RemoveFailedHostReplicationEventRecord(object nativeEvent)
        {
            lock (ReplicationEventLock)
            {
                if (!ReplicationHostEvents.TryGetValue(nativeEvent, out var record)) return;
                ReplicationHostEvents.Remove(nativeEvent);
                ReplicationHostEventsById.Remove(record.EventId);
            }
        }

        private static void EnsureReplicationEventHostScope()
        {
            var epoch = GetCurrentReplicationEventEpoch();
            lock (ReplicationEventLock)
            {
                if (string.IsNullOrEmpty(replicationEventHostSessionNonce))
                {
                    replicationEventHostSessionNonce = Guid.NewGuid().ToString("N").Substring(0, 12);
                }

                if (replicationEventHostEpoch == epoch) return;
                replicationEventHostEpoch = epoch;
                ReplicationHostEvents.Clear();
                ReplicationHostEventsById.Clear();
                replicationNextEventCheckpointRealtime = 0f;
            }
        }

        private static int GetCurrentReplicationEventEpoch()
        {
            var current = instance;
            return ReferenceEquals(current, null) ? 0 : Math.Max(0, current.multiplayerSaveTransfer.Epoch);
        }

        private void ProcessReplicationEventRuntime()
        {
            if (!replicationConfigEventReplication && !replicationConfigWeatherReplication)
            {
                HideReplicationEventPresentation();
                HideReplicationEventWarningPresentation();
                return;
            }

            if (replicationConfigHostMode)
            {
                if (EventRegistryReplicationLaneEnabled())
                {
                    ScanHostReplicationEventsIfDue();
                    SendHostReplicationEventCheckpointIfDue();
                }
            }
            else
            {
                if (FullEventGraphAuthorityEnabled()
                    && Time.realtimeSinceStartup >= replicationNextClientEventQuarantineRealtime)
                {
                    replicationNextClientEventQuarantineRealtime = Time.realtimeSinceStartup + ReplicationEventPollSeconds;
                    QuarantineReplicationClientNativeEvents(null, "runtime");
                }
                RetryPendingReplicationEventChoiceIfDue();
            }

            ProcessReplicationWeatherRuntime();
            UpdateReplicationEventWarningPresentation();
            UpdateReplicationEventPresentation();
        }

        private static void QuarantineReplicationClientNativeEvents(object? manager, string source)
        {
            if (replicationConfigHostMode || !FullEventGraphAuthorityEnabled()) return;
            try
            {
                var type = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEventSystem");
                manager = manager ?? (type == null ? null : ResolveReplicationUnityManagerInstance(type));
                if (manager == null || type == null) return;
                var runningProperty = AccessTools.Property(type, "RunningEvents");
                if (!(runningProperty?.GetValue(manager, null) is IList runningEvents)) return;
                var removed = runningEvents.Count;
                if (removed > 0) runningEvents.Clear();

                var runningIds = AccessTools.Field(type, "runningEventsID")?.GetValue(manager);
                if (runningIds != null)
                    AccessTools.Method(runningIds.GetType(), "Clear", Type.EmptyTypes)?.Invoke(runningIds, null);
                if (removed > 0)
                {
                    replicationEventsSuppressed += removed;
                    replicationLastEventSummary = "client-native-events-quarantined count="
                        + removed.ToString(CultureInfo.InvariantCulture) + " source=" + source;
                    instance?.LogReplicationInfo("Going Cooperative " + replicationLastEventSummary);
                }
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative client native event quarantine failed source="
                    + source + " error=" + FormatReflectionExceptionDetail(ex));
            }
        }

        private void ScanHostReplicationEventsIfDue()
        {
            if (Time.realtimeSinceStartup < replicationNextEventPollRealtime) return;
            replicationNextEventPollRealtime = Time.realtimeSinceStartup + ReplicationEventPollSeconds;
            if (!TryGetNativeRunningReplicationEvents(out var runningEvents)) return;
            var traderOnly = !EventLifecycleLaneEnabled() && TraderEventAuthorityEnabled();

            var active = new HashSet<object>(ReferenceObjectComparer.Instance);
            for (var i = 0; i < runningEvents.Count; i++)
            {
                var nativeEvent = runningEvents[i];
                if (nativeEvent == null) continue;
                if (traderOnly && !IsReplicationTraderEventInstance(nativeEvent)) continue;
                if (IsReplicationTraderEventInstance(nativeEvent) && IsHostReplicationTraderPartyAborted(nativeEvent)) continue;
                active.Add(nativeEvent);
                if (IsReplicationTraderEventInstance(nativeEvent) && TraderEventAuthorityEnabled())
                {
                    var traderRecord = EnsureHostReplicationEventRecord(nativeEvent, "trader-registry-scan");
                    if (traderRecord == null
                        || !EnsureHostReplicationTraderParty(nativeEvent, traderRecord.EventId, "registry-scan")) continue;
                }
                CaptureAndSendHostReplicationEvent(nativeEvent, "poll", false);
            }

            var ended = new List<HostReplicationEventRecord>();
            lock (ReplicationEventLock)
            {
                foreach (var pair in ReplicationHostEvents)
                {
                    if (traderOnly && !IsReplicationTraderEventInstance(pair.Key)) continue;
                    if (!active.Contains(pair.Key) && !pair.Value.TombstoneSent) ended.Add(pair.Value);
                }
            }
            for (var i = 0; i < ended.Count; i++)
            {
                SendHostReplicationEventTombstone(ended[i].NativeEvent, ended[i].EventId, "registry-missing");
            }
        }

        private void SendHostReplicationEventCheckpointIfDue()
        {
            if (Time.realtimeSinceStartup < replicationNextEventCheckpointRealtime) return;
            replicationNextEventCheckpointRealtime = Time.realtimeSinceStartup + ReplicationEventCheckpointSeconds;
            SendHostReplicationEventCheckpoint("periodic");
        }

        private void SendHostReplicationEventCheckpoint(string source)
        {
            if (!replicationConfigHostMode || !EventRegistryReplicationLaneEnabled() || !replicationRemoteHelloReceived) return;
            EnsureReplicationEventHostScope();
            if (!TryGetNativeRunningReplicationEvents(out var runningEvents))
            {
                replicationLastEventSummary = "checkpoint-collect-failed source=" + source;
                LogReplicationWarning("Going Cooperative event checkpoint collect failed source=" + source);
                return;
            }

            var prepared = new List<PreparedHostReplicationEvent>();
            var preparedEventIds = new HashSet<string>(StringComparer.Ordinal);
            var duplicateEvents = 0;
            var traderOnly = !EventLifecycleLaneEnabled() && TraderEventAuthorityEnabled();
            for (var i = 0; i < runningEvents.Count; i++)
            {
                var nativeEvent = runningEvents[i];
                if (nativeEvent == null) continue;
                if (traderOnly && !IsReplicationTraderEventInstance(nativeEvent)) continue;
                if (IsReplicationTraderEventInstance(nativeEvent) && IsHostReplicationTraderPartyAborted(nativeEvent)) continue;
                if (IsReplicationTraderEventInstance(nativeEvent) && TraderEventAuthorityEnabled())
                {
                    var traderRecord = EnsureHostReplicationEventRecord(nativeEvent, "trader-checkpoint");
                    if (traderRecord == null
                        || !EnsureHostReplicationTraderParty(nativeEvent, traderRecord.EventId, "event-checkpoint")) continue;
                }
                if (!TryPrepareHostReplicationEvent(nativeEvent, "checkpoint", out var preparedEvent) || preparedEvent == null)
                {
                    replicationLastEventSummary = "checkpoint-projection-incomplete projected="
                        + prepared.Count.ToString(CultureInfo.InvariantCulture)
                        + " total="
                        + runningEvents.Count.ToString(CultureInfo.InvariantCulture)
                        + " source="
                        + source;
                    LogReplicationWarning("Going Cooperative event " + replicationLastEventSummary);
                    return;
                }

                if (!preparedEventIds.Add(preparedEvent.Record.EventId))
                {
                    duplicateEvents++;
                    continue;
                }
                prepared.Add(preparedEvent);
            }
            prepared.Sort((left, right) => string.CompareOrdinal(left.Record.EventId, right.Record.EventId));
            if (duplicateEvents > 0)
            {
                LogReplicationWarning("Going Cooperative event checkpoint deduplicated repeated native registry entries count="
                    + duplicateEvents.ToString(CultureInfo.InvariantCulture)
                    + " source="
                    + source);
            }

            var snapshotId = ++replicationEventCheckpointSequence;

            SendReplicationEventDelta(
                GameEventRegistryBeginDeltaKind,
                0L,
                string.Empty,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " count=" + prepared.Count.ToString(CultureInfo.InvariantCulture)
                    + " source=" + FormatReplicationWorldObjectDetailToken(source));

            for (var i = 0; i < prepared.Count; i++)
            {
                CommitAndSendPreparedHostReplicationEvent(prepared[i], "checkpoint", true, snapshotId);
            }

            SendReplicationEventDelta(
                GameEventRegistryEndDeltaKind,
                0L,
                string.Empty,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " count=" + prepared.Count.ToString(CultureInfo.InvariantCulture));
            SendCurrentHostReplicationEventSpeedState("checkpoint");
            replicationLastEventSummary = "checkpoint-sent id=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count=" + prepared.Count.ToString(CultureInfo.InvariantCulture)
                + " source=" + source;
        }

        private bool CaptureAndSendHostReplicationEvent(object nativeEvent, string source, bool force, long snapshotId = 0L)
        {
            if (!replicationConfigHostMode
                || nativeEvent == null
                || (!EventLifecycleLaneEnabled()
                    && !(IsReplicationTraderEventInstance(nativeEvent) && TraderEventAuthorityEnabled()))) return false;
            return TryPrepareHostReplicationEvent(nativeEvent, source, out var prepared)
                && prepared != null
                && CommitAndSendPreparedHostReplicationEvent(prepared, source, force, snapshotId);
        }

        private bool TryPrepareHostReplicationEvent(
            object nativeEvent,
            string source,
            out PreparedHostReplicationEvent? prepared)
        {
            prepared = null;
            if (IsReplicationTraderEventInstance(nativeEvent) && IsHostReplicationTraderPartyAborted(nativeEvent)) return false;
            var record = EnsureHostReplicationEventRecord(nativeEvent, source);
            if (record != null
                && IsReplicationTraderEventInstance(nativeEvent)
                && TraderEventAuthorityEnabled()
                && !IsHostReplicationTraderPartyPublicationReady(nativeEvent, record.EventId)) return false;
            if (record == null || !TryBuildHostReplicationEventState(nativeEvent, record, out var state, out var signature)) return false;
            prepared = new PreparedHostReplicationEvent(record, state, signature);
            return true;
        }

        private bool CommitAndSendPreparedHostReplicationEvent(
            PreparedHostReplicationEvent prepared,
            string source,
            bool force,
            long snapshotId)
        {
            var record = prepared.Record;
            var state = prepared.State;
            var signature = prepared.Signature;

            lock (ReplicationEventLock)
            {
                if (!string.Equals(record.Signature, signature, StringComparison.Ordinal))
                {
                    record.Revision++;
                    record.Signature = signature;
                }
                state.Revision = record.Revision;
                record.LastState = state;
                record.TombstoneSent = false;
            }

            if (!force && string.Equals(record.LastSentSignature, signature, StringComparison.Ordinal)) return false;
            record.LastSentSignature = signature;
            var detail = FormatReplicationEventStateDetail(state, snapshotId, source);
            SendReplicationEventDelta(GameEventStateDeltaKind, state.Revision, state.BlueprintId, detail);
            replicationEventsSent++;
            replicationLastEventSummary = "state-sent eventId=" + state.EventId
                + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                + " phase=" + state.PhaseType
                + " source=" + source;
            if (replicationConfigEventDiagnostics)
            {
                LogReplicationInfo("Going Cooperative event state sent " + replicationLastEventSummary);
            }
            return true;
        }

        private void SendHostReplicationEventTombstone(object nativeEvent, string eventId, string source)
        {
            HostReplicationEventRecord? record;
            lock (ReplicationEventLock)
            {
                if (!ReplicationHostEventsById.TryGetValue(eventId, out record) || record.TombstoneSent) return;
                record.TombstoneSent = true;
                record.Revision++;
            }

            SendReplicationEventDelta(
                GameEventTombstoneDeltaKind,
                record.Revision,
                record.BlueprintId,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " eventIdB64=" + EncodeReplicationDetailBase64(record.EventId)
                    + " revision=" + record.Revision.ToString(CultureInfo.InvariantCulture)
                    + " source=" + FormatReplicationWorldObjectDetailToken(source));
            lock (ReplicationEventLock)
            {
                ReplicationHostEvents.Remove(nativeEvent);
                ReplicationHostEventsById.Remove(eventId);
            }
            replicationEventsSent++;
            replicationLastEventSummary = "tombstone-sent eventId=" + record.EventId + " source=" + source;
        }

        private void SendReplicationEventDelta(string kind, long uniqueId, string blueprintId, string detail)
        {
            SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                kind,
                uniqueId,
                blueprintId ?? string.Empty,
                0,
                0,
                0,
                detail));
        }

        private void CaptureAndSendReplicationEventNotice(object nativeEvent, string message)
        {
            if (!replicationConfigHostMode
                || !EventNoticeLaneEnabled()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived)
            {
                return;
            }
            var record = EnsureHostReplicationEventRecord(nativeEvent, "notice");
            if (record == null) return;
            EnsureReplicationEventHostScope();
            var noticeId = replicationEventHostSessionNonce
                + ":" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                + ":notice:" + (++replicationEventHostNoticeCounter).ToString(CultureInfo.InvariantCulture);
            var text = TruncateReplicationEventText(message, 1024);
            SendReplicationEventDelta(
                GameEventNoticeDeltaKind,
                replicationEventHostNoticeCounter,
                record.BlueprintId,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " noticeIdB64=" + EncodeReplicationDetailBase64(noticeId)
                    + " eventIdB64=" + EncodeReplicationDetailBase64(record.EventId)
                    + " textB64=" + EncodeReplicationDetailBase64(text));
            replicationEventNoticesSent++;
            replicationLastEventSummary = "notice-sent eventId=" + record.EventId + " noticeId=" + noticeId;
        }

        private static bool TryGetNativeRunningReplicationEvents(out IList runningEvents)
        {
            runningEvents = new ArrayList();
            try
            {
                var type = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEventSystem");
                var manager = type == null ? null : ResolveReplicationUnityManagerInstance(type);
                var property = type == null ? null : AccessTools.Property(type, "RunningEvents");
                if (manager == null || property == null || !(property.GetValue(manager, null) is IList list)) return false;
                runningEvents = list;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadReplicationEventBlueprintId(object nativeEvent)
        {
            try
            {
                if (!TryReadInstanceMemberValue(nativeEvent, "Blueprint", out var blueprint) || blueprint == null) return nativeEvent.GetType().Name;
                var getId = AccessTools.Method(blueprint.GetType(), "GetID", Type.EmptyTypes);
                return getId?.Invoke(blueprint, null)?.ToString() ?? nativeEvent.GetType().Name;
            }
            catch
            {
                return nativeEvent.GetType().Name;
            }
        }

        private static bool TryBuildHostReplicationEventState(
            object nativeEvent,
            HostReplicationEventRecord record,
            out ClientReplicationEventState state,
            out string signature)
        {
            state = new ClientReplicationEventState
            {
                Active = true,
                EventId = record.EventId,
                BlueprintId = record.BlueprintId,
                EventType = nativeEvent.GetType().Name,
                Family = ClassifyReplicationEventFamily(nativeEvent),
                Title = record.BlueprintId,
                ReceivedRealtime = Time.realtimeSinceStartup
            };
            signature = string.Empty;
            try
            {
                TryReadInstanceMemberValue(nativeEvent, "Blueprint", out var blueprint);
                if (blueprint != null)
                {
                    state.Category = ReadReplicationStringMember(blueprint, "Category");
                    state.Blocking = !ReadReplicationBoolMember(blueprint, "NonBlocking");
                    state.BlockingObjectiveButton = ReadReplicationBoolMember(blueprint, "IsBlockingObjectiveButton");
                }
                state.WeatherTextKey = ReadReplicationStringMember(nativeEvent, "ReplaceWeatherText");

                object? currentPhase = null;
                if (TryGetReplicationEventCurrentPhase(nativeEvent, out var phase) && phase != null)
                {
                    currentPhase = phase;
                    state.PhaseType = phase.GetType().Name;
                    var fullName = phase.GetType().FullName ?? state.PhaseType;
                    state.IsDialog = fullName.IndexOf("ShowDialogPhase", StringComparison.Ordinal) >= 0;
                    if (state.IsDialog)
                    {
                        state.DialogIndex = ReadReplicationIntMember(phase, "dialogIndex", -1);
                        state.DialogId = ReadReplicationStringMember(phase, "dialogId");
                        state.DialogClosed = ReadReplicationBoolMember(phase, "dialogWasClosed")
                            || ReadReplicationBoolMember(phase, "DialogWasClosed")
                            || ReadReplicationIntMember(phase, "switchPhaseIndexNextTick", -1) >= 0;
                        if (TryGetReplicationEventDialogContent(nativeEvent, state.DialogId, state.DialogIndex, out var content) && content != null)
                        {
                            if (string.IsNullOrWhiteSpace(state.DialogId)) state.DialogId = ReadReplicationStringMember(content, "Id");
                            if (TryBuildNativeReplicationEventDialog(nativeEvent, content, out var dialog) && dialog != null)
                            {
                                state.DialogProjectionComplete = true;
                                state.ShowCloseButton = ReadReplicationBoolMember(dialog, "ShowCloseButton");
                                state.Title = ReadReplicationStringMember(dialog, "WindowTitle");
                                state.ContentTitle = ReadReplicationStringMember(dialog, "ContentTitle");
                                state.Body = ReadReplicationStringMember(dialog, "ContentBodyText");
                                state.ImagePath = ReadReplicationStringMember(dialog, "ContentBodyImagePath");
                                ReadNativeReplicationEventDialogOptions(
                                    dialog,
                                    out state.Options,
                                    out state.OptionTooltips,
                                    out state.OptionDisabled,
                                    out state.OptionDisabledTooltips,
                                    out state.NativeOptionCount,
                                    out state.ChoiceContextComplete);
                            }
                            else
                            {
                                state.DialogProjectionComplete = false;
                                state.ChoiceContextComplete = false;
                                state.ShowCloseButton = ReadReplicationBoolMember(content, "ShowCloseButton");
                                state.Title = InvokeReplicationStringMethod(nativeEvent, "GetEventTitle", content, state.Title);
                                state.Body = InvokeReplicationStringMethod(nativeEvent, "GetEventInfo", content, string.Empty);
                                state.ImagePath = InvokeReplicationStringMethod(nativeEvent, "GetEventImagePath", content, string.Empty);
                                state.Options = ReadReplicationEventOptions(nativeEvent, content);
                                state.NativeOptionCount = state.Options.Length;
                                state.OptionTooltips = new string[state.Options.Length];
                                state.OptionDisabled = new bool[state.Options.Length];
                                state.OptionDisabledTooltips = new string[state.Options.Length];
                            }
                            var overrideImage = ReadReplicationStringMember(phase, "overrideDialogImage");
                            if (!string.IsNullOrWhiteSpace(overrideImage)) state.ImagePath = overrideImage;
                        }
                    }
                }
                if (replicationConfigEventWarningReplication)
                    ReadReplicationEventWarnings(nativeEvent, currentPhase, out state.Warnings, out state.WarningProjectionComplete);

                state.Title = TruncateReplicationEventText(state.Title, 256);
                state.ContentTitle = TruncateReplicationEventText(state.ContentTitle, 512);
                state.Body = TruncateReplicationEventText(state.Body, 4096);
                state.ImagePath = TruncateReplicationEventText(state.ImagePath, 512);
                state.WeatherTextKey = TruncateReplicationEventText(state.WeatherTextKey, 256);
                signature = state.BlueprintId + "|" + state.EventType + "|" + state.Family + "|" + state.Category + "|" + state.PhaseType
                    + "|" + state.IsDialog + "|" + state.DialogId + "|" + state.DialogIndex.ToString(CultureInfo.InvariantCulture)
                    + "|" + state.DialogClosed + "|" + state.ShowCloseButton + "|" + state.Blocking
                    + "|" + state.DialogProjectionComplete + "|" + state.ChoiceContextComplete
                    + "|" + state.NativeOptionCount.ToString(CultureInfo.InvariantCulture)
                    + "|" + state.BlockingObjectiveButton + "|" + state.WeatherTextKey
                    + "|" + state.WarningProjectionComplete + "|" + FormatReplicationEventWarnings(state.Warnings)
                    + "|" + state.Title + "|" + state.ContentTitle + "|" + state.Body + "|" + state.ImagePath
                    + "|" + string.Join(ReplicationEventOptionSeparator.ToString(), state.Options)
                    + "|" + string.Join(ReplicationEventOptionSeparator.ToString(), state.OptionTooltips)
                    + "|" + FormatReplicationEventOptionDisabled(state.OptionDisabled)
                    + "|" + string.Join(ReplicationEventOptionSeparator.ToString(), state.OptionDisabledTooltips);
                return true;
            }
            catch (Exception ex)
            {
                signature = "error|" + ex.GetType().Name + "|" + state.BlueprintId;
                instance?.LogReplicationWarning("Going Cooperative event state extraction failed event=" + state.BlueprintId + " error=" + FormatReflectionExceptionDetail(ex));
                return false;
            }
        }

        private static bool TryGetReplicationEventCurrentPhase(object nativeEvent, out object? phase)
        {
            phase = null;
            return TryReadInstanceMemberValue(nativeEvent, "stateMachine", out var machine)
                && machine != null
                && TryReadInstanceMemberValue(machine, "currentPhase", out phase)
                && phase != null;
        }

        private static string ClassifyReplicationEventFamily(object nativeEvent)
        {
            switch (nativeEvent.GetType().FullName)
            {
                case "NSMedieval.GameEventSystem.Events.InfoEvent":
                    return "Information";
                case "NSMedieval.GameEventSystem.Events.AnimalGroupEvent":
                case "NSMedieval.GameEventSystem.Events.AnimalPestEvent":
                    return "ExternalAnimals";
                case "NSMedieval.GameEventSystem.Events.BardVisitorEvent":
                case "NSMedieval.GameEventSystem.Events.PriestVisitorEvent":
                case "NSMedieval.GameEventSystem.Events.ShamanVisitorEvent":
                case "NSMedieval.GameEventSystem.Events.TraderEvent":
                case "NSMedieval.GameEventSystem.Events.MultiTraderEvent":
                case "NSMedieval.GameEventSystem.Events.BeggarEvent":
                case "NSMedieval.GameEventSystem.Events.ShowTradeDealLocationEvent":
                    return "VisitorsTrade";
                case "NSMedieval.GameEventSystem.Events.NewWorkerEvent":
                    return "Recruitment";
                case "NSMedieval.GameEventSystem.Events.RaidEvent":
                case "NSMedieval.GameEventSystem.Events.MultiRaidEvent":
                case "NSMedieval.GameEventSystem.Events.AmbushEvent":
                case "NSMedieval.GameEventSystem.Events.AttackCampEvent":
                case "NSMedieval.GameEventSystem.Events.ShakedownEvent":
                case "NSMedieval.GameEventSystem.Events.RunawayEvent":
                    return "ThreatCombat";
                case "NSMedieval.GameEventSystem.Events.AlterWeatherEvent":
                case "NSMedieval.GameEventSystem.Events.HailstormEvent":
                case "NSMedieval.GameEventSystem.Events.ThunderstormEvent":
                case "NSMedieval.GameEventSystem.Events.CropBlightEvent":
                case "NSMedieval.GameEventSystem.Events.RoomFloodEvent":
                    return "Environment";
                case "NSMedieval.GameEventSystem.Events.EndGameEvent":
                case "NSMedieval.GameEventSystem.Events.GameOverEvent":
                case "NSMedieval.GameEventSystem.Events.GameOverSecondMapEvent":
                    return "TerminalMeta";
                default:
                    return "Unknown";
            }
        }

        private static bool TryGetReplicationEventInstanceFromPhase(object phase, out object? nativeEvent)
        {
            nativeEvent = null;
            return TryReadInstanceMemberValue(phase, "EventInstance", out nativeEvent)
                && nativeEvent != null;
        }

        private static bool TryGetReplicationEventDialogContent(object nativeEvent, string dialogId, int dialogIndex, out object? content)
        {
            content = null;
            try
            {
                MethodInfo? method;
                object[] args;
                if (!string.IsNullOrWhiteSpace(dialogId))
                {
                    method = AccessTools.Method(nativeEvent.GetType(), "GetDialogContent", new[] { typeof(string) });
                    args = new object[] { dialogId };
                }
                else
                {
                    method = AccessTools.Method(nativeEvent.GetType(), "GetDialogContent", new[] { typeof(int) });
                    args = new object[] { Math.Max(0, dialogIndex) };
                }
                content = method?.Invoke(nativeEvent, args);
                return content != null;
            }
            catch
            {
                return false;
            }
        }

        private static string[] ReadReplicationEventOptions(object nativeEvent, object content)
        {
            var result = new List<string>();
            try
            {
                var localizationType = AccessTools.TypeByName("NSMedieval.Controllers.LocalizationController");
                var localization = localizationType == null ? null : ResolveReplicationUnityManagerInstance(localizationType);
                var getText = localizationType == null ? null : AccessTools.Method(localizationType, "GetText", new[] { typeof(string) });
                if (TryReadInstanceMemberValue(content, "Options", out var options) && options is IEnumerable enumerable)
                {
                    foreach (var option in enumerable)
                    {
                        if (result.Count >= 8) break;
                        var text = option?.ToString() ?? string.Empty;
                        if (localization != null && getText != null)
                            text = getText.Invoke(localization, new object[] { text })?.ToString() ?? text;
                        var process = AccessTools.Method(nativeEvent.GetType(), "ProcessLocalizedButtonText", new[] { typeof(string) });
                        if (process != null) text = process.Invoke(nativeEvent, new object[] { text })?.ToString() ?? text;
                        result.Add(TruncateReplicationEventText(text.Replace(ReplicationEventOptionSeparator, ' '), 256));
                    }
                }
            }
            catch
            {
                // A missing localized option still leaves a usable Continue action.
            }
            return result.ToArray();
        }

        private static bool TryBuildNativeReplicationEventDialog(object nativeEvent, object content, out object? dialog)
        {
            dialog = null;
            try
            {
                var utilType = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEventUtil");
                if (utilType == null) return false;
                MethodInfo? buildDialog = null;
                var methods = utilType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                for (var i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, "BuildDialogContent", StringComparison.Ordinal)) continue;
                    var parameters = methods[i].GetParameters();
                    if (parameters.Length != 2
                        || !parameters[0].ParameterType.IsInstanceOfType(nativeEvent)
                        || !parameters[1].ParameterType.IsInstanceOfType(content))
                    {
                        continue;
                    }
                    buildDialog = methods[i];
                    break;
                }
                dialog = buildDialog?.Invoke(null, new[] { nativeEvent, content });
                return dialog != null;
            }
            catch
            {
                return false;
            }
        }

        private static void ReadNativeReplicationEventDialogOptions(
            object dialog,
            out string[] labels,
            out string[] tooltips,
            out bool[] disabled,
            out string[] disabledTooltips,
            out int nativeOptionCount,
            out bool contextComplete)
        {
            var labelList = new List<string>();
            var tooltipList = new List<string>();
            var disabledList = new List<bool>();
            var disabledTooltipList = new List<string>();
            nativeOptionCount = 0;
            contextComplete = true;
            try
            {
                if (TryReadInstanceMemberValue(dialog, "Options", out var options) && options is IEnumerable enumerable)
                {
                    foreach (var option in enumerable)
                    {
                        if (option == null) continue;
                        nativeOptionCount++;
                        if (labelList.Count >= 8)
                        {
                            contextComplete = false;
                            continue;
                        }
                        var optionLabel = ReadReplicationStringMember(option, "Text").Replace(ReplicationEventOptionSeparator, ' ');
                        if (optionLabel.Length > 256) contextComplete = false;
                        labelList.Add(TruncateReplicationEventText(optionLabel, 256));
                        tooltipList.Add(ReadNativeReplicationEventOptionTooltip(option, out var tooltipComplete));
                        disabledList.Add(ReadReplicationBoolMember(option, "Disabled"));
                        disabledTooltipList.Add(TruncateReplicationEventText(
                            ReadReplicationStringMember(option, "DisabledTooltip").Replace(ReplicationEventOptionSeparator, ' '),
                            1024));
                        contextComplete &= tooltipComplete;
                    }
                }
            }
            catch
            {
                // Missing optional tooltip data must not make the dialog unusable.
                contextComplete = false;
            }
            labels = labelList.ToArray();
            tooltips = tooltipList.ToArray();
            disabled = disabledList.ToArray();
            disabledTooltips = disabledTooltipList.ToArray();
        }

        private static string FormatReplicationEventOptionDisabled(bool[] values)
        {
            if (values.Length == 0) return string.Empty;
            var result = new StringBuilder(values.Length);
            for (var i = 0; i < values.Length; i++) result.Append(values[i] ? '1' : '0');
            return result.ToString();
        }

        private static string ReadNativeReplicationEventOptionTooltip(object option, out bool complete)
        {
            var lines = new List<string>();
            complete = true;
            try
            {
                if (TryReadInstanceMemberValue(option, "Tooltips", out var tooltips) && tooltips is IEnumerable enumerable)
                {
                    foreach (var tooltip in enumerable)
                    {
                        if (tooltip == null) continue;
                        if (lines.Count >= 8)
                        {
                            complete = false;
                            break;
                        }
                        if (TryReadInstanceMemberValue(tooltip, "Humanoid", out var humanoid) && humanoid != null)
                            complete = false;
                        var key = ReadReplicationStringMember(tooltip, "Key");
                        var title = LocalizeReplicationEventText("dialogOptionEffect_" + key);
                        if (!string.IsNullOrWhiteSpace(title)) lines.Add(title);
                        if (TryReadInstanceMemberValue(tooltip, "Args", out var args) && args is IEnumerable argumentEnumerable)
                        {
                            foreach (var argument in argumentEnumerable)
                            {
                                if (argument == null) continue;
                                if (lines.Count >= 8)
                                {
                                    complete = false;
                                    break;
                                }
                                var text = argument.ToString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Tooltips are context only; the option label remains authoritative.
                complete = false;
            }
            var result = string.Join("\n", lines).Replace(ReplicationEventOptionSeparator, ' ');
            if (result.Length > 1024) complete = false;
            return TruncateReplicationEventText(result, 1024);
        }

        private static string LocalizeReplicationEventText(string key)
        {
            try
            {
                var localizationType = AccessTools.TypeByName("NSMedieval.Controllers.LocalizationController");
                var localization = localizationType == null ? null : ResolveReplicationUnityManagerInstance(localizationType);
                var getText = localizationType == null ? null : AccessTools.Method(localizationType, "GetText", new[] { typeof(string) });
                return localization == null || getText == null
                    ? key
                    : getText.Invoke(localization, new object[] { key })?.ToString() ?? key;
            }
            catch
            {
                return key;
            }
        }

        private static void ReadReplicationEventWarnings(
            object nativeEvent,
            object? phase,
            out ClientReplicationEventWarningState[] warnings,
            out bool complete)
        {
            var candidates = new List<KeyValuePair<object, object>>();
            var seen = new HashSet<object>(ReferenceObjectComparer.Instance);
            AddReplicationEventWarningCandidate(nativeEvent, "warningMessage", nativeEvent, candidates, seen);
            if (phase != null)
            {
                AddReplicationEventWarningCandidate(phase, "CountdownMessage", phase, candidates, seen);
                if (TryReadInstanceMemberValue(phase, "countdown", out var countdown) && countdown != null)
                    AddReplicationEventWarningCandidate(countdown, "CountdownMessage", countdown, candidates, seen);
            }

            complete = candidates.Count <= 2;
            var result = new List<ClientReplicationEventWarningState>();
            for (var i = 0; i < candidates.Count && result.Count < 2; i++)
            {
                result.Add(BuildReplicationEventWarningState(
                    candidates[i].Key,
                    candidates[i].Value,
                    nativeEvent,
                    ref complete));
            }
            warnings = result.ToArray();
        }

        private static void AddReplicationEventWarningCandidate(
            object source,
            string memberName,
            object owner,
            List<KeyValuePair<object, object>> candidates,
            HashSet<object> seen)
        {
            if (!TryReadInstanceMemberValue(source, memberName, out var value)
                || value == null
                || !seen.Add(value))
            {
                return;
            }
            candidates.Add(new KeyValuePair<object, object>(value, owner));
        }

        private static ClientReplicationEventWarningState BuildReplicationEventWarningState(
            object warning,
            object owner,
            object nativeEvent,
            ref bool complete)
        {
            var state = new ClientReplicationEventWarningState
            {
                Id = BoundReplicationEventWarningText(ReadReplicationStringMember(warning, "ID"), 128, ref complete),
                Text = BoundReplicationEventWarningText(ReadReplicationStringMember(warning, "Text"), 1024, ref complete),
                Tooltip = BoundReplicationEventWarningText(ReadReplicationStringMember(warning, "Tooltip"), 2048, ref complete),
                Icon = BoundReplicationEventWarningText(ReadReplicationStringMember(warning, "Icon"), 512, ref complete),
                Timer = ReadReplicationIntMember(warning, "Timer", 0),
                ShouldShow = ReadReplicationBoolMember(warning, "ShouldShow")
            };
            if (TryReadInstanceMemberValue(warning, "FactionInstance", out var faction) && faction != null)
                state.FactionName = BoundReplicationEventWarningText(ReadReplicationStringMember(faction, "NameLocalized"), 256, ref complete);

            var extraLines = new List<string>();
            AppendReplicationEventWarningLines(nativeEvent, "WarningTooltipPrefixLines", extraLines, ref complete);
            AppendReplicationEventWarningLines(owner, "additionalTooltipLines", extraLines, ref complete);
            state.ExtraLines = extraLines.ToArray();
            if (state.Timer < -1 || state.Timer > 10000000) complete = false;
            return state;
        }

        private static void AppendReplicationEventWarningLines(
            object owner,
            string memberName,
            List<string> lines,
            ref bool complete)
        {
            if (!TryReadInstanceMemberValue(owner, memberName, out var value) || !(value is IEnumerable enumerable)) return;
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                if (lines.Count >= 8)
                {
                    complete = false;
                    break;
                }
                lines.Add(BoundReplicationEventWarningText(
                    (item.ToString() ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '),
                    512,
                    ref complete));
            }
        }

        private static string BoundReplicationEventWarningText(string value, int maximum, ref bool complete)
        {
            var sanitized = (value ?? string.Empty)
                .Replace(ReplicationEventWarningFieldSeparator, ' ')
                .Replace(ReplicationEventWarningEntrySeparator, ' ');
            if (sanitized.Length > maximum) complete = false;
            return TruncateReplicationEventText(sanitized, maximum);
        }

        private static string FormatReplicationEventWarnings(ClientReplicationEventWarningState[] warnings)
        {
            var entries = new string[warnings.Length];
            for (var i = 0; i < warnings.Length; i++)
            {
                var warning = warnings[i];
                entries[i] = string.Join(ReplicationEventWarningFieldSeparator.ToString(), new[]
                {
                    warning.Id,
                    warning.Text,
                    warning.Tooltip,
                    warning.Icon,
                    warning.Timer.ToString(CultureInfo.InvariantCulture),
                    warning.ShouldShow ? "1" : "0",
                    warning.FactionName,
                    string.Join("\n", warning.ExtraLines)
                });
            }
            return string.Join(ReplicationEventWarningEntrySeparator.ToString(), entries);
        }

        private static string InvokeReplicationStringMethod(object owner, string name, object argument, string fallback)
        {
            try
            {
                var method = AccessTools.Method(owner.GetType(), name);
                return method?.Invoke(owner, new[] { argument })?.ToString() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static string ReadReplicationStringMember(object owner, string name)
        {
            return TryReadInstanceMemberValue(owner, name, out var value) && value != null ? value.ToString() ?? string.Empty : string.Empty;
        }

        private static bool ReadReplicationBoolMember(object owner, string name)
        {
            if (!TryReadInstanceMemberValue(owner, name, out var value) || value == null) return false;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private static int ReadReplicationIntMember(object owner, string name, int fallback)
        {
            if (!TryReadInstanceMemberValue(owner, name, out var value) || value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string TruncateReplicationEventText(string? value, int maximum)
        {
            var text = value ?? string.Empty;
            return text.Length <= maximum ? text : text.Substring(0, maximum);
        }

        private static string FormatReplicationEventStateDetail(ClientReplicationEventState state, long snapshotId, string source)
        {
            return "scope=" + replicationEventHostSessionNonce
                + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " eventIdB64=" + EncodeReplicationDetailBase64(state.EventId)
                + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                + " typeB64=" + EncodeReplicationDetailBase64(state.EventType)
                + " familyB64=" + EncodeReplicationDetailBase64(state.Family)
                + " categoryB64=" + EncodeReplicationDetailBase64(state.Category)
                + " phaseB64=" + EncodeReplicationDetailBase64(state.PhaseType)
                + " dialog=" + FormatReplicationBool(state.IsDialog)
                + " dialogIdB64=" + EncodeReplicationDetailBase64(state.DialogId)
                + " dialogIndex=" + state.DialogIndex.ToString(CultureInfo.InvariantCulture)
                + " dialogClosed=" + FormatReplicationBool(state.DialogClosed)
                + " showClose=" + FormatReplicationBool(state.ShowCloseButton)
                + " projectionComplete=" + FormatReplicationBool(state.DialogProjectionComplete)
                + " choiceContextComplete=" + FormatReplicationBool(state.ChoiceContextComplete)
                + " nativeOptionCount=" + state.NativeOptionCount.ToString(CultureInfo.InvariantCulture)
                + " blocking=" + FormatReplicationBool(state.Blocking)
                + " blockingObjective=" + FormatReplicationBool(state.BlockingObjectiveButton)
                + " weatherTextB64=" + EncodeReplicationDetailBase64(state.WeatherTextKey)
                + " warningsComplete=" + FormatReplicationBool(state.WarningProjectionComplete)
                + " warningsB64=" + EncodeReplicationDetailBase64(FormatReplicationEventWarnings(state.Warnings))
                + " titleB64=" + EncodeReplicationDetailBase64(state.Title)
                + " contentTitleB64=" + EncodeReplicationDetailBase64(state.ContentTitle)
                + " bodyB64=" + EncodeReplicationDetailBase64(state.Body)
                + " imageB64=" + EncodeReplicationDetailBase64(state.ImagePath)
                + " optionsB64=" + EncodeReplicationDetailBase64(string.Join(ReplicationEventOptionSeparator.ToString(), state.Options))
                + " optionTooltipsB64=" + EncodeReplicationDetailBase64(string.Join(ReplicationEventOptionSeparator.ToString(), state.OptionTooltips))
                + " optionDisabled=" + (state.OptionDisabled.Length == 0 ? "_" : FormatReplicationEventOptionDisabled(state.OptionDisabled))
                + " optionDisabledTooltipsB64=" + EncodeReplicationDetailBase64(string.Join(ReplicationEventOptionSeparator.ToString(), state.OptionDisabledTooltips))
                + " source=" + FormatReplicationWorldObjectDetailToken(source);
        }

        private static bool IsReplicationEventDeltaKind(string deltaKind)
        {
            return string.Equals(deltaKind, GameEventRegistryBeginDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, GameEventStateDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, GameEventRegistryEndDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, GameEventTombstoneDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, GameEventSpeedStateDeltaKind, StringComparison.Ordinal)
                || string.Equals(deltaKind, GameEventNoticeDeltaKind, StringComparison.Ordinal)
                || IsReplicationTraderPartyDeltaKind(deltaKind)
                || IsReplicationWeatherDeltaKind(deltaKind);
        }

        private static bool TryApplyReplicationEventWorldDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (IsReplicationWeatherDeltaKind(delta.DeltaKind)) return TryApplyReplicationWeatherWorldDelta(delta, out detail);
            if (!EventRegistryReplicationLaneEnabled())
            {
                detail = "event-lane-disabled";
                return false;
            }
            if (replicationConfigHostMode)
            {
                detail = "event-delta-ignored-on-host";
                return false;
            }
            if (!EventLifecycleLaneEnabled()
                && string.Equals(delta.DeltaKind, GameEventStateDeltaKind, StringComparison.Ordinal)
                && !IsReplicationTraderEventBlueprintId(delta.BlueprintId))
            {
                detail = "event-state-unsupported-by-trader-only-lane";
                return false;
            }

            if (string.Equals(delta.DeltaKind, GameEventRegistryBeginDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationEventRegistryBegin(delta, out detail);
            if (string.Equals(delta.DeltaKind, GameEventStateDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationEventState(delta, out detail);
            if (string.Equals(delta.DeltaKind, GameEventRegistryEndDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationEventRegistryEnd(delta, out detail);
            if (string.Equals(delta.DeltaKind, GameEventTombstoneDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationEventTombstone(delta, out detail);
            if (string.Equals(delta.DeltaKind, GameEventSpeedStateDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationEventSpeedState(delta, out detail);
            if (string.Equals(delta.DeltaKind, GameEventNoticeDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationEventNotice(delta, out detail);
            detail = "event-delta-unsupported";
            return false;
        }

        private static bool TryApplyReplicationEventRegistryBegin(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventLong(delta.Detail, "snapshotId", out var snapshotId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "count", out var expectedCount)
                || snapshotId <= 0L || expectedCount < 0 || expectedCount > ReplicationEventMaxCheckpointStates)
            {
                detail = "event-registry-begin-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            EventCheckpointRecordResult result;
            var completedSnapshotId = -1L;
            var removed = 0;
            lock (ReplicationEventLock)
            {
                result = ReplicationClientEventCheckpoints.RecordBegin(snapshotId, expectedCount, delta.Sequence);
                if (result == EventCheckpointRecordResult.Conflict
                    || result == EventCheckpointRecordResult.CapacityExceeded)
                {
                    ReplicationClientEventCheckpoints.AbandonThrough(snapshotId);
                }
                else if (result == EventCheckpointRecordResult.Accepted
                    || result == EventCheckpointRecordResult.Duplicate)
                {
                    TryFinalizeReadyReplicationEventCheckpoint(out completedSnapshotId, out removed);
                }
            }
            if (result == EventCheckpointRecordResult.Conflict
                || result == EventCheckpointRecordResult.CapacityExceeded)
            {
                LogReplicationEventCheckpointAbandoned("begin", snapshotId, result);
            }
            detail = "ok event-registry-begin-" + FormatReplicationEventCheckpointResult(result)
                + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                + FormatReplicationEventCheckpointCompletion(completedSnapshotId, removed);
            return true;
        }

        private static bool TryApplyReplicationEventState(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryParseReplicationEventState(delta, out var scope, out var epoch, out var snapshotId, out var state, out detail)) return false;
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            EventCheckpointRecordResult checkpointResult = EventCheckpointRecordResult.Accepted;
            var checkpointAbandoned = false;
            var completedSnapshotId = -1L;
            var removed = 0;
            var stateResult = string.Empty;
            var appliedNewState = false;
            lock (ReplicationEventLock)
            {
                if (snapshotId > 0L)
                {
                    checkpointResult = ReplicationClientEventCheckpoints.RecordState(
                        snapshotId,
                        state.EventId,
                        delta.Sequence);
                    if (checkpointResult == EventCheckpointRecordResult.Stale
                        || checkpointResult == EventCheckpointRecordResult.Superseded)
                    {
                        detail = "ok event-state-" + FormatReplicationEventCheckpointResult(checkpointResult)
                            + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                            + " eventId=" + state.EventId;
                        return true;
                    }
                    if (checkpointResult == EventCheckpointRecordResult.Conflict
                        || checkpointResult == EventCheckpointRecordResult.CapacityExceeded)
                    {
                        ReplicationClientEventCheckpoints.AbandonThrough(snapshotId);
                        checkpointAbandoned = true;
                    }
                }

                if (!checkpointAbandoned)
                {
                    ReplicationClientEvents.TryGetValue(state.EventId, out var current);
                    if (ReplicationClientEventRevisionHighWater.TryGetValue(state.EventId, out var highWater)
                        && highWater >= state.Revision)
                    {
                        if (current != null)
                        {
                            current.LastHostDeltaSequence = Math.Max(current.LastHostDeltaSequence, delta.Sequence);
                        }
                        stateResult = "event-state-high-water eventId=" + state.EventId;
                    }
                    else if (current != null && current.Revision > state.Revision)
                    {
                        current.LastHostDeltaSequence = Math.Max(current.LastHostDeltaSequence, delta.Sequence);
                        stateResult = "event-state-stale eventId=" + state.EventId;
                    }
                    else if (current != null && current.Revision == state.Revision)
                    {
                        current.LastHostDeltaSequence = Math.Max(current.LastHostDeltaSequence, delta.Sequence);
                        stateResult = "event-state-duplicate eventId=" + state.EventId;
                    }
                    else
                    {
                        state.ReceivedRealtime = Time.realtimeSinceStartup;
                        state.LastHostDeltaSequence = delta.Sequence;
                        ReplicationClientEvents[state.EventId] = state;
                        ReplicationClientEventRevisionHighWater[state.EventId] = state.Revision;
                        if (replicationPendingEventChoice != null
                            && string.Equals(replicationPendingEventChoice.EventId, state.EventId, StringComparison.Ordinal)
                            && (state.Revision > replicationPendingEventChoice.EventRevision || state.DialogClosed || !state.IsDialog))
                        {
                            replicationPendingEventChoice = null;
                        }
                        appliedNewState = true;
                        stateResult = "state-applied eventId=" + state.EventId
                            + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                            + " phase=" + state.PhaseType;
                    }
                    if (snapshotId > 0L)
                    {
                        TryFinalizeReadyReplicationEventCheckpoint(out completedSnapshotId, out removed);
                    }
                }
            }
            if (checkpointAbandoned)
            {
                LogReplicationEventCheckpointAbandoned("state", snapshotId, checkpointResult);
                detail = "ok event-state-checkpoint-abandoned snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " reason="
                    + FormatReplicationEventCheckpointResult(checkpointResult);
                return true;
            }
            if (appliedNewState)
            {
                replicationEventsApplied++;
                replicationLastEventSummary = stateResult;
            }
            detail = "ok " + stateResult
                + FormatReplicationEventCheckpointCompletion(completedSnapshotId, removed);
            return true;
        }

        private static bool TryApplyReplicationEventRegistryEnd(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventLong(delta.Detail, "snapshotId", out var snapshotId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "count", out var endCount)
                || snapshotId <= 0L || endCount < 0 || endCount > ReplicationEventMaxCheckpointStates)
            {
                detail = "event-registry-end-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            EventCheckpointRecordResult result;
            var completedSnapshotId = -1L;
            var removed = 0;
            lock (ReplicationEventLock)
            {
                result = ReplicationClientEventCheckpoints.RecordEnd(snapshotId, endCount, delta.Sequence);
                if (result == EventCheckpointRecordResult.Conflict
                    || result == EventCheckpointRecordResult.CapacityExceeded)
                {
                    ReplicationClientEventCheckpoints.AbandonThrough(snapshotId);
                }
                else if (result == EventCheckpointRecordResult.Accepted
                    || result == EventCheckpointRecordResult.Duplicate)
                {
                    TryFinalizeReadyReplicationEventCheckpoint(out completedSnapshotId, out removed);
                }
            }
            if (result == EventCheckpointRecordResult.Conflict
                || result == EventCheckpointRecordResult.CapacityExceeded)
            {
                LogReplicationEventCheckpointAbandoned("end", snapshotId, result);
            }
            detail = "ok event-registry-end-" + FormatReplicationEventCheckpointResult(result)
                + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                + FormatReplicationEventCheckpointCompletion(completedSnapshotId, removed);
            return true;
        }

        private static bool TryFinalizeReadyReplicationEventCheckpoint(out long snapshotId, out int removed)
        {
            snapshotId = -1L;
            removed = 0;
            if (!ReplicationClientEventCheckpoints.TryTakeNewestComplete(out var completion) || completion == null)
                return false;

            var stale = new List<string>();
            foreach (var pair in ReplicationClientEvents)
            {
                if (ReplicationOrderingPolicy.ShouldPruneCheckpointEvent(
                    completion.ContainsEvent(pair.Key),
                    pair.Value.LastHostDeltaSequence,
                    completion.BeginSequence))
                {
                    stale.Add(pair.Key);
                }
            }
            for (var i = 0; i < stale.Count; i++)
            {
                if (ReplicationClientEvents.TryGetValue(stale[i], out var staleState))
                {
                    ReplicationClientEventRevisionHighWater[stale[i]] = Math.Max(
                        staleState.Revision,
                        ReplicationClientEventRevisionHighWater.TryGetValue(stale[i], out var currentHighWater)
                            ? currentHighWater
                            : -1L);
                }
                ReplicationClientEvents.Remove(stale[i]);
                RemoveClientReplicationTraderParty(stale[i], "event-checkpoint-prune");
                if (replicationPendingEventChoice != null
                    && string.Equals(replicationPendingEventChoice.EventId, stale[i], StringComparison.Ordinal))
                {
                    replicationPendingEventChoice = null;
                }
                removed++;
            }
            snapshotId = completion.SnapshotId;
            return true;
        }

        private static string FormatReplicationEventCheckpointCompletion(long snapshotId, int removed)
        {
            return snapshotId < 0L
                ? string.Empty
                : " completedSnapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " removed=" + removed.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatReplicationEventCheckpointResult(EventCheckpointRecordResult result)
        {
            return result.ToString().ToLowerInvariant();
        }

        private static void LogReplicationEventCheckpointAbandoned(
            string recordKind,
            long snapshotId,
            EventCheckpointRecordResult result)
        {
            replicationLastEventSummary = "checkpoint-abandoned id="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " record="
                + recordKind
                + " reason="
                + FormatReplicationEventCheckpointResult(result);
            instance?.LogReplicationWarning("Going Cooperative event " + replicationLastEventSummary);
        }

        private static bool TryApplyReplicationEventTombstone(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventText(delta.Detail, "eventIdB64", out var eventId)
                || !TryReadReplicationEventLong(delta.Detail, "revision", out var revision))
            {
                detail = "event-tombstone-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            lock (ReplicationEventLock)
            {
                if (ReplicationClientEvents.TryGetValue(eventId, out var state) && state.Revision > revision)
                {
                    detail = "ok event-tombstone-stale eventId=" + eventId;
                    return true;
                }
                ReplicationClientEvents.Remove(eventId);
                ReplicationClientEventRevisionHighWater[eventId] = Math.Max(
                    revision,
                    ReplicationClientEventRevisionHighWater.TryGetValue(eventId, out var highWater) ? highWater : -1L);
                if (replicationPendingEventChoice != null && string.Equals(replicationPendingEventChoice.EventId, eventId, StringComparison.Ordinal))
                    replicationPendingEventChoice = null;
            }
            RemoveClientReplicationTraderParty(eventId, "event-tombstone");
            replicationEventsApplied++;
            detail = "ok event-tombstone eventId=" + eventId;
            return true;
        }

        private static bool TryAcceptReplicationEventScope(string scope, int epoch, out string detail)
        {
            lock (ReplicationEventLock)
            {
                if (string.IsNullOrWhiteSpace(scope) || epoch < 0)
                {
                    detail = "event-scope-invalid";
                    return false;
                }
                var expectedEpoch = GetCurrentReplicationEventEpoch();
                if (epoch != expectedEpoch)
                {
                    detail = "event-epoch-not-current expected=" + expectedEpoch.ToString(CultureInfo.InvariantCulture)
                        + " actual=" + epoch.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (ReplicationRetiredEventScopes.Contains(scope))
                {
                    detail = "event-scope-retired";
                    return false;
                }
                if (string.IsNullOrEmpty(replicationEventClientSessionNonce)
                    || !string.Equals(replicationEventClientSessionNonce, scope, StringComparison.Ordinal))
                {
                    RetireReplicationEventScope(replicationEventClientSessionNonce);
                    replicationEventClientSessionNonce = scope;
                    replicationEventClientEpoch = epoch;
                    ReplicationClientEvents.Clear();
                    ReplicationClientEventRevisionHighWater.Clear();
                    ReplicationClientEventCheckpoints.Reset();
                    ReplicationClientEventNoticeSeen.Clear();
                    ReplicationClientEventNoticeSeenOrder.Clear();
                    replicationPendingEventChoice = null;
                    ReplicationClientWeatherInstances.Clear();
                    ReplicationClientWeatherCheckpointSeen.Clear();
                    ReplicationClientWeatherForecastPagesSeen.Clear();
                    replicationWeatherClientCheckpointId = -1L;
                    replicationWeatherClientCompletedCheckpointId = -1L;
                    replicationWeatherClientExpectedSchedules = 0;
                    replicationWeatherClientExpectedForecastPages = 0;
                    replicationWeatherClientExpectedForecastHours = 0;
                    replicationLastEventSpeedAppliedSequence = -1L;
                    replicationLastWeatherEnvironmentAppliedSequence = -1L;
                    ResetReplicationTraderParties(ReplicationTraderPartyResetContext.ScopeChangedSameWorld);
                    detail = "ok event-scope-reset";
                    return true;
                }
                if (epoch < replicationEventClientEpoch)
                {
                    detail = "event-epoch-stale expected=" + replicationEventClientEpoch.ToString(CultureInfo.InvariantCulture)
                        + " actual=" + epoch.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (epoch > replicationEventClientEpoch)
                {
                    replicationEventClientEpoch = epoch;
                    ReplicationClientEvents.Clear();
                    ReplicationClientEventRevisionHighWater.Clear();
                    ReplicationClientEventCheckpoints.Reset();
                    ReplicationClientEventNoticeSeen.Clear();
                    ReplicationClientEventNoticeSeenOrder.Clear();
                    replicationPendingEventChoice = null;
                    ReplicationClientWeatherInstances.Clear();
                    ReplicationClientWeatherCheckpointSeen.Clear();
                    ReplicationClientWeatherForecastPagesSeen.Clear();
                    replicationWeatherClientCheckpointId = -1L;
                    replicationWeatherClientCompletedCheckpointId = -1L;
                    replicationWeatherClientExpectedSchedules = 0;
                    replicationWeatherClientExpectedForecastPages = 0;
                    replicationWeatherClientExpectedForecastHours = 0;
                    replicationLastEventSpeedAppliedSequence = -1L;
                    replicationLastWeatherEnvironmentAppliedSequence = -1L;
                    ResetReplicationTraderParties(ReplicationTraderPartyResetContext.ScopeChangedSameWorld);
                }
            }
            detail = "ok event-scope";
            return true;
        }

        private static void RetireReplicationEventScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope) || !ReplicationRetiredEventScopes.Add(scope)) return;
            ReplicationRetiredEventScopeOrder.Enqueue(scope);
            while (ReplicationRetiredEventScopeOrder.Count > 16)
            {
                ReplicationRetiredEventScopes.Remove(ReplicationRetiredEventScopeOrder.Dequeue());
            }
        }

        private static bool TryParseReplicationEventState(
            ReplicationWorldObjectDelta delta,
            out string scope,
            out int epoch,
            out long snapshotId,
            out ClientReplicationEventState state,
            out string detail)
        {
            scope = string.Empty;
            epoch = -1;
            snapshotId = 0L;
            state = new ClientReplicationEventState();
            detail = string.Empty;
            if (!TryReadReplicationEventEnvelope(delta.Detail, out scope, out epoch)
                || !TryReadReplicationEventLong(delta.Detail, "snapshotId", out snapshotId)
                || !TryReadReplicationEventText(delta.Detail, "eventIdB64", out state.EventId)
                || !TryReadReplicationEventLong(delta.Detail, "revision", out state.Revision)
                || string.IsNullOrWhiteSpace(state.EventId)
                || snapshotId < 0L
                || state.Revision < 0)
            {
                detail = "event-state-envelope-malformed";
                return false;
            }
            state.Active = true;
            state.BlueprintId = delta.BlueprintId;
            TryReadReplicationEventText(delta.Detail, "typeB64", out state.EventType);
            TryReadReplicationEventText(delta.Detail, "familyB64", out state.Family);
            TryReadReplicationEventText(delta.Detail, "categoryB64", out state.Category);
            TryReadReplicationEventText(delta.Detail, "phaseB64", out state.PhaseType);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "dialog", out state.IsDialog);
            TryReadReplicationEventText(delta.Detail, "dialogIdB64", out state.DialogId);
            TryReadReplicationWorldObjectDetailInt(delta.Detail, "dialogIndex", out state.DialogIndex);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "dialogClosed", out state.DialogClosed);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "showClose", out state.ShowCloseButton);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "projectionComplete", out state.DialogProjectionComplete);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "choiceContextComplete", out state.ChoiceContextComplete);
            TryReadReplicationWorldObjectDetailInt(delta.Detail, "nativeOptionCount", out state.NativeOptionCount);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "blocking", out state.Blocking);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "blockingObjective", out state.BlockingObjectiveButton);
            TryReadReplicationEventText(delta.Detail, "weatherTextB64", out state.WeatherTextKey);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "warningsComplete", out state.WarningProjectionComplete);
            if (TryReadReplicationEventText(delta.Detail, "warningsB64", out var warningsText)
                && !TryParseReplicationEventWarnings(warningsText, out state.Warnings))
            {
                detail = "event-state-warnings-malformed";
                return false;
            }
            TryReadReplicationEventText(delta.Detail, "titleB64", out state.Title);
            TryReadReplicationEventText(delta.Detail, "contentTitleB64", out state.ContentTitle);
            TryReadReplicationEventText(delta.Detail, "bodyB64", out state.Body);
            TryReadReplicationEventText(delta.Detail, "imageB64", out state.ImagePath);
            if (TryReadReplicationEventText(delta.Detail, "optionsB64", out var optionsText) && !string.IsNullOrEmpty(optionsText))
                state.Options = optionsText.Split(new[] { ReplicationEventOptionSeparator }, StringSplitOptions.None);
            if (TryReadReplicationEventText(delta.Detail, "optionTooltipsB64", out var tooltipsText) && !string.IsNullOrEmpty(tooltipsText))
                state.OptionTooltips = tooltipsText.Split(new[] { ReplicationEventOptionSeparator }, StringSplitOptions.None);
            if (TryReadReplicationWorldObjectDetailToken(delta.Detail, "optionDisabled", out var disabledText)
                && !string.Equals(disabledText, "_", StringComparison.Ordinal))
            {
                state.OptionDisabled = new bool[disabledText.Length];
                for (var i = 0; i < disabledText.Length; i++)
                {
                    if (disabledText[i] != '0' && disabledText[i] != '1')
                    {
                        detail = "event-state-option-disabled-malformed";
                        return false;
                    }
                    state.OptionDisabled[i] = disabledText[i] == '1';
                }
            }
            if (TryReadReplicationEventText(delta.Detail, "optionDisabledTooltipsB64", out var disabledTooltipsText)
                && !string.IsNullOrEmpty(disabledTooltipsText))
            {
                state.OptionDisabledTooltips = disabledTooltipsText.Split(
                    new[] { ReplicationEventOptionSeparator },
                    StringSplitOptions.None);
            }
            if (state.EventType.Length > 256
                || state.Family.Length > 64
                || state.Category.Length > 256
                || state.PhaseType.Length > 256
                || state.DialogId.Length > 256
                || state.WeatherTextKey.Length > 256
                || state.Title.Length > 256
                || state.ContentTitle.Length > 512
                || state.Body.Length > 4096
                || state.ImagePath.Length > 512
                || state.NativeOptionCount < 0
                || state.NativeOptionCount > 32
                || state.Options.Length > 8
                || state.OptionTooltips.Length > 8
                || state.OptionDisabled.Length > 8
                || state.OptionDisabledTooltips.Length > 8)
            {
                detail = "event-state-content-out-of-range";
                return false;
            }
            if (state.Options.Length != state.OptionTooltips.Length
                || state.Options.Length != state.OptionDisabled.Length
                || state.Options.Length != state.OptionDisabledTooltips.Length)
            {
                detail = "event-state-option-shape-mismatch";
                return false;
            }
            for (var i = 0; i < state.Options.Length; i++)
            {
                if (state.Options[i].Length > 256)
                {
                    detail = "event-state-option-out-of-range";
                    return false;
                }
            }
            for (var i = 0; i < state.OptionTooltips.Length; i++)
            {
                if (state.OptionTooltips[i].Length > 1024)
                {
                    detail = "event-state-tooltip-out-of-range";
                    return false;
                }
            }
            for (var i = 0; i < state.OptionDisabledTooltips.Length; i++)
            {
                if (state.OptionDisabledTooltips[i].Length > 1024)
                {
                    detail = "event-state-disabled-tooltip-out-of-range";
                    return false;
                }
            }
            return true;
        }

        private static bool TryParseReplicationEventWarnings(
            string payload,
            out ClientReplicationEventWarningState[] warnings)
        {
            warnings = Array.Empty<ClientReplicationEventWarningState>();
            if (string.IsNullOrEmpty(payload)) return true;
            var entries = payload.Split(new[] { ReplicationEventWarningEntrySeparator }, StringSplitOptions.None);
            if (entries.Length > 2) return false;
            var result = new ClientReplicationEventWarningState[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var fields = entries[i].Split(new[] { ReplicationEventWarningFieldSeparator }, StringSplitOptions.None);
                if (fields.Length != 8
                    || fields[0].Length > 128
                    || fields[1].Length > 1024
                    || fields[2].Length > 2048
                    || fields[3].Length > 512
                    || !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timer)
                    || timer < -1
                    || timer > 10000000
                    || (fields[5] != "0" && fields[5] != "1")
                    || fields[6].Length > 256)
                {
                    return false;
                }
                var extraLines = string.IsNullOrEmpty(fields[7])
                    ? Array.Empty<string>()
                    : fields[7].Split(new[] { '\n' }, StringSplitOptions.None);
                if (extraLines.Length > 8) return false;
                for (var line = 0; line < extraLines.Length; line++)
                    if (extraLines[line].Length > 512) return false;
                result[i] = new ClientReplicationEventWarningState
                {
                    Id = fields[0],
                    Text = fields[1],
                    Tooltip = fields[2],
                    Icon = fields[3],
                    Timer = timer,
                    ShouldShow = fields[5] == "1",
                    FactionName = fields[6],
                    ExtraLines = extraLines
                };
            }
            warnings = result;
            return true;
        }

        private static bool TryReadReplicationEventEnvelope(string payload, out string scope, out int epoch)
        {
            scope = string.Empty;
            epoch = -1;
            return TryReadReplicationWorldObjectDetailToken(payload, "scope", out scope)
                && !string.IsNullOrWhiteSpace(scope)
                && TryReadReplicationWorldObjectDetailInt(payload, "epoch", out epoch)
                && epoch >= 0;
        }

        private static bool TryReadReplicationEventLong(string payload, string name, out long value)
        {
            value = 0L;
            return TryReadReplicationWorldObjectDetailToken(payload, name, out var token)
                && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadReplicationEventText(string payload, string name, out string value)
        {
            value = string.Empty;
            if (!TryReadReplicationWorldObjectDetailToken(payload, name, out var token)) return false;
            if (string.Equals(token, "_", StringComparison.Ordinal)) return true;
            return TryDecodeReplicationDetailBase64(token, out value);
        }

        private static bool TryFindClientReplicationEventForNativePhase(object phase, out ClientReplicationEventState? state)
        {
            state = null;
            if (!TryGetReplicationEventInstanceFromPhase(phase, out var nativeEvent) || nativeEvent == null) return false;
            var blueprintId = ReadReplicationEventBlueprintId(nativeEvent);
            var dialogId = ReadReplicationStringMember(phase, "dialogId");
            var dialogIndex = ReadReplicationIntMember(phase, "dialogIndex", -1);
            lock (ReplicationEventLock)
            {
                foreach (var candidate in ReplicationClientEvents.Values)
                {
                    if (candidate.Active
                        && candidate.IsDialog
                        && string.Equals(candidate.BlueprintId, blueprintId, StringComparison.Ordinal)
                        && (string.IsNullOrEmpty(dialogId) || string.Equals(candidate.DialogId, dialogId, StringComparison.Ordinal))
                        && (dialogIndex < 0 || candidate.DialogIndex == dialogIndex))
                    {
                        state = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        private void SendReplicationEventChoice(ClientReplicationEventState state, int optionIndex, string source)
        {
            if (!EventChoiceLaneEnabled()
                || replicationConfigHostMode
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || state.DialogClosed
                || !CanSendReplicationEventChoice(state)
                || IsReplicationEventOptionDisabled(state, optionIndex)
                || optionIndex < 0
                || optionIndex > 31)
            {
                return;
            }

            var requestId = replicationEventClientSessionNonce + ":choice:" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var payload = LockstepCommandPayloads.CreateGameEventOptionChosenPayload(
                replicationEventClientEpoch,
                state.EventId,
                state.Revision,
                state.DialogId,
                state.DialogIndex,
                optionIndex,
                requestId);
            var command = new LockstepCommand(ReplicationClientPeerId, ++replicationIntentSequence, 0L, CommandKind.Custom, payload);
            replicationPendingEventChoice = new PendingReplicationEventChoice
            {
                Command = command,
                EventId = state.EventId,
                EventRevision = state.Revision,
                OptionIndex = optionIndex,
                Source = source,
                LastSentRealtime = -ReplicationEventChoiceRetrySeconds
            };
            SendPendingReplicationEventChoice();
        }

        private void RetryPendingReplicationEventChoiceIfDue()
        {
            var pending = replicationPendingEventChoice;
            if (pending == null || Time.realtimeSinceStartup - pending.LastSentRealtime < ReplicationEventChoiceRetrySeconds) return;
            if (pending.SendCount >= ReplicationEventChoiceMaxSends)
            {
                replicationLastEventSummary = "choice-timeout eventId=" + pending.EventId;
                replicationPendingEventChoice = null;
                return;
            }
            SendPendingReplicationEventChoice();
        }

        private void SendPendingReplicationEventChoice()
        {
            var pending = replicationPendingEventChoice;
            if (pending == null || replicationTransport == null || !replicationRemoteHelloReceived) return;
            SendReplicationLocalCommandIntent(pending.Command, "event-choice:" + pending.Source);
            pending.SendCount++;
            pending.LastSentRealtime = Time.realtimeSinceStartup;
            replicationEventChoicesSent++;
            replicationLastEventSummary = "choice-sent eventId=" + pending.EventId
                + " revision=" + pending.EventRevision.ToString(CultureInfo.InvariantCulture)
                + " option=" + pending.OptionIndex.ToString(CultureInfo.InvariantCulture)
                + " send=" + pending.SendCount.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryApplyReplicationEventChoice(
            long epoch,
            string eventId,
            long eventRevision,
            string dialogId,
            int dialogIndex,
            int optionIndex,
            string requestId,
            out string detail)
        {
            detail = string.Empty;
            if (!replicationConfigHostMode
                || !EventChoiceLaneEnabled()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived)
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-authority-unavailable";
                return false;
            }
            EnsureReplicationEventHostScope();
            if (epoch != replicationEventHostEpoch)
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-epoch-mismatch expected=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " actual=" + epoch.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            HostReplicationEventRecord? record;
            lock (ReplicationEventLock)
            {
                ReplicationHostEventsById.TryGetValue(eventId, out record);
            }
            if (record == null || record.TombstoneSent || record.Revision != eventRevision)
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-stale-or-missing eventId=" + eventId
                    + " expectedRevision=" + (record?.Revision ?? -1L).ToString(CultureInfo.InvariantCulture)
                    + " actualRevision=" + eventRevision.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!TryBuildHostReplicationEventState(record.NativeEvent, record, out var current, out _)
                || !current.IsDialog
                || current.DialogClosed
                || !CanSendReplicationEventChoice(current)
                || (!string.IsNullOrEmpty(dialogId) && !string.Equals(current.DialogId, dialogId, StringComparison.Ordinal))
                || (dialogIndex >= 0 && current.DialogIndex != dialogIndex))
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-dialog-mismatch eventId=" + eventId;
                return false;
            }
            var optionCount = current.Options.Length;
            if (phaseIsBranching(current.PhaseType) && optionCount > 4) optionCount = 4;
            if (optionCount <= 0 || optionIndex < 0 || optionIndex >= optionCount)
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-option-out-of-range eventId=" + eventId
                    + " option=" + optionIndex.ToString(CultureInfo.InvariantCulture)
                    + " count=" + optionCount.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (IsReplicationEventOptionDisabled(current, optionIndex))
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-option-disabled eventId=" + eventId
                    + " option=" + optionIndex.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!TryGetReplicationEventCurrentPhase(record.NativeEvent, out var phase) || phase == null)
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-phase-missing eventId=" + eventId;
                return false;
            }
            if (phaseIsBranching(phase.GetType().Name))
            {
                if (!TryReadInstanceMemberValue(phase, "choiceDestinationPhases", out var destinations)
                    || !(destinations is IList destinationList)
                    || optionIndex >= Math.Min(4, destinationList.Count))
                {
                    replicationEventChoicesRejected++;
                    detail = "event-choice-branch-option-out-of-range eventId=" + eventId;
                    return false;
                }
            }
            try
            {
                replicationEventApplicationDepth++;
                if (!TryCloseOwnedReplicationEventDialog(phase, optionIndex, out detail))
                {
                    replicationEventChoicesRejected++;
                    return false;
                }
                replicationEventChoicesApplied++;
                detail = "ok event-choice-applied eventId=" + eventId
                    + " revision=" + eventRevision.ToString(CultureInfo.InvariantCulture)
                    + " option=" + optionIndex.ToString(CultureInfo.InvariantCulture)
                    + " requestId=" + requestId;
                return true;
            }
            catch (Exception ex)
            {
                replicationEventChoicesRejected++;
                detail = "event-choice-invoke-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                replicationEventApplicationDepth = Math.Max(0, replicationEventApplicationDepth - 1);
            }
        }

        private static bool CanSendReplicationEventChoice(ClientReplicationEventState state)
        {
            return state.DialogProjectionComplete
                && state.ChoiceContextComplete
                && state.NativeOptionCount > 0
                && state.NativeOptionCount <= 8
                && (!phaseIsBranching(state.PhaseType) || state.NativeOptionCount <= 4)
                && state.Options.Length == state.NativeOptionCount;
        }

        private static bool IsReplicationEventOptionDisabled(ClientReplicationEventState state, int optionIndex)
        {
            return optionIndex >= 0
                && optionIndex < state.OptionDisabled.Length
                && state.OptionDisabled[optionIndex];
        }

        private static bool TryCloseOwnedReplicationEventDialog(object phase, int optionIndex, out string detail)
        {
            detail = string.Empty;
            var managerType = AccessTools.TypeByName("NSMedieval.Dialogs.DialogViewManager");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            if (manager == null
                || !TryReadInstanceMemberValue(manager, "OnClose", out var closeHandlerValue)
                || !(closeHandlerValue is Delegate closeHandler)
                || !TryReadInstanceMemberValue(manager, "view", out var view)
                || view == null)
            {
                detail = "event-choice-dialog-manager-not-ready";
                return false;
            }

            var handlers = closeHandler.GetInvocationList();
            if (handlers.Length != 1
                || !ReferenceEquals(handlers[0].Target, phase)
                || !string.Equals(handlers[0].Method.Name, "OnClose", StringComparison.Ordinal))
            {
                detail = "event-choice-dialog-owner-ambiguous handlers=" + handlers.Length.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            var isShowing = FindReplicationMethodOnTypeOrBase(view.GetType(), "IsShowing", Type.EmptyTypes);
            var close = AccessTools.Method(manager.GetType(), "Close", new[] { typeof(int) });
            if (isShowing == null || close == null
                || !(isShowing.Invoke(view, null) is bool showing) || !showing)
            {
                detail = "event-choice-dialog-view-not-showing";
                return false;
            }

            close.Invoke(manager, new object[] { optionIndex });
            detail = "event-choice-native-dialog-closed";
            return true;
        }

        private static void SendHostReplicationEventSpeedStateIfEnabled(int speedIndex, string source)
        {
            replicationHostEventSpeedIndex = speedIndex;
            var current = instance;
            if (current == null
                || !replicationConfigHostMode
                || !FullEventGraphAuthorityEnabled()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || speedIndex < 0
                || speedIndex > 8)
            {
                return;
            }
            if (replicationLastHostEventSpeedSentIndex == speedIndex) return;
            EnsureReplicationEventHostScope();
            current.SendReplicationEventDelta(
                GameEventSpeedStateDeltaKind,
                speedIndex,
                string.Empty,
                "scope=" + replicationEventHostSessionNonce
                    + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture)
                    + " speedIndex=" + speedIndex.ToString(CultureInfo.InvariantCulture)
                    + " source=" + FormatReplicationWorldObjectDetailToken(source));
            replicationLastHostEventSpeedSentIndex = speedIndex;
        }

        private static void SendCurrentHostReplicationEventSpeedState(string source)
        {
            if (gameSpeedManagerInstance == null) TryCaptureGameSpeedManagerInstance("event-speed-checkpoint");
            if (gameSpeedManagerInstance != null
                && TryReadInstanceMemberValue(gameSpeedManagerInstance, "CurrentSpeedIndex", out var speedValue)
                && speedValue != null)
            {
                try { replicationHostEventSpeedIndex = Convert.ToInt32(speedValue, CultureInfo.InvariantCulture); }
                catch { }
            }
            if (replicationHostEventSpeedIndex < 0 || replicationHostEventSpeedIndex > 8) return;
            replicationLastHostEventSpeedSentIndex = -1;
            SendHostReplicationEventSpeedStateIfEnabled(replicationHostEventSpeedIndex, source);
        }

        private static bool phaseIsBranching(string phaseType)
        {
            return phaseType.IndexOf("ShowDialogPhaseBranching", StringComparison.Ordinal) >= 0;
        }

        private static bool TryApplyReplicationEventSpeedState(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigEventSpeedReplication)
            {
                detail = "event-speed-disabled";
                return false;
            }
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "speedIndex", out var speedIndex)
                || speedIndex < 0
                || speedIndex > 8)
            {
                detail = "event-speed-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            lock (ReplicationEventLock)
            {
                if (delta.Sequence <= replicationLastEventSpeedAppliedSequence)
                {
                    detail = "ok event-speed-stale sequence=" + delta.Sequence.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }
            try
            {
                replicationEventApplicationDepth++;
                if (gameSpeedManagerInstance == null) TryCaptureGameSpeedManagerInstance("event-speed-apply");
                if (gameSpeedManagerInstance != null
                    && TryReadInstanceMemberValue(gameSpeedManagerInstance, "CurrentSpeedIndex", out var currentSpeedValue)
                    && currentSpeedValue != null
                    && Convert.ToInt32(currentSpeedValue, CultureInfo.InvariantCulture) == speedIndex)
                {
                    lock (ReplicationEventLock) replicationLastEventSpeedAppliedSequence = delta.Sequence;
                    detail = "ok event-speed-already-current index=" + speedIndex.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                bool applied;
                if (speedIndex <= 3)
                {
                    var methodName = speedIndex == 0 ? "SetSpeedPause"
                        : speedIndex == 1 ? "SetSpeedNormal"
                        : speedIndex == 2 ? "SetSpeedFast"
                        : "SetSpeedFaster";
                    applied = TryInvokeStoredGameSpeedManagerMethod(methodName, out detail);
                }
                else
                {
                    if (gameSpeedManagerInstance == null) TryCaptureGameSpeedManagerInstance("event-speed-apply");
                    var speedEnumType = AccessTools.TypeByName("NSMedieval.Manager.GameSpeedIndex");
                    var processSpeed = gameSpeedManagerInstance == null || speedEnumType == null
                        ? null
                        : AccessTools.Method(gameSpeedManagerInstance.GetType(), "ProcessSpeedChange", new[] { speedEnumType });
                    if (gameSpeedManagerInstance == null || speedEnumType == null || processSpeed == null)
                    {
                        detail = "event-speed-native-surface-missing";
                        applied = false;
                    }
                    else
                    {
                        processSpeed.Invoke(gameSpeedManagerInstance, new[] { Enum.ToObject(speedEnumType, speedIndex) });
                        detail = "event-speed-index-applied index=" + speedIndex.ToString(CultureInfo.InvariantCulture);
                        applied = true;
                    }
                }
                if (applied)
                {
                    lock (ReplicationEventLock) replicationLastEventSpeedAppliedSequence = delta.Sequence;
                }
                return applied;
            }
            finally
            {
                replicationEventApplicationDepth = Math.Max(0, replicationEventApplicationDepth - 1);
            }
        }

        private static bool TryApplyReplicationEventNotice(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!EventNoticeLaneEnabled())
            {
                detail = "event-notice-lane-disabled";
                return false;
            }
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryReadReplicationEventText(delta.Detail, "noticeIdB64", out var noticeId)
                || !TryReadReplicationEventText(delta.Detail, "textB64", out var message)
                || string.IsNullOrWhiteSpace(noticeId)
                || noticeId.Length > 128
                || string.IsNullOrWhiteSpace(message)
                || message.Length > 1024)
            {
                detail = "event-notice-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            lock (ReplicationEventLock)
            {
                if (ReplicationClientEventNoticeSeen.Contains(noticeId))
                {
                    detail = "ok event-notice-duplicate noticeId=" + noticeId;
                    return true;
                }
            }
            var enteredApplication = false;
            try
            {
                var controllerType = AccessTools.TypeByName("NSMedieval.BlackBarMessageController");
                var controller = controllerType == null ? null : ResolveReplicationUnityManagerInstance(controllerType);
                var show = controllerType == null
                    ? null
                    : AccessTools.Method(controllerType, "ShowBlackBarMessage", new[] { typeof(string) });
                if (controller == null || show == null)
                {
                    detail = "event-notice-controller-not-ready";
                    return false;
                }
                replicationEventApplicationDepth++;
                enteredApplication = true;
                show.Invoke(controller, new object[] { message });
                lock (ReplicationEventLock)
                {
                    if (ReplicationClientEventNoticeSeen.Add(noticeId))
                    {
                        ReplicationClientEventNoticeSeenOrder.Enqueue(noticeId);
                        while (ReplicationClientEventNoticeSeenOrder.Count > 256)
                            ReplicationClientEventNoticeSeen.Remove(ReplicationClientEventNoticeSeenOrder.Dequeue());
                    }
                }
                replicationEventNoticesApplied++;
                replicationLastEventSummary = "notice-applied noticeId=" + noticeId;
                detail = "ok event-notice-applied noticeId=" + noticeId;
                return true;
            }
            catch (Exception ex)
            {
                detail = "event-notice-apply-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                if (enteredApplication)
                    replicationEventApplicationDepth = Math.Max(0, replicationEventApplicationDepth - 1);
            }
        }

        private void UpdateReplicationEventWarningPresentation()
        {
            if (replicationConfigHostMode
                || !EventWarningLaneEnabled()
                || multiplayerLoadingInProgress
                || multiplayerMainMenuActive
                || !replicationRuntimeStarted
                || multiplayerCanvasRoot == null)
            {
                HideReplicationEventWarningPresentation();
                return;
            }

            var visible = new List<KeyValuePair<ClientReplicationEventState, ClientReplicationEventWarningState>>();
            lock (ReplicationEventLock)
            {
                foreach (var state in ReplicationClientEvents.Values)
                {
                    if (!state.Active) continue;
                    for (var i = 0; i < state.Warnings.Length; i++)
                    {
                        if (state.Warnings[i].ShouldShow)
                            visible.Add(new KeyValuePair<ClientReplicationEventState, ClientReplicationEventWarningState>(state, state.Warnings[i]));
                    }
                }
            }
            visible.Sort((left, right) => right.Key.ReceivedRealtime.CompareTo(left.Key.ReceivedRealtime));
            if (visible.Count > 2) visible.RemoveRange(2, visible.Count - 2);
            if (visible.Count == 0)
            {
                HideReplicationEventWarningPresentation();
                return;
            }

            var keyBuilder = new StringBuilder();
            for (var i = 0; i < visible.Count; i++)
            {
                keyBuilder.Append(visible[i].Key.EventId).Append('|')
                    .Append(visible[i].Key.Revision).Append('|')
                    .Append(visible[i].Value.Id).Append('|')
                    .Append(visible[i].Value.Timer).Append('|')
                    .Append(visible[i].Key.WarningProjectionComplete).Append(';');
            }
            var renderKey = keyBuilder.ToString();
            if (multiplayerEventWarningPanel == null || !string.Equals(multiplayerEventWarningKey, renderKey, StringComparison.Ordinal))
            {
                if (multiplayerEventWarningPanel != null) Destroy(multiplayerEventWarningPanel);
                multiplayerCanvasGameFont = FindMultiplayerGameFont();
                multiplayerEventWarningPanel = CreateMultiplayerCanvasImage(
                    multiplayerCanvasRoot.transform,
                    "Going Cooperative Event Warnings",
                    Color.clear);
                SetMultiplayerCanvasRect(
                    multiplayerEventWarningPanel.GetComponent<RectTransform>(),
                    new Vector2(0.02f, 0.7f),
                    new Vector2(0.42f, 0.96f),
                    Vector2.zero,
                    Vector2.zero);
                var cardHeight = 1f / visible.Count;
                for (var i = 0; i < visible.Count; i++)
                {
                    var pair = visible[i];
                    var top = 1f - i * cardHeight;
                    var card = CreateMultiplayerCanvasImage(
                        multiplayerEventWarningPanel.transform,
                        "Warning " + i.ToString(CultureInfo.InvariantCulture),
                        new Color(0.12f, 0.095f, 0.07f, 0.94f));
                    SetMultiplayerCanvasRect(
                        card.GetComponent<RectTransform>(),
                        new Vector2(0f, top - cardHeight + 0.025f),
                        new Vector2(1f, top),
                        new Vector2(0f, 0f),
                        new Vector2(0f, -4f));
                    var textMinX = 0.04f;
                    if (TryLoadReplicationEventSprite(pair.Value.Icon, out var iconSprite) && iconSprite != null)
                    {
                        var iconObject = CreateMultiplayerCanvasImage(card.transform, "Icon", Color.white);
                        var icon = iconObject.GetComponent<Image>();
                        icon.sprite = iconSprite;
                        icon.preserveAspect = true;
                        SetMultiplayerCanvasRect(iconObject.GetComponent<RectTransform>(), new Vector2(0.025f, 0.2f), new Vector2(0.17f, 0.8f), Vector2.zero, Vector2.zero);
                        textMinX = 0.19f;
                    }
                    var heading = string.IsNullOrWhiteSpace(pair.Value.FactionName)
                        ? pair.Value.Text
                        : pair.Value.FactionName + " — " + pair.Value.Text;
                    var title = CreateMultiplayerGameText(card.transform, "Title", heading, 14f, TextAlignmentOptions.MidlineLeft, MultiplayerCanvasText);
                    title.fontStyle = FontStyles.Bold;
                    title.enableWordWrapping = true;
                    SetMultiplayerCanvasRect(title.rectTransform, new Vector2(textMinX, 0.54f), new Vector2(0.96f, 0.94f), Vector2.zero, Vector2.zero);

                    var detailBuilder = new StringBuilder();
                    if (pair.Value.Timer >= 0) detailBuilder.Append(pair.Value.Timer.ToString(CultureInfo.InvariantCulture)).Append(" MIN");
                    if (!string.IsNullOrWhiteSpace(pair.Value.Tooltip))
                    {
                        if (detailBuilder.Length > 0) detailBuilder.Append(" — ");
                        detailBuilder.Append(pair.Value.Tooltip);
                    }
                    for (var line = 0; line < pair.Value.ExtraLines.Length; line++)
                    {
                        if (detailBuilder.Length > 0) detailBuilder.Append("\n");
                        detailBuilder.Append(pair.Value.ExtraLines[line]);
                    }
                    if (!pair.Key.WarningProjectionComplete)
                    {
                        if (detailBuilder.Length > 0) detailBuilder.Append("\n");
                        detailBuilder.Append("DETAILS INCOMPLETE — CHECK HOST");
                    }
                    var details = CreateMultiplayerGameText(card.transform, "Details", detailBuilder.ToString(), 11f, TextAlignmentOptions.TopLeft, MultiplayerCanvasMuted);
                    details.enableWordWrapping = true;
                    details.overflowMode = TextOverflowModes.Ellipsis;
                    SetMultiplayerCanvasRect(details.rectTransform, new Vector2(textMinX, 0.08f), new Vector2(0.96f, 0.56f), Vector2.zero, Vector2.zero);
                }
                multiplayerEventWarningKey = renderKey;
            }
            if (multiplayerEventWarningPanel != null) multiplayerEventWarningPanel.SetActive(true);
        }

        private void HideReplicationEventWarningPresentation()
        {
            if (multiplayerEventWarningPanel != null) multiplayerEventWarningPanel.SetActive(false);
        }

        private void UpdateReplicationEventPresentation()
        {
            if (replicationConfigHostMode
                || !EventDialogLaneEnabled()
                || multiplayerLoadingInProgress
                || multiplayerMainMenuActive
                || !replicationRuntimeStarted
                || multiplayerCanvasRoot == null)
            {
                HideReplicationEventPresentation();
                return;
            }

            ClientReplicationEventState? selected = null;
            lock (ReplicationEventLock)
            {
                foreach (var state in ReplicationClientEvents.Values)
                {
                    if (!state.Active || !state.IsDialog || state.DialogClosed) continue;
                    if (selected == null || state.ReceivedRealtime > selected.ReceivedRealtime) selected = state;
                }
            }
            if (selected == null)
            {
                HideReplicationEventPresentation();
                return;
            }

            var pending = replicationPendingEventChoice != null
                && string.Equals(replicationPendingEventChoice.EventId, selected.EventId, StringComparison.Ordinal);
            var renderKey = selected.EventId + "|" + selected.Revision.ToString(CultureInfo.InvariantCulture) + "|" + pending;
            if (multiplayerEventPresentationPanel == null || !string.Equals(multiplayerEventPresentationKey, renderKey, StringComparison.Ordinal))
            {
                RebuildReplicationEventPresentation(selected, pending);
                multiplayerEventPresentationKey = renderKey;
            }
            if (multiplayerEventPresentationPanel != null) multiplayerEventPresentationPanel.SetActive(true);
        }

        private void RebuildReplicationEventPresentation(ClientReplicationEventState state, bool pending)
        {
            if (multiplayerEventPresentationPanel != null) Destroy(multiplayerEventPresentationPanel);
            if (multiplayerCanvasRoot == null) return;
            multiplayerCanvasGameFont = FindMultiplayerGameFont();
            multiplayerEventPresentationPanel = CreateMultiplayerCanvasImage(
                multiplayerCanvasRoot.transform,
                "Going Cooperative Event Presentation",
                new Color(0.055f, 0.06f, 0.064f, 0.96f));
            SetMultiplayerCanvasRect(
                multiplayerEventPresentationPanel.GetComponent<RectTransform>(),
                new Vector2(0.6f, 0.16f),
                new Vector2(0.98f, 0.84f),
                Vector2.zero,
                Vector2.zero);

            var category = CreateMultiplayerGameText(
                multiplayerEventPresentationPanel.transform,
                "Category",
                !string.IsNullOrWhiteSpace(state.Category)
                    ? state.Category.ToUpperInvariant()
                    : string.IsNullOrWhiteSpace(state.Family) ? "SETTLEMENT EVENT" : state.Family.ToUpperInvariant(),
                12f,
                TextAlignmentOptions.MidlineLeft,
                MultiplayerCanvasMuted);
            SetMultiplayerCanvasRect(category.rectTransform, new Vector2(0.06f, 0.9f), new Vector2(0.94f, 0.97f), Vector2.zero, Vector2.zero);

            var title = CreateMultiplayerGameText(
                multiplayerEventPresentationPanel.transform,
                "Title",
                string.IsNullOrWhiteSpace(state.Title) ? state.BlueprintId : state.Title,
                23f,
                TextAlignmentOptions.MidlineLeft,
                MultiplayerCanvasText);
            title.fontStyle = FontStyles.Bold;
            SetMultiplayerCanvasRect(title.rectTransform, new Vector2(0.06f, 0.77f), new Vector2(0.94f, 0.91f), Vector2.zero, Vector2.zero);

            var contentTitle = CreateMultiplayerGameText(
                multiplayerEventPresentationPanel.transform,
                "Content Title",
                state.ContentTitle,
                16f,
                TextAlignmentOptions.MidlineLeft,
                MultiplayerCanvasMuted);
            contentTitle.fontStyle = FontStyles.Bold;
            SetMultiplayerCanvasRect(contentTitle.rectTransform, new Vector2(0.06f, 0.68f), new Vector2(0.94f, 0.78f), Vector2.zero, Vector2.zero);

            var bodyAnchorMinX = 0.06f;
            if (TryLoadReplicationEventSprite(state.ImagePath, out var eventSprite) && eventSprite != null)
            {
                var imageObject = CreateMultiplayerCanvasImage(
                    multiplayerEventPresentationPanel.transform,
                    "Event Image",
                    Color.white);
                var image = imageObject.GetComponent<Image>();
                image.sprite = eventSprite;
                image.preserveAspect = true;
                SetMultiplayerCanvasRect(
                    imageObject.GetComponent<RectTransform>(),
                    new Vector2(0.06f, 0.39f),
                    new Vector2(0.33f, 0.67f),
                    Vector2.zero,
                    Vector2.zero);
                bodyAnchorMinX = 0.36f;
            }

            var body = CreateMultiplayerGameText(
                multiplayerEventPresentationPanel.transform,
                "Body",
                string.IsNullOrWhiteSpace(state.Body) ? state.PhaseType : state.Body,
                14f,
                TextAlignmentOptions.TopLeft,
                MultiplayerCanvasText);
            body.enableWordWrapping = true;
            body.overflowMode = TextOverflowModes.Ellipsis;
            SetMultiplayerCanvasRect(body.rectTransform, new Vector2(bodyAnchorMinX, 0.39f), new Vector2(0.94f, 0.68f), Vector2.zero, Vector2.zero);

            if (!state.IsDialog || state.DialogClosed) return;
            var labels = state.Options;
            var count = Math.Min(phaseIsBranching(state.PhaseType) ? 4 : 8, labels.Length);
            var columns = count > 4 ? 2 : 1;
            var rows = columns == 1 ? count : (count + 1) / 2;
            var choiceContextAvailable = CanSendReplicationEventChoice(state);
            for (var i = 0; i < count; i++)
            {
                var option = i;
                var tooltip = i < state.OptionTooltips.Length ? state.OptionTooltips[i] : string.Empty;
                var optionDisabled = IsReplicationEventOptionDisabled(state, i);
                var disabledTooltip = i < state.OptionDisabledTooltips.Length
                    ? state.OptionDisabledTooltips[i]
                    : string.Empty;
                var label = pending
                    ? "WAITING FOR HOST..."
                    : labels[i] + (optionDisabled
                        ? "\n<size=10><color=#D6A66A>" + (string.IsNullOrWhiteSpace(disabledTooltip) ? "UNAVAILABLE" : disabledTooltip) + "</color></size>"
                        : !choiceContextAvailable
                        ? "\n<size=10><color=#D6A66A>SELECT ON HOST — CONTEXT INCOMPLETE</color></size>"
                        : string.IsNullOrWhiteSpace(tooltip)
                            ? string.Empty
                            : "\n<size=10><color=#AEB7BE>" + tooltip + "</color></size>");
                var button = CreateMultiplayerGameButton(
                    multiplayerEventPresentationPanel.transform,
                    "Option " + i.ToString(CultureInfo.InvariantCulture),
                    label,
                    () => SendReplicationEventChoice(state, option, "event-ui"),
                    pending ? MultiplayerCanvasCard : MultiplayerCanvasAccent);
                button.interactable = !pending
                    && EventChoiceLaneEnabled()
                    && choiceContextAvailable
                    && !optionDisabled;
                var text = button.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null)
                {
                    text.enableWordWrapping = true;
                    text.overflowMode = TextOverflowModes.Ellipsis;
                }
                var row = columns == 1 ? i : i / columns;
                var column = columns == 1 ? 0 : i % columns;
                var height = 0.32f / Math.Max(1, rows);
                var top = 0.36f - row * height;
                var left = columns == 1 ? 0.06f : column == 0 ? 0.06f : 0.51f;
                var right = columns == 1 ? 0.94f : column == 0 ? 0.49f : 0.94f;
                SetMultiplayerCanvasRect(
                    button.GetComponent<RectTransform>(),
                    new Vector2(left, top - height + 0.008f),
                    new Vector2(right, top),
                    Vector2.zero,
                    Vector2.zero);
            }
        }

        private static bool TryLoadReplicationEventSprite(string imagePath, out Sprite? sprite)
        {
            sprite = null;
            if (string.IsNullOrWhiteSpace(imagePath)) return false;
            try
            {
                var assetUtilsType = AccessTools.TypeByName("NSMedieval.UI.Utils.AssetUtils");
                var getSprite = assetUtilsType == null
                    ? null
                    : AccessTools.Method(assetUtilsType, "GetSprite", new[] { typeof(string) });
                sprite = getSprite?.Invoke(null, new object[] { imagePath }) as Sprite;
                return sprite != null;
            }
            catch
            {
                return false;
            }
        }

        private void HideReplicationEventPresentation()
        {
            if (multiplayerEventPresentationPanel != null) multiplayerEventPresentationPanel.SetActive(false);
        }

        private static void ResetReplicationEventRuntimeState(ReplicationTraderPartyResetContext traderPartyResetContext)
        {
            lock (ReplicationEventLock)
            {
                RetireReplicationEventScope(replicationEventClientSessionNonce);
                ReplicationHostEvents.Clear();
                ReplicationHostEventsById.Clear();
                ReplicationClientEvents.Clear();
                ReplicationClientEventRevisionHighWater.Clear();
                ReplicationClientEventCheckpoints.Reset();
                ReplicationClientEventNoticeSeen.Clear();
                ReplicationClientEventNoticeSeenOrder.Clear();
                replicationPendingEventChoice = null;
                ReplicationEventNoticeCaptureContext.Clear();
            }
            ResetReplicationTraderParties(traderPartyResetContext);
            replicationEventHostSessionNonce = string.Empty;
            replicationEventClientSessionNonce = string.Empty;
            replicationEventHostEpoch = -1;
            replicationEventClientEpoch = -1;
            replicationEventHostCounter = 0L;
            replicationEventHostNoticeCounter = 0L;
            replicationEventCheckpointSequence = 0L;
            replicationLastEventSpeedAppliedSequence = -1L;
            replicationHostEventSpeedIndex = -1;
            replicationLastHostEventSpeedSentIndex = -1;
            replicationNextEventPollRealtime = 0f;
            replicationNextEventCheckpointRealtime = 0f;
            replicationNextClientEventQuarantineRealtime = 0f;
            replicationEventApplicationDepth = 0;
            replicationClientEventAuthorityParked = false;
            replicationEventsSent = 0L;
            replicationEventsApplied = 0L;
            replicationEventsSuppressed = 0L;
            replicationEventChoicesSent = 0L;
            replicationEventChoicesApplied = 0L;
            replicationEventChoicesRejected = 0L;
            replicationEventNoticesSent = 0L;
            replicationEventNoticesApplied = 0L;
            replicationLastEventSummary = string.Empty;
            PurgeReplicationEventWorldObjectDeltas();
            ResetReplicationWeatherRuntimeState();
            var current = instance;
            if (current != null)
            {
                if (current.multiplayerEventPresentationPanel != null) UnityEngine.Object.Destroy(current.multiplayerEventPresentationPanel);
                if (current.multiplayerEventWarningPanel != null) UnityEngine.Object.Destroy(current.multiplayerEventWarningPanel);
                current.multiplayerEventPresentationPanel = null;
                current.multiplayerEventPresentationKey = string.Empty;
                current.multiplayerEventWarningPanel = null;
                current.multiplayerEventWarningKey = string.Empty;
            }
        }

        private static string FormatReplicationEventStatus()
        {
            return "eventsSent=" + replicationEventsSent.ToString(CultureInfo.InvariantCulture)
                + " eventsApplied=" + replicationEventsApplied.ToString(CultureInfo.InvariantCulture)
                + " eventsSuppressed=" + replicationEventsSuppressed.ToString(CultureInfo.InvariantCulture)
                + " eventChoicesSent=" + replicationEventChoicesSent.ToString(CultureInfo.InvariantCulture)
                + " eventChoicesApplied=" + replicationEventChoicesApplied.ToString(CultureInfo.InvariantCulture)
                + " eventChoicesRejected=" + replicationEventChoicesRejected.ToString(CultureInfo.InvariantCulture)
                + " eventNoticesSent=" + replicationEventNoticesSent.ToString(CultureInfo.InvariantCulture)
                + " eventNoticesApplied=" + replicationEventNoticesApplied.ToString(CultureInfo.InvariantCulture)
                + " lastEvent=" + (string.IsNullOrEmpty(replicationLastEventSummary) ? "<none>" : replicationLastEventSummary);
        }

        private sealed class HostReplicationEventRecord
        {
            public object NativeEvent = null!;
            public string EventId = string.Empty;
            public string BlueprintId = string.Empty;
            public string Source = string.Empty;
            public long Revision;
            public string Signature = string.Empty;
            public string LastSentSignature = string.Empty;
            public bool TombstoneSent;
            public ClientReplicationEventState? LastState;
        }

        private sealed class PreparedHostReplicationEvent
        {
            public PreparedHostReplicationEvent(
                HostReplicationEventRecord record,
                ClientReplicationEventState state,
                string signature)
            {
                Record = record;
                State = state;
                Signature = signature;
            }

            public HostReplicationEventRecord Record { get; }
            public ClientReplicationEventState State { get; }
            public string Signature { get; }
        }

        private sealed class ClientReplicationEventState
        {
            public bool Active;
            public string EventId = string.Empty;
            public string BlueprintId = string.Empty;
            public long Revision;
            public string EventType = string.Empty;
            public string Family = string.Empty;
            public string Category = string.Empty;
            public string PhaseType = string.Empty;
            public bool IsDialog;
            public string DialogId = string.Empty;
            public int DialogIndex = -1;
            public bool DialogClosed;
            public bool ShowCloseButton;
            public bool DialogProjectionComplete;
            public bool ChoiceContextComplete;
            public int NativeOptionCount;
            public bool Blocking;
            public bool BlockingObjectiveButton;
            public string WeatherTextKey = string.Empty;
            public bool WarningProjectionComplete;
            public ClientReplicationEventWarningState[] Warnings = Array.Empty<ClientReplicationEventWarningState>();
            public string Title = string.Empty;
            public string ContentTitle = string.Empty;
            public string Body = string.Empty;
            public string ImagePath = string.Empty;
            public string[] Options = Array.Empty<string>();
            public string[] OptionTooltips = Array.Empty<string>();
            public bool[] OptionDisabled = Array.Empty<bool>();
            public string[] OptionDisabledTooltips = Array.Empty<string>();
            public float ReceivedRealtime;
            public long LastHostDeltaSequence;
        }

        private sealed class ClientReplicationEventWarningState
        {
            public string Id = string.Empty;
            public string Text = string.Empty;
            public string Tooltip = string.Empty;
            public string Icon = string.Empty;
            public int Timer;
            public bool ShouldShow;
            public string FactionName = string.Empty;
            public string[] ExtraLines = Array.Empty<string>();
        }

        private sealed class PendingReplicationEventChoice
        {
            public LockstepCommand Command = null!;
            public string EventId = string.Empty;
            public long EventRevision;
            public int OptionIndex;
            public string Source = string.Empty;
            public int SendCount;
            public float LastSentRealtime;
        }

        private sealed class ReferenceObjectComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceObjectComparer Instance = new ReferenceObjectComparer();
            public new bool Equals(object? x, object? y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}

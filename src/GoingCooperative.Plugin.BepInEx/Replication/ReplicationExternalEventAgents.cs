using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using NSMedieval;
using NSMedieval.GameEventSystem;
using NSMedieval.Manager;
using NSMedieval.Serialization;
using NSMedieval.State;
using NSMedieval.UI;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private enum ReplicationTraderPartyResetContext
        {
            WorldReloadPending,
            StopInPlace,
            ScopeChangedSameWorld
        }

        private enum ClientTraderPartySemanticDisposition
        {
            Apply,
            AckStale,
            AckDuplicate,
            Conflict
        }

        internal const string ReplicationTraderPartyBeginDeltaKind = "TraderPartyBegin";
        internal const string ReplicationTraderPartyChunkDeltaKind = "TraderPartyChunk";
        internal const string ReplicationTraderPartyCommitDeltaKind = "TraderPartyCommit";
        internal const string ReplicationTraderPartyAgentStateDeltaKind = "TraderPartyAgentState";
        internal const string ReplicationTraderPartyTombstoneDeltaKind = "TraderPartyAgentTombstone";
        internal const string ReplicationTraderPartyMemberAdoptDeltaKind = "TraderPartyMemberAdopt";
        internal const string ReplicationTraderPartyAbortDeltaKind = "TraderPartyAbort";

        private const string ReplicationTraderPartyWireVersion = "trader-party-v3";
        private const string ReplicationTraderPartyWriterId = "going-cooperative-trader-party-v3";
        private const int ReplicationTraderPartyBundleMagic = 0x31505447; // GTP1
        private const int ReplicationTraderPartyBundleVersion = 3;
        private const int ReplicationTraderPartyRawChunkBytes = 192;
        private const int ReplicationTraderPartyMaxBundleBytes = 96 * 1024;
        private const int ReplicationTraderPartyMaxUncompressedBundleBytes = 1024 * 1024;
        private const int ReplicationTraderPartyMaxChunks = 512;
        private const int ReplicationTraderPartyMaxHumanoids = 128;
        private const int ReplicationTraderPartyMaxAnimals = 256;
        private const int ReplicationTraderPartyMaxStringChars = 512;
        private const int ReplicationTraderPartyMaxMemberManifestBytes = 256 * 1024;
        private const int ReplicationTraderPartyMaxCompressedMemberManifestBytes = 24 * 1024;
        private const int ReplicationTraderPartySendWindow = 16;
        private const int ReplicationTraderPartyMaxClientTransfers = 2;
        private const int ReplicationTraderPartyMaxAbortNativeCandidates = 8;
        private const int ReplicationTraderPartyMaxTombstones = 2048;
        private const int ReplicationTraderPartyMaxSemanticHighWaters = 4096;
        private const int ReplicationTraderPartyMaxTerminalEvents = 512;
        internal const int ReplicationTraderPartyMaxSends = 30;
        internal const float ReplicationTraderPartyRetrySeconds = 1f;
        private const float ReplicationTraderPartyTransferTimeoutSeconds = 40f;
        private const float ReplicationTraderPartyCheckpointSeconds = 30f;

        private static readonly object ReplicationTraderPartyLock = new object();
        private static readonly Dictionary<object, List<CreatureBase>> ReplicationTraderInitCreaturesByEvent =
            new Dictionary<object, List<CreatureBase>>(ExternalReferenceComparer.Instance);
        private static readonly Dictionary<string, HostTraderPartyRecord> ReplicationHostTraderParties =
            new Dictionary<string, HostTraderPartyRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<string, HostTraderPartyTransfer> ReplicationHostTraderPartyTransfers =
            new Dictionary<string, HostTraderPartyTransfer>(StringComparer.Ordinal);
        private static readonly Dictionary<long, HostTraderPartySequenceBinding> ReplicationHostTraderPartySequenceBindings =
            new Dictionary<long, HostTraderPartySequenceBinding>();
        private static readonly Dictionary<string, ClientTraderPartyTransfer> ReplicationClientTraderPartyTransfers =
            new Dictionary<string, ClientTraderPartyTransfer>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ReplicationClientTraderPartyGenerationByEvent =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationClientTraderPartyTombstones =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> ReplicationClientTraderPartyTombstoneOrder = new Queue<string>();
        private static readonly Dictionary<string, ClientTraderPartySemanticHighWater> ReplicationClientTraderPartySemanticHighWaters =
            new Dictionary<string, ClientTraderPartySemanticHighWater>(StringComparer.Ordinal);
        private static readonly Queue<string> ReplicationClientTraderPartySemanticHighWaterOrder = new Queue<string>();
        private static readonly Dictionary<string, string> ReplicationClientTraderPartyHashByEvent =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationClientTraderPartyFailureLatches =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<object, TraderPartyQuarantineMarker> ReplicationAdoptedTraderEvents =
            new ConditionalWeakTable<object, TraderPartyQuarantineMarker>();
        private static readonly Dictionary<string, WeakReference> ReplicationAdoptedTraderEventById =
            new Dictionary<string, WeakReference>(StringComparer.Ordinal);
        private static readonly HashSet<object> ReplicationAbortedHostTraderEvents =
            new HashSet<object>(ExternalReferenceComparer.Instance);
        private static readonly List<PendingHostTraderPartyAbort> ReplicationPendingHostTraderPartyAborts =
            new List<PendingHostTraderPartyAbort>();
        private static readonly Dictionary<object, HostTraderPartyPrepareFailure> ReplicationHostTraderPartyPrepareFailures =
            new Dictionary<object, HostTraderPartyPrepareFailure>(ExternalReferenceComparer.Instance);
        private static readonly Dictionary<string, WeakReference> ReplicationDetachedTraderPartyAdoptions =
            new Dictionary<string, WeakReference>(StringComparer.Ordinal);
        private static readonly TraderPartyTransferTracker ReplicationClientTraderPartyTracker =
            new TraderPartyTransferTracker(new TraderPartyTransferLimits(
                ReplicationTraderPartyMaxClientTransfers,
                ReplicationTraderPartyMaxTerminalEvents,
                ReplicationTraderPartyMaxChunks,
                ReplicationTraderPartyRawChunkBytes,
                ReplicationTraderPartyMaxChunks,
                ReplicationTraderPartyMaxBundleBytes,
                ReplicationTraderPartyMaxTombstones,
                ReplicationTraderPartyMaxTombstones,
                (long)(ReplicationTraderPartyTransferTimeoutSeconds * 1000f)));
        private static readonly HashSet<string> ReplicationClientEndedTraderParties = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationClientTerminalTraderParties = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> ReplicationClientTerminalTraderPartyOrder = new Queue<string>();
        private static readonly Dictionary<object, TraderPartyAgentBinding> ReplicationTraderPartyBindingByObject =
            new Dictionary<object, TraderPartyAgentBinding>(ExternalReferenceComparer.Instance);
        private static readonly Dictionary<string, object> ReplicationTraderPartyObjectByNetworkId =
            new Dictionary<string, object>(StringComparer.Ordinal);

        private static bool replicationTraderPartyHookInstallAttempted;
        private static bool replicationTraderPartyHooksReady;
        private static bool replicationTraderPartyNativeSurfacesValidated;
        private static bool replicationTraderPartyNativeSurfacesReady;
        private static int replicationTraderPartyApplicationDepth;
        private static long replicationTraderPartyTransferCounter;
        private static float replicationNextTraderPartyCheckpointRealtime;
        private static bool replicationTraderPartyObservedRemoteReady;
        private static bool replicationTraderPartyRecoveryRequested;
        private static bool replicationTraderPartyRecoveryAttempted;
        private static string replicationTraderPartyRecoveryReason = string.Empty;

        private void TryInstallReplicationExternalEventAgentHooks(Harmony harmonyInstance)
        {
            replicationTraderPartyHookInstallAttempted = true;
            var initPostfix = ExternalHarmony(nameof(ReplicationTraderInitPostfix));
            var npcRemovedPostfix = ExternalHarmony(nameof(ReplicationTraderNpcRemovedPostfix));
            var animalRemovedPostfix = ExternalHarmony(nameof(ReplicationTraderAnimalRemovedPostfix));
            var humanoidStatePostfix = ExternalHarmony(nameof(ReplicationTraderHumanoidStatePostfix));
            var animalStatePostfix = ExternalHarmony(nameof(ReplicationTraderAnimalStatePostfix));
            var prisonerOwnerPostfix = ExternalHarmony(nameof(ReplicationTraderPrisonerOwnerPostfix));
            var tradeOpenPrefix = ExternalHarmony(nameof(ReplicationTraderOpenTradingMenuPrefix));
            var tradeApplyPrefix = ExternalHarmony(nameof(ReplicationTraderApplyTradePrefix));
            var tradeApplyPostfix = ExternalHarmony(nameof(ReplicationTraderApplyTradePostfix));
            var tradeTalkPrefix = ExternalHarmony(nameof(ReplicationTraderTalkPrefix));
            var clientLifecyclePrefix = ExternalHarmony(nameof(ReplicationTraderClientLifecyclePrefix));
            var count = 0;

            count += PatchExternalMethod(
                harmonyInstance,
                "NSMedieval.UI.TradingManager",
                "InitTrader",
                new[]
                {
                    typeof(HumanoidInstance),
                    AccessTools.TypeByName("NSMedieval.UI.TraderType"),
                    typeof(List<CreatureBase>).MakeByRefType(),
                    typeof(GameEventInstance)
                },
                null,
                initPostfix);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.NPCController", "RemoveNPC", new[] { typeof(HumanoidInstance) }, null, npcRemovedPostfix);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.Manager.AnimalManager", "RemoveAnimal", new[] { typeof(AnimalInstance), typeof(bool) }, null, animalRemovedPostfix);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.State.HumanoidInstance", "RetreatFromMap", Type.EmptyTypes, null, humanoidStatePostfix);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.State.AnimalInstance", "AssignPetOwner", new[] { typeof(CreatureBase) }, null, animalStatePostfix);
            count += PatchExternalMethod(
                harmonyInstance,
                "NSMedieval.State.AnimalInstance",
                "SetAnimalType",
                new[] { AccessTools.TypeByName("NSMedieval.Types.AnimalType") },
                null,
                animalStatePostfix);

            var captiveType = AccessTools.TypeByName("NSMedieval.State.CaptiveNpcBehaviour");
            var ownerSetter = captiveType == null ? null : AccessTools.PropertySetter(captiveType, "Owner");
            if (ownerSetter != null)
            {
                harmonyInstance.Patch(ownerSetter, postfix: prisonerOwnerPostfix);
                count++;
            }

            count += PatchExternalMethod(
                harmonyInstance,
                "NSMedieval.UI.TradingManager",
                "OpenTradingMenu",
                new[]
                {
                    AccessTools.TypeByName("NSMedieval.UI.ITrader"),
                    AccessTools.TypeByName("NSMedieval.UI.ITrader")
                },
                tradeOpenPrefix,
                null);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.UI.TradingManager", "ApplyTrade", new[] { typeof(float) }, tradeApplyPrefix, tradeApplyPostfix);
            count += PatchExternalMethod(
                harmonyInstance,
                "NSMedieval.State.TraderBehaviour",
                "OnSettlerTalkTo",
                new[] { AccessTools.TypeByName("NSMedieval.State.WorkerBehaviour") },
                tradeTalkPrefix,
                null);

            count += PatchExternalMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventInstance", "Tick", new[] { typeof(float) }, clientLifecyclePrefix, null);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventInstance", "ForceEnd", Type.EmptyTypes, clientLifecyclePrefix, null);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.GameEventSystem.GameEventInstance", "OnEnd", Type.EmptyTypes, clientLifecyclePrefix, null);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.GameEventSystem.Events.TraderEvent", "OnLoaded", new[] { typeof(bool) }, clientLifecyclePrefix, null);
            count += PatchExternalMethod(harmonyInstance, "NSMedieval.GameEventSystem.Events.MultiTraderEvent", "OnLoaded", new[] { typeof(bool) }, clientLifecyclePrefix, null);

            replicationTraderPartyHooksReady = count == 15 && ValidateReplicationTraderPartyNativeSurfaces();
            LogReplicationInfo("Going Cooperative trader party hooks installed count="
                + count.ToString(CultureInfo.InvariantCulture)
                + " ready="
                + (replicationTraderPartyHooksReady ? "yes" : "no"));
            if (!replicationTraderPartyHooksReady)
            {
                LogReplicationWarning("Going Cooperative trader authority failed closed: expected 15 exact party hooks and complete native surfaces.");
            }
        }

        private static HarmonyMethod ExternalHarmony(string name)
        {
            return new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static int PatchExternalMethod(
            Harmony harmonyInstance,
            string typeName,
            string methodName,
            Type?[] parameterTypes,
            HarmonyMethod? prefix,
            HarmonyMethod? postfix)
        {
            if (parameterTypes.Any(type => type == null)) return 0;
            var type = AccessTools.TypeByName(typeName);
            var method = type == null ? null : AccessTools.Method(type, methodName, parameterTypes!);
            if (method == null) return 0;
            harmonyInstance.Patch(method, prefix, postfix);
            return 1;
        }

        // Called during config load before Harmony installation as well as at runtime.
        // Native surface validation is safe before hook installation; after installation
        // an incomplete patch set closes the authority gate.
        internal static bool ReplicationTraderPartySurfacesReady()
        {
            var nativeReady = ValidateReplicationTraderPartyNativeSurfaces();
            return nativeReady && (!replicationTraderPartyHookInstallAttempted || replicationTraderPartyHooksReady);
        }

        private static bool ValidateReplicationTraderPartyNativeSurfaces()
        {
            if (replicationTraderPartyNativeSurfacesValidated) return replicationTraderPartyNativeSurfacesReady;
            replicationTraderPartyNativeSurfacesValidated = true;
            try
            {
                var serializer = AccessTools.TypeByName("NSMedieval.Serialization.FVSerializer");
                var deserializer = AccessTools.TypeByName("NSMedieval.Serialization.FVDeserializer");
                var traderEvent = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.TraderEvent");
                var multiTraderEvent = AccessTools.TypeByName("NSMedieval.GameEventSystem.Events.MultiTraderEvent");
                var gameEventSystem = AccessTools.TypeByName("NSMedieval.GameEventSystem.GameEventSystem");
                var creature = AccessTools.TypeByName("NSMedieval.State.CreatureBase");
                var humanoid = AccessTools.TypeByName("NSMedieval.State.HumanoidInstance");
                var animal = AccessTools.TypeByName("NSMedieval.State.AnimalInstance");
                var captive = AccessTools.TypeByName("NSMedieval.State.CaptiveNpcBehaviour");
                var npcManager = AccessTools.TypeByName("NSMedieval.Manager.NPCManager");
                var animalManager = AccessTools.TypeByName("NSMedieval.Manager.AnimalManager");
                var npcController = AccessTools.TypeByName("NSMedieval.NPCController");
                var villageSave = AccessTools.TypeByName("NSMedieval.State.VillageSaveData");
                var trading = AccessTools.TypeByName("NSMedieval.UI.TradingManager");
                var traderBehaviour = AccessTools.TypeByName("NSMedieval.State.TraderBehaviour");
                var workerBehaviour = AccessTools.TypeByName("NSMedieval.State.WorkerBehaviour");
                var writeCreatureList = serializer?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(method => method.Name == "Write"
                        && method.IsGenericMethodDefinition
                        && method.GetGenericArguments().Length == 1
                        && method.ReturnType == typeof(void)
                        && method.GetParameters().Length == 2
                        && method.GetParameters()[0].ParameterType == typeof(string)
                        && method.GetParameters()[1].ParameterType.IsGenericType
                        && method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(IList<>)) == true;
                var readCreatureList = deserializer?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(method => method.Name == "ReadObjectList"
                        && method.IsGenericMethodDefinition
                        && method.GetGenericArguments().Length == 1
                        && method.GetParameters().Length == 2
                        && method.GetParameters()[0].ParameterType == typeof(string)
                        && method.GetParameters()[1].ParameterType.IsGenericType
                        && method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(List<>)
                        && method.ReturnType.IsGenericType
                        && method.ReturnType.GetGenericTypeDefinition() == typeof(List<>)) == true;
                replicationTraderPartyNativeSurfacesReady = serializer != null
                    && deserializer != null
                    && traderEvent != null
                    && multiTraderEvent != null
                    && gameEventSystem != null
                    && creature != null
                    && humanoid != null
                    && animal != null
                    && captive != null
                    && npcManager != null
                    && animalManager != null
                    && npcController != null
                    && villageSave != null
                    && trading != null
                    && traderBehaviour != null
                    && workerBehaviour != null
                    && writeCreatureList
                    && readCreatureList
                    && AccessTools.Constructor(serializer, new[] { typeof(string), typeof(string[]) }) != null
                    && AccessTools.Method(serializer, "WriteReferences") != null
                    && AccessTools.Method(serializer, "GetBytes", new[] { typeof(string) }) != null
                    && AccessTools.Method(serializer, "GetReferenceBytes") != null
                    && AccessTools.Constructor(deserializer, new[] { typeof(string), typeof(byte[]) }) != null
                    && AccessTools.Method(deserializer, "ReadReferences", new[] { typeof(byte[]) }) != null
                    && AccessTools.Property(traderEvent, "Trader") != null
                    && AccessTools.Property(traderEvent, "Guards") != null
                    && AccessTools.Method(traderEvent, "Unsubscribe") != null
                    && AccessTools.Method(multiTraderEvent, "Unsubscribe") != null
                    && AccessTools.Method(traderEvent, "RemoveWarningMessage") != null
                    && AccessTools.Method(multiTraderEvent, "RemoveWarningMessage") != null
                    && AccessTools.Property(gameEventSystem, "RunningEvents") != null
                    && AccessTools.Method(gameEventSystem, "RemoveFromRunningEvents") != null
                    && AccessTools.Field(creature, "uniqueId") != null
                    && AccessTools.Property(creature, "UniqueId") != null
                    && AccessTools.Property(creature, "PetsIDs") != null
                    && AccessTools.Property(creature, "Pets") != null
                    && AccessTools.Method(creature, "AssignPet", new[] { animal }) != null
                    && AccessTools.Method(creature, "DestroyStorage") != null
                    && AccessTools.Method(creature, "DestroyEquipment") != null
                    && AccessTools.Property(humanoid, "PrisonerBehaviour") != null
                    && AccessTools.Method(animal, "AssignPetOwner", new[] { creature }) != null
                    && AccessTools.Method(animal, "RopeTo") != null
                    && AccessTools.Property(captive, "Owner") != null
                    && AccessTools.Property(captive, "Humanoid") != null
                    && AccessTools.Method(npcManager, "LoadSavedNPC", new[] { humanoid }) != null
                    && AccessTools.Method(npcManager, "GetView", new[] { humanoid }) != null
                    && AccessTools.Method(animalManager, "InstantiateAnimal", new[] { animal, typeof(bool) }) != null
                    && AccessTools.Method(animalManager, "GetView", new[] { animal }) != null
                    && AccessTools.Method(animalManager, "RemoveAnimal", new[] { animal, typeof(bool) }) != null
                    && AccessTools.Method(npcController, "RemoveNPC", new[] { humanoid }) != null
                    && AccessTools.Method(villageSave, "AddAnimal", new[] { animal }) != null
                    && AccessTools.Method(villageSave, "AddNPC", new[] { humanoid }) != null
                    && AccessTools.Method(trading, "OpenTradingMenu") != null
                    && AccessTools.Method(trading, "ApplyTrade", new[] { typeof(float) }) != null
                    && AccessTools.Method(traderBehaviour, "OnSettlerTalkTo", new[] { workerBehaviour }) != null;
            }
            catch
            {
                replicationTraderPartyNativeSurfacesReady = false;
            }

            return replicationTraderPartyNativeSurfacesReady;
        }

        private static void ReplicationTraderInitPostfix(
            HumanoidInstance humanoidInstance,
            ref List<CreatureBase> addedCreatures,
            GameEventInstance fromEvent)
        {
            if (!replicationConfigHostMode || fromEvent == null || humanoidInstance == null) return;
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationTraderInitCreaturesByEvent.TryGetValue(fromEvent, out var cached))
                {
                    cached = new List<CreatureBase>();
                    ReplicationTraderInitCreaturesByEvent[fromEvent] = cached;
                }

                if (!cached.Contains(humanoidInstance)) cached.Add(humanoidInstance);
                if (addedCreatures == null) return;
                for (var i = 0; i < addedCreatures.Count; i++)
                {
                    var creature = addedCreatures[i];
                    if (creature != null && !cached.Contains(creature)) cached.Add(creature);
                }
            }
        }

        private static bool ReplicationTraderClientLifecyclePrefix(object __instance)
        {
            if (replicationConfigHostMode
                || replicationTraderPartyApplicationDepth > 0
                || !replicationConfigEventTraderAuthority
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !IsReplicationTraderEventInstance(__instance))
            {
                return true;
            }

            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationAdoptedTraderEvents.TryGetValue(__instance, out _)) return true;
                replicationEventsSuppressed++;
                return false;
            }
        }

        private static void ReplicationTraderNpcRemovedPostfix(HumanoidInstance instance)
        {
            if (!replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || instance == null) return;
            SendHostReplicationTraderPartyTombstone(instance, "npc-removed");
        }

        private static void ReplicationTraderAnimalRemovedPostfix(AnimalInstance animal, bool dropResources)
        {
            if (!replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || animal == null) return;
            SendHostReplicationTraderPartyTombstone(animal, dropResources ? "animal-removed-carcass" : "animal-removed");
        }

        private static void ReplicationTraderHumanoidStatePostfix(HumanoidInstance __instance)
        {
            if (!replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || __instance == null) return;
            SendHostReplicationTraderPartyAgentState(__instance, "retreat");
        }

        private static void ReplicationTraderAnimalStatePostfix(AnimalInstance __instance)
        {
            if (!replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || __instance == null) return;
            SendHostReplicationTraderPartyAgentState(__instance, "animal-owner-or-type");
        }

        private static void ReplicationTraderPrisonerOwnerPostfix(object __instance)
        {
            if (!replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || __instance == null) return;
            var humanoid = AccessTools.Property(__instance.GetType(), "Humanoid")?.GetValue(__instance, null) as HumanoidInstance;
            if (humanoid != null) SendHostReplicationTraderPartyAgentState(humanoid, "prisoner-owner");
        }

        private static bool ReplicationTraderOpenTradingMenuPrefix(object otherTrader)
        {
            if (replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0) return true;
            var owner = TryGetTraderOwner(otherTrader);
            if (!ShouldBlockClientTrader(owner)) return true;
            ShowReplicationTraderReadOnlyMessage();
            return false;
        }

        private static bool ReplicationTraderTalkPrefix(object __instance)
        {
            if (replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || __instance == null) return true;
            var owner = TryGetTraderOwner(__instance);
            if (!ShouldBlockClientTrader(owner)) return true;
            ShowReplicationTraderReadOnlyMessage();
            return false;
        }

        private static bool ReplicationTraderApplyTradePrefix(object __instance)
        {
            if (replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || __instance == null) return true;
            var trader = AccessTools.Field(__instance.GetType(), "trader")?.GetValue(__instance);
            var owner = TryGetTraderOwner(trader);
            if (!ShouldBlockClientTrader(owner)) return true;
            ShowReplicationTraderReadOnlyMessage();
            return false;
        }

        private static void ReplicationTraderApplyTradePostfix(object __instance)
        {
            if (!replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || __instance == null) return;
            var trader = AccessTools.Field(__instance.GetType(), "trader")?.GetValue(__instance);
            var owner = TryGetTraderOwner(trader);
            if (owner == null) return;
            try { ReconcileHostReplicationTraderPartyAfterTrade(owner); }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative contained trader reconciliation failure error="
                    + ex.GetType().Name + ":" + ex.Message + "; full session resync is required.");
            }
        }

        private static HumanoidInstance? TryGetTraderOwner(object? trader)
        {
            if (trader is TraderBehaviour behaviour) return behaviour.Humanoid;
            return trader == null
                ? null
                : AccessTools.Property(trader.GetType(), "Humanoid")?.GetValue(trader, null) as HumanoidInstance;
        }

        private static bool ShouldBlockClientTrader(HumanoidInstance? owner)
        {
            return owner != null
                && (TraderEventAuthorityEnabled() || TryGetReplicationTraderPartyNetworkId(owner, out _));
        }

        private static void ShowReplicationTraderReadOnlyMessage()
        {
            const string message = "Merchant trading is host-only in this multiplayer build.";
            try
            {
                var controllerType = AccessTools.TypeByName("NSMedieval.BlackBarMessageController");
                var controller = controllerType == null ? null : AccessTools.Property(controllerType, "Instance")?.GetValue(null, null);
                AccessTools.Method(controllerType, "ShowBlackBarMessage", new[] { typeof(string) })?.Invoke(controller, new object[] { message });
            }
            catch
            {
                // The authoritative block remains active even if its presentation surface is unavailable.
            }
            instance?.LogReplicationInfo("Going Cooperative blocked client merchant trade: read-only trader party lane.");
        }

        internal static bool PrepareHostReplicationTraderParty(
            object nativeEvent,
            string eventId,
            string source,
            out string detail)
        {
            if (!replicationConfigHostMode
                || nativeEvent == null
                || string.IsNullOrWhiteSpace(eventId)
                || !ReplicationTraderPartySurfacesReady())
            {
                detail = "trader-party-lane-not-ready";
                return false;
            }

            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationAbortedHostTraderEvents.Contains(nativeEvent))
                {
                    detail = "trader-party-event-already-aborted";
                    return false;
                }
                if (ReplicationHostTraderParties.ContainsKey(eventId))
                {
                    detail = "ok trader-party-already-prepared";
                    return true;
                }
                if (ReplicationHostTraderPartyPrepareFailures.TryGetValue(nativeEvent, out var deferredFailure))
                {
                    if (!replicationRemoteHelloReceived && Time.realtimeSinceStartup < deferredFailure.NextRetryRealtime)
                    {
                        detail = "retry-deferred=" + deferredFailure.Reason;
                        return false;
                    }
                    ReplicationHostTraderPartyPrepareFailures.Remove(nativeEvent);
                }
            }

            if (!TryBuildHostTraderPartyRecord(nativeEvent, eventId, out var record, out detail) || record == null)
            {
                RecordHostTraderPartyPrepareFailure(nativeEvent, detail, null);
                detail = "capture=" + detail;
                return false;
            }

            // Initial publication is two-phase: the complete immutable FV payload must
            // exist before this party receives any global binding or GameEventState.
            if (!TryGetOrCreateImmutableTraderPartyBundle(record, out _, out _, out var serializationDetail))
            {
                RecordHostTraderPartyPrepareFailure(nativeEvent, serializationDetail, record);
                detail = "serialize=" + serializationDetail;
                return false;
            }

            var publishedBindings = new List<TraderPartyAgentBinding>();
            var publicationFailure = string.Empty;
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationAbortedHostTraderEvents.Contains(nativeEvent))
                {
                    detail = "trader-party-event-aborted-during-prepare";
                    return false;
                }
                if (ReplicationHostTraderParties.TryGetValue(eventId, out var concurrentRecord))
                {
                    if (ReferenceEquals(concurrentRecord.NativeEvent, nativeEvent)
                        && concurrentRecord.PublicationReady)
                    {
                        detail = "ok trader-party-concurrently-prepared";
                        return true;
                    }
                    publicationFailure = "event-id-collision";
                }
                else
                {
                    try
                    {
                        for (var i = 0; i < record.Agents.Count; i++)
                        {
                            RegisterTraderPartyBinding(record.Agents[i]);
                            publishedBindings.Add(record.Agents[i]);
                        }
                        ReplicationHostTraderParties[eventId] = record;
                        record.PublicationReady = true;
                        ReplicationHostTraderPartyPrepareFailures.Remove(nativeEvent);
                    }
                    catch (Exception ex)
                    {
                        for (var i = publishedBindings.Count - 1; i >= 0; i--)
                            UnregisterTraderPartyBinding(publishedBindings[i]);
                        publicationFailure = ex.GetType().Name + ":" + ex.Message;
                    }
                }
            }

            if (!string.IsNullOrEmpty(publicationFailure))
            {
                RecordHostTraderPartyPrepareFailure(nativeEvent, publicationFailure, record);
                detail = "binding-publication=" + publicationFailure;
                return false;
            }

            if (replicationRemoteHelloReceived)
            {
                TryStartHostTraderPartyTransfer(record, source);
            }
            detail = "ok " + detail;
            return true;
        }

        internal static void PublishHostReplicationTraderParty(object nativeEvent, string eventId)
        {
            if (PrepareHostReplicationTraderParty(nativeEvent, eventId, "native-start", out var detail)) return;
            AbortHostReplicationTraderParty(nativeEvent, eventId, detail);
        }

        internal static bool EnsureHostReplicationTraderParty(object nativeEvent, string eventId, string source)
        {
            if (!replicationConfigHostMode || nativeEvent == null || string.IsNullOrWhiteSpace(eventId)
                || !ReplicationTraderPartySurfacesReady()) return false;
            HostTraderPartyRecord? existing = null;
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationAbortedHostTraderEvents.Contains(nativeEvent)) return false;
                ReplicationHostTraderParties.TryGetValue(eventId, out existing);
            }
            if (existing != null)
            {
                if (!EnsureHostTraderPartyBootstrapReady(existing, out var bootstrapDetail))
                {
                    if (!existing.PublicationReady)
                        AbortHostReplicationTraderParty(nativeEvent, eventId, bootstrapDetail);
                    else
                        instance?.LogReplicationWarning("Going Cooperative refused trader bootstrap refresh after publication eventId="
                            + eventId + " detail=" + bootstrapDetail + "; current actors remain intact and recovery requires resync.");
                    return false;
                }
                if (replicationRemoteHelloReceived
                    && (existing.BootstrapAdvertisePending
                        || (!existing.EverTransferred && Time.realtimeSinceStartup >= existing.NextCheckpointRealtime)))
                    TryStartHostTraderPartyTransfer(existing, source);
                return true;
            }
            if (PrepareHostReplicationTraderParty(nativeEvent, eventId, source, out var detail))
            {
                instance?.LogReplicationInfo("Going Cooperative ensured host trader party eventId=" + eventId + " source=" + source);
                return true;
            }
            AbortHostReplicationTraderParty(nativeEvent, eventId, detail);
            return false;
        }

        private static bool EnsureHostTraderPartyBootstrapReady(HostTraderPartyRecord record, out string detail)
        {
            lock (ReplicationTraderPartyLock)
            {
                if (!record.PublicationReady)
                {
                    detail = "publication-not-ready";
                    return false;
                }
                if (TraderPartyRuntimePolicy.DecideBootstrapDisposition(
                        record.EverTransferred,
                        membershipRemoved: false,
                        record.BootstrapDirty,
                        record.BootstrapRefreshForNewPeer) == TraderPartyBootstrapDisposition.RebuildForNewPeer
                    && !record.TransferActive
                    && !record.BootstrapRevisionPrepared)
                {
                    if (record.ManifestRevision == int.MaxValue)
                    {
                        detail = "manifest-revision-exhausted";
                        return false;
                    }
                    record.ManifestRevision++;
                    record.CachedManifestRevision = 0;
                    record.CachedBundle = Array.Empty<byte>();
                    record.CachedHash = string.Empty;
                    record.CachedMemberNetworkIds = Array.Empty<string>();
                    record.CachedMemberManifestToken = string.Empty;
                    record.BootstrapRevisionPrepared = true;
                }
            }
            if (record.CachedBundle.Length > 0)
            {
                detail = "ok cached";
                return true;
            }
            if (!TryGetOrCreateImmutableTraderPartyBundle(record, out _, out _, out detail)) return false;
            return true;
        }

        internal static bool IsHostReplicationTraderPartyAborted(object nativeEvent)
        {
            if (nativeEvent == null) return false;
            lock (ReplicationTraderPartyLock) return ReplicationAbortedHostTraderEvents.Contains(nativeEvent);
        }

        internal static void AbortHostReplicationTraderParty(object nativeEvent, string eventId, string reason)
        {
            if (!replicationConfigHostMode || nativeEvent == null || string.IsNullOrWhiteSpace(eventId)) return;
            var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unknown-capture-failure" : reason;
            var hasAbortIdentity = false;
            var abortPartyFingerprint = string.Empty;
            var abortPartyMemberCount = 0;
            try
            {
                hasAbortIdentity = TryBuildNativeTraderPartyIdentity(
                    nativeEvent, out abortPartyFingerprint, out abortPartyMemberCount, out _);
            }
            catch (Exception ex)
            {
                // Abort must remain a fail-closed cleanup path even when a native
                // trader surface changes underneath identity inspection. An
                // identity-less abort is safe: the client refuses ambiguous
                // native cleanup and requests a full checkpoint replacement.
                instance?.LogReplicationWarning("Going Cooperative could not fingerprint aborted trader party eventId="
                    + eventId + " error=" + ex.GetType().Name + ":" + ex.Message);
            }
            if (!replicationRemoteHelloReceived || replicationRemoteCompatibilityRefused)
            {
                instance?.LogReplicationWarning("Going Cooperative left unpublished solo trader native after prepare failure eventId="
                    + eventId + " reason=" + normalizedReason + "; retry remains available before a compatible peer joins.");
                return;
            }
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationAbortedHostTraderEvents.Add(nativeEvent)) return;
                var capturedActors = new List<CreatureBase>();
                if (ReplicationHostTraderPartyPrepareFailures.TryGetValue(nativeEvent, out var failure))
                    for (var i = 0; i < failure.CapturedAgents.Count; i++)
                        if (!capturedActors.Contains(failure.CapturedAgents[i].Agent)) capturedActors.Add(failure.CapturedAgents[i].Agent);
                if (ReplicationTraderInitCreaturesByEvent.TryGetValue(nativeEvent, out var initCreatures))
                    for (var i = 0; i < initCreatures.Count; i++)
                        if (!capturedActors.Contains(initCreatures[i])) capturedActors.Add(initCreatures[i]);
                if (ReplicationHostTraderParties.TryGetValue(eventId, out var record))
                {
                    for (var i = 0; i < record.Agents.Count; i++)
                    {
                        UnregisterTraderPartyBinding(record.Agents[i]);
                        if (!capturedActors.Contains(record.Agents[i].Agent)) capturedActors.Add(record.Agents[i].Agent);
                    }
                    ReplicationHostTraderParties.Remove(eventId);
                }
                ReplicationPendingHostTraderPartyAborts.Add(new PendingHostTraderPartyAbort
                {
                    NativeEvent = nativeEvent,
                    EventId = eventId,
                    Reason = NormalizeTraderPartyWireReason(normalizedReason),
                    PartyFingerprint = hasAbortIdentity ? abortPartyFingerprint : string.Empty,
                    PartyMemberCount = hasAbortIdentity ? abortPartyMemberCount : 0,
                    CapturedActors = capturedActors
                });
                ReplicationHostTraderPartyPrepareFailures.Remove(nativeEvent);
            }
            instance?.LogReplicationWarning("Going Cooperative trader party aborted before publication eventId="
                + eventId + " reason=" + normalizedReason);
        }

        private static string NormalizeTraderPartyWireReason(string reason)
        {
            var sanitized = new string((reason ?? string.Empty)
                .Select(character => char.IsControl(character) ? ' ' : character)
                .ToArray()).Trim();
            if (sanitized.Length == 0) sanitized = "unknown-capture-failure";
            return sanitized.Length <= ReplicationTraderPartyMaxStringChars
                ? sanitized
                : sanitized.Substring(0, ReplicationTraderPartyMaxStringChars);
        }

        private static void RecordHostTraderPartyPrepareFailure(
            object nativeEvent,
            string reason,
            HostTraderPartyRecord? record)
        {
            lock (ReplicationTraderPartyLock)
            {
                ReplicationHostTraderPartyPrepareFailures[nativeEvent] = new HostTraderPartyPrepareFailure
                {
                    Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason,
                    NextRetryRealtime = Time.realtimeSinceStartup + ReplicationTraderPartyCheckpointSeconds,
                    CapturedAgents = record == null
                        ? new List<TraderPartyAgentBinding>()
                        : new List<TraderPartyAgentBinding>(record.Agents)
                };
            }
        }

        internal static bool IsHostReplicationTraderPartyPublicationReady(object nativeEvent, string eventId)
        {
            lock (ReplicationTraderPartyLock)
            {
                return !ReplicationAbortedHostTraderEvents.Contains(nativeEvent)
                    && ReplicationHostTraderParties.TryGetValue(eventId, out var record)
                    && record.PublicationReady
                    && record.CachedBundle.Length > 0;
            }
        }

        internal static void DiscardHostReplicationTraderPartyCapture(object nativeEvent, string reason)
        {
            if (nativeEvent == null) return;
            lock (ReplicationTraderPartyLock) ReplicationTraderInitCreaturesByEvent.Remove(nativeEvent);
            instance?.LogReplicationInfo("Going Cooperative discarded transient trader capture reason=" + reason);
        }

        internal static void CompleteHostReplicationTraderParty(object nativeEvent, string eventId, string reason)
        {
            if (!replicationConfigHostMode || nativeEvent == null || string.IsNullOrWhiteSpace(eventId)) return;
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationHostTraderParties.TryGetValue(eventId, out var record)
                    || !ReferenceEquals(record.NativeEvent, nativeEvent)) return;
                record.EventEnded = true;
                record.NextCheckpointRealtime = float.PositiveInfinity;
                CancelHostTraderPartyTransfersForEvent(eventId, "event-complete");
                var detached = record.Agents.Where(binding => binding.Detached).ToList();
                for (var i = 0; i < detached.Count; i++)
                {
                    if (!RememberDetachedTraderPartyAdoption(detached[i]))
                        instance?.LogReplicationWarning("Going Cooperative could not retain detached host trader adoption alias entity="
                            + detached[i].NetworkId);
                    UnregisterTraderPartyBinding(detached[i]);
                    record.Agents.Remove(detached[i]);
                    if (detached[i].Agent is HumanoidInstance humanoid) record.Humanoids.Remove(humanoid);
                    if (detached[i].Agent is AnimalInstance animal) record.Animals.Remove(animal);
                }
                if (record.Agents.Count == 0) ReplicationHostTraderParties.Remove(eventId);
                instance?.LogReplicationInfo("Going Cooperative released detached host trader identities eventId="
                    + eventId + " count=" + detached.Count.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + reason);
            }
        }

        private static bool TryBuildHostTraderPartyRecord(
            object nativeEvent,
            string eventId,
            out HostTraderPartyRecord? record,
            out string detail)
        {
            record = null;
            var traders = ReadCreatureList(nativeEvent, "traders", "Trader").OfType<HumanoidInstance>().ToList();
            var guards = ReadCreatureList(nativeEvent, "crowd", "Guards").OfType<HumanoidInstance>().ToList();
            if (traders.Count == 0)
            {
                detail = "trader-missing";
                return false;
            }

            List<CreatureBase> extras;
            lock (ReplicationTraderPartyLock)
            {
                extras = ReplicationTraderInitCreaturesByEvent.TryGetValue(nativeEvent, out var cached)
                    ? new List<CreatureBase>(cached)
                    : new List<CreatureBase>();
            }

            // Save-loaded active events do not rerun InitTrader. Reconstruct their
            // stock roster from the only durable relationship the game saves.
            var village = GlobalSaveController.CurrentVillageData;
            if (village != null)
            {
                foreach (var animal in village.Animals)
                    if (animal != null && animal.PetOwner != null && traders.Contains(animal.PetOwner)
                        && !extras.Contains(animal)) extras.Add(animal);
                var villageNpcs = village.NPCs;
                for (var i = 0; i < villageNpcs.Count; i++)
                {
                    var npc = villageNpcs[i];
                    if (npc?.PrisonerBehaviour?.Owner != null && traders.Contains(npc.PrisonerBehaviour.Owner)
                        && !extras.Contains(npc)) extras.Add(npc);
                }
            }

            var result = new HostTraderPartyRecord
            {
                NativeEvent = nativeEvent,
                EventId = eventId,
                Generation = 1,
                ManifestRevision = 1,
                NextCheckpointRealtime = Time.realtimeSinceStartup + ReplicationTraderPartyCheckpointSeconds
            };
            var seen = new HashSet<object>(ExternalReferenceComparer.Instance);
            for (var i = 0; i < traders.Count; i++) AddHostPartyAgent(result, traders[i], "Trader", i, string.Empty, seen);
            for (var i = 0; i < guards.Count; i++) AddHostPartyAgent(result, guards[i], "Guard", i, string.Empty, seen);

            var stockPrisonerOrdinal = 0;
            var stockAnimalOrdinal = 0;
            for (var i = 0; i < extras.Count; i++)
            {
                var creature = extras[i];
                if (creature == null || seen.Contains(creature) || traders.Contains(creature) || guards.Contains(creature)) continue;
                var owner = GetHostPartyOwner(creature);
                var ownerNetworkId = owner == null ? string.Empty : FindHostPartyNetworkId(result, owner);
                if (creature is AnimalInstance)
                {
                    AddHostPartyAgent(result, creature, "StockAnimal", stockAnimalOrdinal++, ownerNetworkId, seen);
                }
                else if (creature is HumanoidInstance)
                {
                    AddHostPartyAgent(result, creature, "StockPrisoner", stockPrisonerOrdinal++, ownerNetworkId, seen);
                }
            }

            if (result.Humanoids.Count > ReplicationTraderPartyMaxHumanoids
                || result.Animals.Count > ReplicationTraderPartyMaxAnimals)
            {
                detail = "roster-cap-exceeded";
                return false;
            }

            result.NextStockAnimalOrdinal = stockAnimalOrdinal;
            result.NextStockPrisonerOrdinal = stockPrisonerOrdinal;
            record = result;
            detail = "ok humanoids=" + result.Humanoids.Count.ToString(CultureInfo.InvariantCulture)
                + " animals=" + result.Animals.Count.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static void AddHostPartyAgent(
            HostTraderPartyRecord record,
            CreatureBase creature,
            string role,
            int ordinal,
            string ownerNetworkId,
            HashSet<object> seen)
        {
            if (creature == null || !seen.Add(creature)) return;
            var networkId = "event-agent:"
                + record.EventId
                + ":"
                + record.Generation.ToString(CultureInfo.InvariantCulture)
                + ":"
                + role.ToLowerInvariant()
                + ":"
                + ordinal.ToString(CultureInfo.InvariantCulture);
            var descriptor = new TraderPartyAgentBinding
            {
                EventId = record.EventId,
                Generation = record.Generation,
                NetworkId = networkId,
                Role = role,
                OwnerNetworkId = ownerNetworkId,
                HostUniqueId = creature.UniqueId,
                PriorStableEntityId = TryGetReplicationStableEntityId(creature, out var priorStableEntityId)
                    ? priorStableEntityId
                    : string.Empty,
                Fingerprint = BuildTraderPartyFingerprint(creature),
                Agent = creature,
                IsAnimal = creature is AnimalInstance,
                Roped = creature.RopedTo() != null
            };
            record.Agents.Add(descriptor);
            if (creature is AnimalInstance animal) record.Animals.Add(animal);
            else if (creature is HumanoidInstance humanoid) record.Humanoids.Add(humanoid);
        }

        private static string FindHostPartyNetworkId(HostTraderPartyRecord record, CreatureBase owner)
        {
            for (var i = 0; i < record.Agents.Count; i++)
                if (ReferenceEquals(record.Agents[i].Agent, owner)) return record.Agents[i].NetworkId;
            return string.Empty;
        }

        private static List<CreatureBase> ReadCreatureList(object owner, string fieldName, string propertyName)
        {
            var result = new List<CreatureBase>();
            object? value = AccessTools.Field(owner.GetType(), fieldName)?.GetValue(owner)
                ?? AccessTools.Property(owner.GetType(), propertyName)?.GetValue(owner, null);
            if (value is CreatureBase one)
            {
                result.Add(one);
                return result;
            }
            if (value is not IEnumerable values) return result;
            foreach (var item in values)
                if (item is CreatureBase creature && !result.Contains(creature)) result.Add(creature);
            return result;
        }

        private static CreatureBase? GetHostPartyOwner(CreatureBase creature)
        {
            if (creature is AnimalInstance animal) return animal.PetOwner;
            if (creature is HumanoidInstance humanoid) return humanoid.PrisonerBehaviour?.Owner;
            return null;
        }

        private static void ResolveHostTraderPartyOwnerState(
            string eventId,
            CreatureBase? owner,
            out string ownerId,
            out bool ownedBySameEventTrader)
        {
            ownerId = string.Empty;
            ownedBySameEventTrader = false;
            if (owner == null) return;
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationTraderPartyBindingByObject.TryGetValue(owner, out var ownerBinding)
                    && string.Equals(ownerBinding.EventId, eventId, StringComparison.Ordinal)
                    && string.Equals(ownerBinding.Role, "Trader", StringComparison.Ordinal)
                    && !ownerBinding.Tombstoned)
                {
                    ownerId = ownerBinding.NetworkId;
                    ownedBySameEventTrader = true;
                    return;
                }
            }
            TryGetReplicationStableEntityId(owner, out ownerId);
        }

        private static void TryStartHostTraderPartyTransfer(HostTraderPartyRecord record, string source)
        {
            if (!replicationConfigHostMode
                || !replicationRemoteHelloReceived
                || string.IsNullOrWhiteSpace(replicationEventHostSessionNonce)
                || replicationEventHostEpoch < 0
                || record.EventEnded
                || record.TransferActive)
                return;
            if (!EnsureHostTraderPartyBootstrapReady(record, out var readinessDetail))
            {
                record.SerializationFailureRevision = record.ManifestRevision;
                record.NextCheckpointRealtime = float.PositiveInfinity;
                instance?.LogReplicationWarning("Going Cooperative trader bootstrap readiness failed eventId="
                    + record.EventId + " revision=" + record.ManifestRevision.ToString(CultureInfo.InvariantCulture)
                    + " source=" + source + " detail=" + readinessDetail);
                return;
            }
            if (record.EventEnded || record.TransferActive) return;
            if (record.SerializationFailureRevision == record.ManifestRevision)
            {
                return;
            }
            if (!TryGetOrCreateImmutableTraderPartyBundle(record, out var bundle, out var hash, out var detail))
            {
                record.SerializationFailureRevision = record.ManifestRevision;
                record.NextCheckpointRealtime = float.PositiveInfinity;
                instance?.LogReplicationWarning("Going Cooperative trader party serialization failed closed eventId="
                    + record.EventId + " revision=" + record.ManifestRevision.ToString(CultureInfo.InvariantCulture)
                    + " detail=" + detail + "; recovery/revision change required (no hot-loop retries).");
                return;
            }

            var transferId = record.EventId
                + ":xfer:"
                + (++replicationTraderPartyTransferCounter).ToString(CultureInfo.InvariantCulture);
            var transfer = new HostTraderPartyTransfer
            {
                TransferId = transferId,
                EventId = record.EventId,
                Generation = record.ManifestRevision,
                PartyGeneration = record.Generation,
                Bundle = bundle,
                Hash = hash,
                ChunkCount = (bundle.Length + ReplicationTraderPartyRawChunkBytes - 1) / ReplicationTraderPartyRawChunkBytes,
                HumanoidCount = record.CachedHumanoidCount,
                AnimalCount = record.CachedAnimalCount,
                MemberNetworkIds = (string[])record.CachedMemberNetworkIds.Clone(),
                MemberManifestToken = record.CachedMemberManifestToken,
                BootstrapMutationRevision = record.BootstrapMutationRevision,
                CreatedRealtime = Time.realtimeSinceStartup,
                Source = source
            };
            if (transfer.ChunkCount <= 0 || transfer.ChunkCount > ReplicationTraderPartyMaxChunks)
            {
                instance?.LogReplicationWarning("Going Cooperative trader party chunk cap exceeded eventId=" + record.EventId);
                return;
            }

            record.TransferActive = true;
            record.BootstrapAdvertisePending = false;
            lock (ReplicationTraderPartyLock) ReplicationHostTraderPartyTransfers[transferId] = transfer;
            var sequence = SendHostTraderPartyDelta(
                ReplicationTraderPartyBeginDeltaKind,
                record.EventId,
                record.ManifestRevision,
                FormatReplicationTraderPartyEnvelope()
                    + " wire=" + ReplicationTraderPartyWireVersion
                    + " transferB64=" + EncodeReplicationDetailBase64(transferId)
                    + " eventIdB64=" + EncodeReplicationDetailBase64(record.EventId)
                    + " generation=" + record.ManifestRevision.ToString(CultureInfo.InvariantCulture)
                    + " partyGeneration=" + record.Generation.ToString(CultureInfo.InvariantCulture)
                    + " bytes=" + bundle.Length.ToString(CultureInfo.InvariantCulture)
                    + " chunks=" + transfer.ChunkCount.ToString(CultureInfo.InvariantCulture)
                    + " humanoids=" + record.CachedHumanoidCount.ToString(CultureInfo.InvariantCulture)
                    + " animals=" + record.CachedAnimalCount.ToString(CultureInfo.InvariantCulture)
                    + " members=" + record.CachedMemberManifestToken
                    + " gameasm=" + record.CachedGameAssemblyMvid
                    + " sha256=" + hash);
            transfer.BeginSequence = sequence;
            BindHostTraderPartySequence(sequence, transfer, -1, ReplicationTraderPartyBeginDeltaKind);
        }

        private static bool TryGetOrCreateImmutableTraderPartyBundle(
            HostTraderPartyRecord record,
            out byte[] bundle,
            out string hash,
            out string detail)
        {
            if (record.CachedBundle.Length > 0 && record.CachedManifestRevision == record.ManifestRevision)
            {
                bundle = record.CachedBundle;
                hash = record.CachedHash;
                detail = string.Empty;
                return true;
            }
            if (!TrySerializeTraderParty(record, out bundle, out hash, out detail)) return false;
            record.CachedBundle = bundle;
            record.CachedHash = hash;
            record.CachedManifestRevision = record.ManifestRevision;
            return true;
        }

        private static bool TrySerializeTraderParty(HostTraderPartyRecord record, out byte[] bundle, out string hash, out string detail)
        {
            bundle = Array.Empty<byte>();
            hash = string.Empty;
            detail = string.Empty;
            try
            {
                var gameAssemblyMvid = GetReplicationGameAssemblyModuleVersionId();
                if (!IsTraderPartyGameAssemblyMvid(gameAssemblyMvid))
                {
                    detail = "game-assembly-mvid-unavailable";
                    return false;
                }

                List<TraderPartyAgentBinding> liveAgents;
                lock (ReplicationTraderPartyLock)
                {
                    record.Agents.RemoveAll(binding => binding.Tombstoned
                        || binding.Agent == null
                        || IsTraderPartyActorDisposed(binding.Agent));
                    liveAgents = new List<TraderPartyAgentBinding>(record.Agents);
                }
                if (!liveAgents.Any(binding => string.Equals(binding.Role, "Trader", StringComparison.Ordinal)))
                {
                    record.EventEnded = true;
                    detail = "party-ended";
                    return false;
                }
                RefreshHostTraderPartyOwnership(liveAgents);
                ValidateHostTraderPartyManifest(liveAgents);
                var creatures = liveAgents.Select(binding => binding.Agent).ToList();
                record.CachedHumanoidCount = creatures.Count(creature => creature is HumanoidInstance);
                record.CachedAnimalCount = creatures.Count(creature => creature is AnimalInstance);
                record.CachedMemberNetworkIds = liveAgents.Select(binding => binding.NetworkId).ToArray();
                if (!TryEncodeTraderPartyMemberManifest(record.CachedMemberNetworkIds, out var memberManifestToken, out detail))
                    return false;
                record.CachedMemberManifestToken = memberManifestToken;

                byte[] data;
                byte[] references;
                using (var serializer = new FVSerializer(ReplicationTraderPartyWriterId, Array.Empty<string>()))
                {
                    // One polymorphic root is required so cross-creature inventory,
                    // ownership and equipment references share one FV graph.
                    serializer.Write<CreatureBase>("creatures", creatures);
                    serializer.WriteReferences();
                    data = serializer.GetBytes(ReplicationTraderPartyWriterId);
                    references = serializer.GetReferenceBytes();
                }

                byte[] uncompressed;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(ReplicationTraderPartyBundleMagic);
                    writer.Write(ReplicationTraderPartyBundleVersion);
                    writer.Write(gameAssemblyMvid);
                    writer.Write(record.EventId);
                    writer.Write(record.Generation);
                    writer.Write(record.ManifestRevision);
                    writer.Write(liveAgents.Count);
                    for (var i = 0; i < liveAgents.Count; i++) WriteTraderPartyDescriptor(writer, liveAgents[i]);
                    writer.Write(data.Length);
                    writer.Write(ComputeTraderPartySha256(data));
                    writer.Write(data);
                    writer.Write(references.Length);
                    writer.Write(ComputeTraderPartySha256(references));
                    writer.Write(references);
                    writer.Flush();
                    uncompressed = stream.ToArray();
                }
                if (uncompressed.Length <= 0 || uncompressed.Length > ReplicationTraderPartyMaxUncompressedBundleBytes)
                {
                    detail = "uncompressed-bundle-size=" + uncompressed.Length.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                using (var compressed = new MemoryStream())
                {
                    using (var gzip = new GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                        gzip.Write(uncompressed, 0, uncompressed.Length);
                    bundle = compressed.ToArray();
                }
                if (bundle.Length <= 0 || bundle.Length > ReplicationTraderPartyMaxBundleBytes)
                {
                    detail = "bundle-size=" + bundle.Length.ToString(CultureInfo.InvariantCulture);
                    bundle = Array.Empty<byte>();
                    return false;
                }
                hash = ComputeTraderPartySha256(bundle);
                record.CachedGameAssemblyMvid = gameAssemblyMvid;
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + ex.Message;
                bundle = Array.Empty<byte>();
                return false;
            }
        }

        private static void RefreshHostTraderPartyOwnership(List<TraderPartyAgentBinding> agents)
        {
            for (var i = 0; i < agents.Count; i++)
            {
                var binding = agents[i];
                if (!IsTraderPartyStockRole(binding.Role))
                {
                    binding.OwnerNetworkId = string.Empty;
                    binding.Detached = false;
                    binding.Roped = false;
                    continue;
                }
                var owner = GetHostPartyOwner(binding.Agent);
                var ownerId = string.Empty;
                var ownedByEventTrader = false;
                if (owner != null)
                {
                    var partyOwner = agents.FirstOrDefault(candidate =>
                        string.Equals(candidate.Role, "Trader", StringComparison.Ordinal)
                        && ReferenceEquals(candidate.Agent, owner));
                    if (partyOwner != null)
                    {
                        ownerId = partyOwner.NetworkId;
                        ownedByEventTrader = true;
                    }
                    else if (!TryGetReplicationStableEntityId(owner, out ownerId) || string.IsNullOrEmpty(ownerId))
                        throw new InvalidDataException("Detached trader stock owner has no stable world identity.");
                }
                binding.OwnerNetworkId = ownerId;
                // Detached means outside this merchant event, not ownerless. A sold
                // animal/prisoner may retain an ordinary settlement owner identity.
                binding.Detached = TraderPartyRuntimePolicy.IsDetached(
                    ownedByEventTrader
                        ? TraderPartyOwnerClassification.SameEventTrader
                        : owner == null
                            ? TraderPartyOwnerClassification.None
                            : TraderPartyOwnerClassification.GenericWorldOwner);
                binding.Roped = owner != null && ReferenceEquals(binding.Agent.RopedTo(), owner);
            }
        }

        private static void ValidateHostTraderPartyManifest(List<TraderPartyAgentBinding> agents)
        {
            var networkIds = new HashSet<string>(StringComparer.Ordinal);
            var hostIds = new HashSet<int>();
            for (var i = 0; i < agents.Count; i++)
            {
                var binding = agents[i];
                if (binding.HostUniqueId <= 0 || !hostIds.Add(binding.HostUniqueId))
                    throw new InvalidDataException("Trader party host unique IDs must be nonzero and unique.");
                if (!networkIds.Add(binding.NetworkId))
                    throw new InvalidDataException("Trader party network IDs must be unique.");
                ValidateTraderPartyDescriptorShape(binding);
            }
            ValidateTraderPartyOwnerRelationships(agents);
        }

        private static void WriteTraderPartyDescriptor(BinaryWriter writer, TraderPartyAgentBinding descriptor)
        {
            writer.Write(descriptor.NetworkId);
            writer.Write(descriptor.Role);
            writer.Write(descriptor.OwnerNetworkId ?? string.Empty);
            writer.Write(descriptor.PriorStableEntityId ?? string.Empty);
            writer.Write(descriptor.HostUniqueId);
            writer.Write(descriptor.Fingerprint);
            writer.Write(descriptor.IsAnimal);
            writer.Write(descriptor.Detached);
            writer.Write(descriptor.Roped);
        }

        private static long SendHostTraderPartyDelta(string kind, string eventId, int generation, string detail)
        {
            var current = instance;
            if (current == null) return -1L;
            var sequence = ++replicationWorldObjectDeltaSequence;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                sequence,
                Time.realtimeSinceStartup,
                kind,
                0L,
                eventId,
                generation,
                0,
                0,
                detail));
            return sequence;
        }

        private static void BindHostTraderPartySequence(long sequence, HostTraderPartyTransfer transfer, int chunkIndex, string kind)
        {
            if (sequence < 0L) return;
            lock (ReplicationTraderPartyLock)
            {
                ReplicationHostTraderPartySequenceBindings[sequence] = new HostTraderPartySequenceBinding
                {
                    TransferId = transfer.TransferId,
                    ChunkIndex = chunkIndex,
                    Kind = kind
                };
            }
        }

        private static void HandleReplicationTraderPartyWorldDeltaAck(ReplicationWorldObjectDeltaAck ack)
        {
            if (!replicationConfigHostMode) return;
            var positive = ack.Applied
                || (ack.Duplicate && ack.Detail.IndexOf("duplicate-sequence", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!positive) return;

            HostTraderPartyTransfer? transfer;
            HostTraderPartySequenceBinding? binding;
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationHostTraderPartySequenceBindings.TryGetValue(ack.Sequence, out binding)
                    || !ReplicationHostTraderPartyTransfers.TryGetValue(binding.TransferId, out transfer)) return;
                ReplicationHostTraderPartySequenceBindings.Remove(ack.Sequence);
                if (binding.ChunkIndex >= 0) transfer.InFlightChunks.Remove(binding.ChunkIndex);
            }

            if (string.Equals(binding.Kind, ReplicationTraderPartyBeginDeltaKind, StringComparison.Ordinal))
            {
                if (ack.Detail.IndexOf("trader-party-begin-already-applied", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    CompleteHostTraderPartyTransfer(transfer);
                    return;
                }
                transfer.BeginAcknowledged = true;
                SendHostTraderPartyChunkWindow(transfer);
                return;
            }
            if (string.Equals(binding.Kind, ReplicationTraderPartyChunkDeltaKind, StringComparison.Ordinal))
            {
                SendHostTraderPartyChunkWindow(transfer);
                return;
            }
            if (!string.Equals(binding.Kind, ReplicationTraderPartyCommitDeltaKind, StringComparison.Ordinal)) return;

            CompleteHostTraderPartyTransfer(transfer);
        }

        private static void CompleteHostTraderPartyTransfer(HostTraderPartyTransfer transfer)
        {
            lock (ReplicationTraderPartyLock)
            {
                PurgeHostTraderPartyTransferSequences(transfer.TransferId);
                ReplicationHostTraderPartyTransfers.Remove(transfer.TransferId);
                if (ReplicationHostTraderParties.TryGetValue(transfer.EventId, out var record))
                {
                    var dirty = record.BootstrapMutationRevision != transfer.BootstrapMutationRevision
                        || record.ManifestRevision > transfer.Generation;
                    record.TransferActive = false;
                    record.EverTransferred = true;
                    record.BootstrapDirty = dirty;
                    record.BootstrapRevisionPrepared = false;
                    record.BootstrapRefreshForNewPeer = false;
                    record.BootstrapAdvertisePending = false;
                    // A manifest revision is immutable. It is resent only after a new
                    // compatible hello/epoch or explicit semantic revision.
                    // Live mutations converge through semantic deltas. Dirty full FV
                    // state remains deferred until a future compatible hello/epoch.
                    record.NextCheckpointRealtime = float.PositiveInfinity;
                }
            }
            transfer.Bundle = Array.Empty<byte>();
        }

        private static void SendHostTraderPartyChunkWindow(HostTraderPartyTransfer transfer)
        {
            if (!transfer.BeginAcknowledged || transfer.CommitSent) return;
            while (transfer.NextChunkIndex < transfer.ChunkCount
                && transfer.InFlightChunks.Count < ReplicationTraderPartySendWindow)
            {
                var index = transfer.NextChunkIndex++;
                var offset = index * ReplicationTraderPartyRawChunkBytes;
                var count = Math.Min(ReplicationTraderPartyRawChunkBytes, transfer.Bundle.Length - offset);
                var bytes = new byte[count];
                Buffer.BlockCopy(transfer.Bundle, offset, bytes, 0, count);
                var sequence = SendHostTraderPartyDelta(
                    ReplicationTraderPartyChunkDeltaKind,
                    transfer.EventId,
                    transfer.Generation,
                    FormatReplicationTraderPartyEnvelope()
                        + " wire=" + ReplicationTraderPartyWireVersion
                        + " transferB64=" + EncodeReplicationDetailBase64(transfer.TransferId)
                        + " index=" + index.ToString(CultureInfo.InvariantCulture)
                        + " chunks=" + transfer.ChunkCount.ToString(CultureInfo.InvariantCulture)
                        + " data=" + Convert.ToBase64String(bytes));
                transfer.InFlightChunks.Add(index);
                BindHostTraderPartySequence(sequence, transfer, index, ReplicationTraderPartyChunkDeltaKind);
            }

            if (transfer.NextChunkIndex < transfer.ChunkCount || transfer.InFlightChunks.Count > 0) return;
            transfer.CommitSent = true;
            var commitSequence = SendHostTraderPartyDelta(
                ReplicationTraderPartyCommitDeltaKind,
                transfer.EventId,
                transfer.Generation,
                FormatReplicationTraderPartyEnvelope()
                    + " wire=" + ReplicationTraderPartyWireVersion
                    + " transferB64=" + EncodeReplicationDetailBase64(transfer.TransferId)
                    + " eventIdB64=" + EncodeReplicationDetailBase64(transfer.EventId)
                    + " generation=" + transfer.Generation.ToString(CultureInfo.InvariantCulture)
                    + " partyGeneration=" + transfer.PartyGeneration.ToString(CultureInfo.InvariantCulture)
                    + " sha256=" + transfer.Hash);
            transfer.CommitSequence = commitSequence;
            BindHostTraderPartySequence(commitSequence, transfer, -1, ReplicationTraderPartyCommitDeltaKind);
        }

        private static void UpdateReplicationTraderPartyTransfers()
        {
            var now = Time.realtimeSinceStartup;
            if (replicationConfigHostMode)
            {
                ProcessPendingHostTraderPartyAborts();
                if (replicationRemoteHelloReceived && !replicationTraderPartyObservedRemoteReady)
                {
                    replicationTraderPartyObservedRemoteReady = true;
                    lock (ReplicationTraderPartyLock)
                        foreach (var record in ReplicationHostTraderParties.Values)
                            if (!record.EventEnded)
                            {
                                record.BootstrapAdvertisePending = true;
                                record.BootstrapRefreshForNewPeer = record.BootstrapDirty && record.EverTransferred;
                                record.SerializationFailureRevision = 0;
                                record.NextCheckpointRealtime = 0f;
                            }
                }
                else if (!replicationRemoteHelloReceived)
                {
                    replicationTraderPartyObservedRemoteReady = false;
                }

                if (replicationRemoteHelloReceived && now >= replicationNextTraderPartyCheckpointRealtime)
                {
                    replicationNextTraderPartyCheckpointRealtime = now + 1f;
                    List<HostTraderPartyRecord> due;
                    lock (ReplicationTraderPartyLock)
                        due = ReplicationHostTraderParties.Values.Where(r => !r.TransferActive && now >= r.NextCheckpointRealtime).ToList();
                    for (var i = 0; i < due.Count; i++) TryStartHostTraderPartyTransfer(due[i], "periodic-checkpoint");
                }

                ExpireHostTraderPartyTransfers(now);
            }
            else
            {
                ExpireClientTraderPartyTransfers(now);
                ProcessReplicationTraderPartyRecovery();
            }
        }

        private static void ProcessPendingHostTraderPartyAborts()
        {
            List<PendingHostTraderPartyAbort> pending;
            lock (ReplicationTraderPartyLock) pending = ReplicationPendingHostTraderPartyAborts.ToList();
            for (var i = 0; i < pending.Count; i++)
            {
                var abort = pending[i];
                if (!abort.AbortSent && replicationRemoteHelloReceived && !replicationRemoteCompatibilityRefused)
                {
                    SendHostTraderPartyDelta(
                        ReplicationTraderPartyAbortDeltaKind,
                        abort.EventId,
                        1,
                        FormatReplicationTraderPartyEnvelope()
                            + " wire=" + ReplicationTraderPartyWireVersion
                            + " eventIdB64=" + EncodeReplicationDetailBase64(abort.EventId)
                            + " partyFingerprint=" + (string.IsNullOrEmpty(abort.PartyFingerprint) ? "_" : abort.PartyFingerprint)
                            + " partyMembers=" + abort.PartyMemberCount.ToString(CultureInfo.InvariantCulture)
                            + " reasonB64=" + EncodeReplicationDetailBase64(abort.Reason));
                    abort.AbortSent = true;
                }

                if (abort.CleanupComplete || abort.CleanupAttempts >= 3) continue;
                abort.CleanupAttempts++;
                abort.CleanupComplete = TryCleanupAbortedHostTraderParty(abort, out var detail);
                if (!abort.CleanupComplete)
                    instance?.LogReplicationWarning("Going Cooperative aborted host trader cleanup retry eventId="
                        + abort.EventId + " attempt=" + abort.CleanupAttempts.ToString(CultureInfo.InvariantCulture)
                        + " detail=" + detail);
            }
            lock (ReplicationTraderPartyLock)
            {
                ReplicationPendingHostTraderPartyAborts.RemoveAll(abort => abort.CleanupComplete && (abort.AbortSent || !replicationRemoteHelloReceived));
            }
        }

        internal static bool FlushHostTraderPartyAbortsBeforeCheckpoint(out string detail)
        {
            detail = string.Empty;
            if (!replicationConfigHostMode) return true;
            for (var pass = 0; pass < 3; pass++)
            {
                ProcessPendingHostTraderPartyAborts();
                List<PendingHostTraderPartyAbort> unresolved;
                lock (ReplicationTraderPartyLock)
                    unresolved = ReplicationPendingHostTraderPartyAborts
                        .Where(abort => !abort.CleanupComplete)
                        .ToList();
                if (unresolved.Count == 0) break;
                for (var i = 0; i < unresolved.Count; i++)
                {
                    unresolved[i].CleanupAttempts++;
                    unresolved[i].CleanupComplete = TryCleanupAbortedHostTraderParty(unresolved[i], out detail);
                }
            }
            ProcessPendingHostTraderPartyAborts();
            lock (ReplicationTraderPartyLock)
            {
                var pending = ReplicationPendingHostTraderPartyAborts.Count(abort => !abort.CleanupComplete);
                if (pending == 0 && ReplicationAbortedHostTraderEvents.Count == 0)
                {
                    detail = "ok abort-cleanup-flushed";
                    return true;
                }
                detail = "pending=" + pending.ToString(CultureInfo.InvariantCulture)
                    + " abortedEvents=" + ReplicationAbortedHostTraderEvents.Count.ToString(CultureInfo.InvariantCulture)
                    + (string.IsNullOrEmpty(detail) ? string.Empty : " last=" + detail);
                return false;
            }
        }

        private static bool TryCleanupAbortedHostTraderParty(PendingHostTraderPartyAbort abort, out string detail)
        {
            replicationTraderPartyApplicationDepth++;
            try
            {
                var system = NSMedieval.GameEventSystem.GameEventSystem.Instance;
                AccessTools.Method(abort.NativeEvent.GetType(), "Unsubscribe")?.Invoke(abort.NativeEvent, Array.Empty<object>());
                AccessTools.Method(abort.NativeEvent.GetType(), "RemoveWarningMessage")?.Invoke(abort.NativeEvent, Array.Empty<object>());
                system.RemoveFromRunningEvents((GameEventInstance)abort.NativeEvent);
                if (system.RunningEvents.Contains((GameEventInstance)abort.NativeEvent))
                    throw new InvalidOperationException("aborted-native-event-remains-running");

                var actors = new List<CreatureBase>(abort.CapturedActors);
                foreach (var trader in ReadCreatureList(abort.NativeEvent, "traders", "Trader"))
                    if (!actors.Contains(trader)) actors.Add(trader);
                foreach (var guard in ReadCreatureList(abort.NativeEvent, "crowd", "Guards"))
                    if (!actors.Contains(guard)) actors.Add(guard);
                lock (ReplicationTraderPartyLock)
                    if (ReplicationTraderInitCreaturesByEvent.TryGetValue(abort.NativeEvent, out var extras))
                        for (var i = 0; i < extras.Count; i++) if (!actors.Contains(extras[i])) actors.Add(extras[i]);

                for (var i = actors.Count - 1; i >= 0; i--)
                {
                    var actor = actors[i];
                    if (actor == null || IsTraderPartyActorDisposed(actor)) continue;
                    if (actor is AnimalInstance animal)
                    {
                        AnimalManager.Instance.RemoveAnimal(animal, false);
                        if (GlobalSaveController.CurrentVillageData.Animals.Contains(animal))
                            throw new InvalidOperationException("aborted-animal-removal-postcondition");
                    }
                    else if (actor is HumanoidInstance humanoid)
                    {
                        humanoid.DestroyStorage();
                        humanoid.DestroyEquipment();
                        NPCController.Instance.RemoveNPC(humanoid);
                        if (GlobalSaveController.CurrentVillageData.NPCs.Contains(humanoid))
                            GlobalSaveController.CurrentVillageData.NPCs.Remove(humanoid);
                        if (GlobalSaveController.CurrentVillageData.NPCs.Contains(humanoid))
                            throw new InvalidOperationException("aborted-humanoid-removal-postcondition");
                    }
                }
                for (var i = 0; i < actors.Count; i++)
                {
                    if (actors[i] is AnimalInstance cachedAnimal
                        && GlobalSaveController.CurrentVillageData.Animals.Contains(cachedAnimal))
                        throw new InvalidOperationException("aborted-cached-animal-survived-cleanup");
                    if (actors[i] is HumanoidInstance cachedHumanoid
                        && (GlobalSaveController.CurrentVillageData.NPCs.Contains(cachedHumanoid)
                            || GlobalSaveController.CurrentVillageData.Workers.Contains(cachedHumanoid)))
                        throw new InvalidOperationException("aborted-cached-humanoid-survived-cleanup");
                }

                AccessTools.Method(abort.NativeEvent.GetType(), "Dispose")?.Invoke(abort.NativeEvent, Array.Empty<object>());
                lock (ReplicationTraderPartyLock)
                {
                    ReplicationTraderInitCreaturesByEvent.Remove(abort.NativeEvent);
                    ReplicationAbortedHostTraderEvents.Remove(abort.NativeEvent);
                }
                detail = "ok";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + ex.Message;
                return false;
            }
            finally
            {
                replicationTraderPartyApplicationDepth--;
            }
        }

        private static void ProcessReplicationTraderPartyRecovery()
        {
            if (!replicationTraderPartyRecoveryRequested || replicationTraderPartyRecoveryAttempted || instance == null) return;
            replicationTraderPartyRecoveryAttempted = true;
            if (!instance.TryRequestFullMultiplayerResync(out var error))
                instance.LogReplicationWarning("Going Cooperative automatic trader resync request failed reason="
                    + replicationTraderPartyRecoveryReason + " error=" + error);
        }

        private static void ExpireHostTraderPartyTransfers(float now)
        {
            var expired = new List<string>();
            lock (ReplicationTraderPartyLock)
            {
                foreach (var pair in ReplicationHostTraderPartyTransfers)
                    if (now - pair.Value.CreatedRealtime > ReplicationTraderPartyTransferTimeoutSeconds) expired.Add(pair.Key);
                for (var i = 0; i < expired.Count; i++)
                {
                    var transfer = ReplicationHostTraderPartyTransfers[expired[i]];
                    ReplicationHostTraderPartyTransfers.Remove(expired[i]);
                    PurgeHostTraderPartyTransferSequences(transfer.TransferId);
                    if (ReplicationHostTraderParties.TryGetValue(transfer.EventId, out var record))
                    {
                        record.TransferActive = false;
                        record.BootstrapAdvertisePending = true;
                        record.NextCheckpointRealtime = now + 2f;
                    }
                }
            }
        }

        private static void PurgeHostTraderPartyTransferSequences(string transferId)
        {
            var sequences = ReplicationHostTraderPartySequenceBindings
                .Where(pair => string.Equals(pair.Value.TransferId, transferId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList();
            for (var i = 0; i < sequences.Count; i++) ReplicationHostTraderPartySequenceBindings.Remove(sequences[i]);
            lock (ReplicationWorldObjectDeltaLock)
                for (var i = 0; i < sequences.Count; i++) replicationPendingWorldObjectDeltas.Remove(sequences[i]);
        }

        private static void ExpireClientTraderPartyTransfers(float now)
        {
            var trackerExpired = ReplicationClientTraderPartyTracker.Expire(
                Math.Max(0L, (long)(now * 1000f)));
            var expired = new List<string>();
            lock (ReplicationTraderPartyLock)
            {
                foreach (var pair in ReplicationClientTraderPartyTransfers)
                    if (now - pair.Value.LastTouchedRealtime > ReplicationTraderPartyTransferTimeoutSeconds) expired.Add(pair.Key);
                for (var i = 0; i < expired.Count; i++) ReplicationClientTraderPartyTransfers.Remove(expired[i]);
                for (var i = 0; i < expired.Count; i++)
                {
                    // The transfer object was removed above; terminal pruning is keyed
                    // from the ended-event set and remaining bindings.
                    foreach (var eventId in ReplicationClientEndedTraderParties.ToList())
                        TryFinalizeClientReplicationTraderParty(eventId);
                }
            }
            if (expired.Count > 0 || trackerExpired > 0)
                instance?.LogReplicationWarning("Going Cooperative trader party transfer expired incomplete count="
                    + expired.Count.ToString(CultureInfo.InvariantCulture)
                    + " tracker=" + trackerExpired.ToString(CultureInfo.InvariantCulture)
                    + "; full session resync is required if the next checkpoint cannot repair it.");
        }

        private static bool IsReplicationTraderPartyDeltaKind(string kind)
        {
            return string.Equals(kind, ReplicationTraderPartyBeginDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationTraderPartyChunkDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationTraderPartyCommitDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationTraderPartyAgentStateDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationTraderPartyTombstoneDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationTraderPartyMemberAdoptDeltaKind, StringComparison.Ordinal)
                || string.Equals(kind, ReplicationTraderPartyAbortDeltaKind, StringComparison.Ordinal);
        }

        private static bool TryApplyReplicationTraderPartyWorldDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (replicationConfigHostMode || !TraderEventAuthorityEnabled())
            {
                detail = "trader-party-lane-disabled";
                return false;
            }
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch))
            {
                detail = "trader-party-envelope-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyBeginDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTraderPartyBegin(delta, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyChunkDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTraderPartyChunk(delta, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyCommitDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTraderPartyCommit(delta, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyAgentStateDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTraderPartyAgentState(delta, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyTombstoneDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTraderPartyTombstone(delta, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyMemberAdoptDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTraderPartyMemberAdopt(delta, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyAbortDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTraderPartyAbort(delta, out detail);
            detail = "trader-party-unsupported-kind";
            return false;
        }

        private static bool TryApplyReplicationTraderPartyAbort(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "wire", out var wire)
                || !string.Equals(wire, ReplicationTraderPartyWireVersion, StringComparison.Ordinal)
                || !TryReadTraderPartyText(delta.Detail, "eventIdB64", out var eventId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "partyFingerprint", out var partyFingerprint)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "partyMembers", out var partyMemberCount)
                || (partyMemberCount == 0 && !string.Equals(partyFingerprint, "_", StringComparison.Ordinal))
                || (partyMemberCount > 0 && !IsTraderPartySha256(partyFingerprint))
                || partyMemberCount < 0
                || partyMemberCount > ReplicationTraderPartyMaxHumanoids + ReplicationTraderPartyMaxAnimals
                || !TryReadTraderPartyText(delta.Detail, "reasonB64", out var reason))
            {
                detail = "trader-party-abort-malformed";
                return false;
            }
            lock (ReplicationTraderPartyLock)
            {
                var transferIds = ReplicationClientTraderPartyTransfers.Values
                    .Where(transfer => string.Equals(transfer.EventId, eventId, StringComparison.Ordinal))
                    .Select(transfer => transfer.TransferId)
                    .ToList();
                for (var i = 0; i < transferIds.Count; i++) ReplicationClientTraderPartyTransfers.Remove(transferIds[i]);
                ReplicationClientEndedTraderParties.Remove(eventId);
                if (ReplicationClientTerminalTraderParties.Add(eventId)) ReplicationClientTerminalTraderPartyOrder.Enqueue(eventId);
                ReplicationClientTraderPartyTracker.RecordPartyTombstone(eventId, 1, int.MaxValue);
                while (ReplicationClientTerminalTraderPartyOrder.Count > ReplicationTraderPartyMaxTerminalEvents)
                {
                    var expiredEvent = ReplicationClientTerminalTraderPartyOrder.Dequeue();
                    ReplicationClientTerminalTraderParties.Remove(expiredEvent);
                    ReplicationClientTraderPartyGenerationByEvent.Remove(expiredEvent);
                    ReplicationClientTraderPartyHashByEvent.Remove(expiredEvent);
                }
                if (ReplicationTraderPartyBindingByObject.Values.Any(binding => string.Equals(binding.EventId, eventId, StringComparison.Ordinal)))
                {
                    LatchReplicationTraderPartyFailure(eventId, 1, "abort-after-party-publication");
                    detail = "trader-party-abort-after-publication";
                    return false;
                }
            }
            var nativeCleanupDetail = string.Empty;
            var nativeCleanupSucceeded = false;
            try
            {
                nativeCleanupSucceeded = TryCleanupClientAbortedNativeTraderParty(
                    eventId, partyFingerprint, partyMemberCount, out nativeCleanupDetail);
            }
            catch (Exception ex)
            {
                // Native event enumeration and fingerprinting use reflected game
                // surfaces. Contain drift here so a malformed abort cannot escape
                // into the game's command loop; recovery remains a full resync.
                nativeCleanupDetail = "native-party-cleanup-exception="
                    + ex.GetType().Name + ":" + ex.Message;
            }
            if (!nativeCleanupSucceeded)
            {
                LatchReplicationTraderPartyFailure(eventId, 1, nativeCleanupDetail);
                detail = "trader-party-abort-native-cleanup=" + nativeCleanupDetail;
                return false;
            }
            RemoveClientTraderPartySemanticHighWaters(eventId);
            ShowReplicationTraderAbortMessage(reason);
            detail = "ok trader-party-abort";
            return true;
        }

        private static bool TryCleanupClientAbortedNativeTraderParty(
            string eventId,
            string partyFingerprint,
            int partyMemberCount,
            out string detail)
        {
            detail = string.Empty;
            var candidates = NSMedieval.GameEventSystem.GameEventSystem.Instance.RunningEvents
                .Where(IsReplicationTraderEventInstance)
                .Where(nativeEvent =>
                {
                    lock (ReplicationTraderPartyLock)
                        return !ReplicationAdoptedTraderEvents.TryGetValue(nativeEvent, out _);
                })
                .Take(ReplicationTraderPartyMaxAbortNativeCandidates + 1)
                .ToList();
            if (candidates.Count == 0)
            {
                detail = "ok no-native-party";
                return true;
            }
            if (partyMemberCount == 0 || string.Equals(partyFingerprint, "_", StringComparison.Ordinal))
            {
                detail = "native-party-identity-unavailable";
                return false;
            }
            if (candidates.Count > ReplicationTraderPartyMaxAbortNativeCandidates)
            {
                detail = "native-party-candidate-cap";
                return false;
            }

            var matches = new List<KeyValuePair<object, List<CreatureBase>>>();
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!TryBuildNativeTraderPartyIdentity(
                    candidates[i], out var candidateFingerprint, out var candidateCount, out var actors)) continue;
                if (candidateCount == partyMemberCount
                    && string.Equals(candidateFingerprint, partyFingerprint, StringComparison.Ordinal))
                    matches.Add(new KeyValuePair<object, List<CreatureBase>>(candidates[i], actors));
            }
            if (matches.Count != 1)
            {
                detail = "native-party-exact-match-count=" + matches.Count.ToString(CultureInfo.InvariantCulture)
                    + " candidates=" + candidates.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            var match = matches[0];
            if (!TryQuarantineImportedClientTraderEvent(eventId, match.Key, out var quarantineDetail))
            {
                detail = "native-party-park=" + quarantineDetail;
                return false;
            }
            replicationTraderPartyApplicationDepth++;
            try
            {
                for (var i = match.Value.Count - 1; i >= 0; i--)
                {
                    var actor = match.Value[i];
                    if (actor is AnimalInstance animal)
                    {
                        AnimalManager.Instance.RemoveAnimal(animal, false);
                        if (GlobalSaveController.CurrentVillageData.Animals.Contains(animal))
                            throw new InvalidOperationException("aborted-client-animal-remains");
                    }
                    else if (actor is HumanoidInstance humanoid)
                    {
                        humanoid.DestroyStorage();
                        humanoid.DestroyEquipment();
                        NPCController.Instance.RemoveNPC(humanoid);
                        if (GlobalSaveController.CurrentVillageData.NPCs.Contains(humanoid))
                            GlobalSaveController.CurrentVillageData.NPCs.Remove(humanoid);
                        if (GlobalSaveController.CurrentVillageData.NPCs.Contains(humanoid))
                            throw new InvalidOperationException("aborted-client-humanoid-remains");
                    }
                }
                AccessTools.Method(match.Key.GetType(), "Dispose")?.Invoke(match.Key, Array.Empty<object>());
                ReleaseReplicationTraderEventQuarantine(eventId);
                detail = "ok exact-native-party-cleaned members=" + match.Value.Count.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                // The exact event stays quarantined and absent from RunningEvents.
                // Never broaden cleanup to other merchant candidates; recovery is a
                // full save replacement from the host.
                detail = "exact-native-party-cleanup=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
            finally
            {
                replicationTraderPartyApplicationDepth--;
            }
        }

        private static void ShowReplicationTraderAbortMessage(string reason)
        {
            var message = "Merchant visit cancelled by multiplayer safety checks: " + reason;
            try
            {
                var controllerType = AccessTools.TypeByName("NSMedieval.BlackBarMessageController");
                var controller = controllerType == null ? null : AccessTools.Property(controllerType, "Instance")?.GetValue(null, null);
                AccessTools.Method(controllerType, "ShowBlackBarMessage", new[] { typeof(string) })?.Invoke(controller, new object[] { message });
            }
            catch { }
            instance?.LogReplicationWarning("Going Cooperative " + message);
        }

        private static bool TryApplyReplicationTraderPartyBegin(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadTraderPartyWire(delta.Detail, out var transferId)
                || !TryReadTraderPartyText(delta.Detail, "eventIdB64", out var eventId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "generation", out var generation)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "partyGeneration", out var partyGeneration)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "bytes", out var bytes)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "chunks", out var chunks)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "humanoids", out var humanoids)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "animals", out var animals)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "members", out var memberManifestToken)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "gameasm", out var gameAssemblyMvid)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "sha256", out var hash)
                || string.IsNullOrWhiteSpace(eventId)
                || generation <= 0 || partyGeneration <= 0
                || bytes <= 0 || bytes > ReplicationTraderPartyMaxBundleBytes
                || chunks <= 0 || chunks > ReplicationTraderPartyMaxChunks
                || chunks != (bytes + ReplicationTraderPartyRawChunkBytes - 1) / ReplicationTraderPartyRawChunkBytes
                || humanoids < 0 || humanoids > ReplicationTraderPartyMaxHumanoids
                || animals < 0 || animals > ReplicationTraderPartyMaxAnimals
                || humanoids + animals < 1
                || !IsTraderPartySha256(hash)
                || !IsTraderPartyGameAssemblyMvid(gameAssemblyMvid)
                || !string.Equals(gameAssemblyMvid, GetReplicationGameAssemblyModuleVersionId(), StringComparison.Ordinal))
            {
                detail = "trader-party-begin-malformed";
                return false;
            }
            if (!TryDecodeTraderPartyMemberManifest(memberManifestToken, eventId, partyGeneration, humanoids + animals, out var memberNetworkIds))
            {
                detail = "trader-party-begin-member-manifest";
                return false;
            }

            var trackerManifest = new TraderPartyTransferManifest(
                transferId,
                eventId,
                partyGeneration,
                generation,
                chunks,
                bytes,
                memberNetworkIds,
                hash);
            var trackerResult = ReplicationClientTraderPartyTracker.RecordBegin(trackerManifest, TraderPartyTrackerNowMilliseconds());
            if (trackerResult == TraderPartyTransferRecordResult.Conflict
                || trackerResult == TraderPartyTransferRecordResult.CapacityExceeded)
            {
                detail = "trader-party-begin-tracker-" + trackerResult.ToString().ToLowerInvariant();
                LatchReplicationTraderPartyFailure(eventId, generation, detail);
                return false;
            }
            if (trackerResult == TraderPartyTransferRecordResult.Stale)
            {
                detail = "ok trader-party-begin-tracker-stale";
                return true;
            }
            if (trackerResult == TraderPartyTransferRecordResult.Duplicate)
            {
                lock (ReplicationTraderPartyLock)
                {
                    if (ReplicationClientTraderPartyTransfers.Values.Any(transfer =>
                        string.Equals(transfer.EventId, eventId, StringComparison.Ordinal)
                        && transfer.Generation == generation
                        && !string.Equals(transfer.TransferId, transferId, StringComparison.Ordinal)))
                    {
                        detail = "ok trader-party-begin-duplicate-active-transfer";
                        return true;
                    }
                }
            }

            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationClientTerminalTraderParties.Contains(eventId))
                {
                    detail = "ok trader-party-begin-already-applied-terminal";
                    return true;
                }
                if (ReplicationClientTraderPartyGenerationByEvent.TryGetValue(eventId, out var appliedGeneration)
                    && generation <= appliedGeneration)
                {
                    if (generation < appliedGeneration)
                    {
                        detail = "ok trader-party-begin-stale";
                        return true;
                    }
                    var sameHash = ReplicationClientTraderPartyHashByEvent.TryGetValue(eventId, out var appliedHash)
                        && string.Equals(appliedHash, hash, StringComparison.OrdinalIgnoreCase);
                    detail = sameHash ? "ok trader-party-begin-already-applied" : "trader-party-same-generation-hash-conflict";
                    if (!sameHash) LatchReplicationTraderPartyFailure(eventId, generation, detail);
                    return sameHash;
                }
                if (ReplicationClientTraderPartyTransfers.TryGetValue(transferId, out var existing))
                {
                    var same = TraderPartyTransferEnvelopeMatches(
                        existing, eventId, generation, partyGeneration, gameAssemblyMvid,
                        bytes, chunks, humanoids, animals, hash);
                    detail = same ? "ok trader-party-begin-duplicate" : "trader-party-begin-conflict";
                    if (!same) LatchReplicationTraderPartyFailure(eventId, generation, detail);
                    return same;
                }
                var sameRevision = ReplicationClientTraderPartyTransfers
                    .Where(pair => string.Equals(pair.Value.EventId, eventId, StringComparison.Ordinal)
                        && pair.Value.Generation == generation)
                    .ToList();
                if (sameRevision.Any(pair => !TraderPartyTransferEnvelopeMatches(
                    pair.Value, eventId, generation, partyGeneration, gameAssemblyMvid,
                    bytes, chunks, humanoids, animals, hash)))
                {
                    detail = "trader-party-same-generation-envelope-conflict";
                    LatchReplicationTraderPartyFailure(eventId, generation, detail);
                    return false;
                }
                for (var i = 0; i < sameRevision.Count; i++) ReplicationClientTraderPartyTransfers.Remove(sameRevision[i].Key);
                var superseded = ReplicationClientTraderPartyTransfers
                    .Where(pair => string.Equals(pair.Value.EventId, eventId, StringComparison.Ordinal)
                        && pair.Value.Generation < generation)
                    .Select(pair => pair.Key)
                    .ToList();
                for (var i = 0; i < superseded.Count; i++) ReplicationClientTraderPartyTransfers.Remove(superseded[i]);
                if (ReplicationClientTraderPartyTransfers.Count >= ReplicationTraderPartyMaxClientTransfers
                    || ReplicationClientTraderPartyTransfers.Values.Sum(item => item.ExpectedBytes) + bytes > ReplicationTraderPartyMaxBundleBytes)
                {
                    detail = "trader-party-begin-allocation-cap";
                    return false;
                }
                ReplicationClientTraderPartyTransfers[transferId] = new ClientTraderPartyTransfer
                {
                    TransferId = transferId,
                    EventId = eventId,
                    Generation = generation,
                    PartyGeneration = partyGeneration,
                    GameAssemblyMvid = gameAssemblyMvid,
                    ExpectedBytes = bytes,
                    ChunkCount = chunks,
                    ExpectedHumanoids = humanoids,
                    ExpectedAnimals = animals,
                    Hash = hash,
                    MemberNetworkIds = memberNetworkIds,
                    Chunks = new byte[chunks][],
                    Received = new bool[chunks],
                    LastTouchedRealtime = Time.realtimeSinceStartup
                };
            }
            detail = "ok trader-party-begin";
            return true;
        }

        private static bool TraderPartyTransferEnvelopeMatches(
            ClientTraderPartyTransfer transfer,
            string eventId,
            int generation,
            int partyGeneration,
            string gameAssemblyMvid,
            int bytes,
            int chunks,
            int humanoids,
            int animals,
            string hash)
        {
            return string.Equals(transfer.EventId, eventId, StringComparison.Ordinal)
                && transfer.Generation == generation
                && transfer.PartyGeneration == partyGeneration
                && string.Equals(transfer.GameAssemblyMvid, gameAssemblyMvid, StringComparison.Ordinal)
                && transfer.ExpectedBytes == bytes
                && transfer.ChunkCount == chunks
                && transfer.ExpectedHumanoids == humanoids
                && transfer.ExpectedAnimals == animals
                && string.Equals(transfer.Hash, hash, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryApplyReplicationTraderPartyChunk(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadTraderPartyWire(delta.Detail, out var transferId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "index", out var index)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "chunks", out var chunks)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "data", out var dataToken)
                || dataToken.Length <= 0
                || dataToken.Length > ((ReplicationTraderPartyRawChunkBytes + 2) / 3) * 4
                || dataToken.Length % 4 != 0)
            {
                detail = "trader-party-chunk-malformed";
                return false;
            }
            byte[] data;
            try { data = Convert.FromBase64String(dataToken); }
            catch { detail = "trader-party-chunk-base64"; return false; }

            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationClientTraderPartyTransfers.TryGetValue(transferId, out var transfer))
                {
                    detail = "trader-party-chunk-begin-missing-retry";
                    return false;
                }
                if (chunks != transfer.ChunkCount || index < 0 || index >= chunks
                    || data.Length != Math.Min(
                        ReplicationTraderPartyRawChunkBytes,
                        transfer.ExpectedBytes - index * ReplicationTraderPartyRawChunkBytes))
                {
                    detail = "trader-party-chunk-bounds";
                    return false;
                }
                if (transfer.Received[index])
                {
                    var duplicate = transfer.Chunks[index].SequenceEqual(data);
                    detail = duplicate ? "ok trader-party-chunk-duplicate" : "trader-party-chunk-conflict";
                    if (!duplicate) LatchReplicationTraderPartyFailure(transfer.EventId, transfer.Generation, detail);
                    return duplicate;
                }
                var trackerResult = ReplicationClientTraderPartyTracker.RecordChunk(
                    new TraderPartyTransferChunk(transferId, index, data),
                    TraderPartyTrackerNowMilliseconds());
                if (trackerResult == TraderPartyTransferRecordResult.Conflict
                    || trackerResult == TraderPartyTransferRecordResult.CapacityExceeded)
                {
                    detail = "trader-party-chunk-tracker-" + trackerResult.ToString().ToLowerInvariant();
                    LatchReplicationTraderPartyFailure(transfer.EventId, transfer.Generation, detail);
                    return false;
                }
                if (trackerResult == TraderPartyTransferRecordResult.Stale)
                {
                    detail = "ok trader-party-chunk-tracker-stale";
                    return true;
                }
                transfer.Chunks[index] = data;
                transfer.Received[index] = true;
                transfer.ReceivedCount++;
                transfer.ReceivedBytes += data.Length;
                transfer.LastTouchedRealtime = Time.realtimeSinceStartup;
                if (transfer.ReceivedBytes > transfer.ExpectedBytes)
                {
                    detail = "trader-party-chunk-overflow";
                    return false;
                }
            }
            detail = "ok trader-party-chunk";
            return true;
        }

        private static bool TryApplyReplicationTraderPartyCommit(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadTraderPartyWire(delta.Detail, out var transferId)
                || !TryReadTraderPartyText(delta.Detail, "eventIdB64", out var eventId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "generation", out var generation)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "partyGeneration", out var partyGeneration)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "sha256", out var hash))
            {
                detail = "trader-party-commit-malformed";
                return false;
            }
            var failureKey = eventId + "|" + generation.ToString(CultureInfo.InvariantCulture);
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationClientTraderPartyFailureLatches.Contains(failureKey))
                {
                    detail = "trader-party-revision-failed-closed; full-session-resync-required";
                    return false;
                }
            }

            ClientTraderPartyTransfer transfer;
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationClientTraderPartyTransfers.TryGetValue(transferId, out transfer!))
                {
                    if (ReplicationClientTerminalTraderParties.Contains(eventId))
                    {
                        detail = "ok trader-party-commit-terminal";
                        return true;
                    }
                    if (ReplicationClientTraderPartyGenerationByEvent.TryGetValue(eventId, out var applied) && applied >= generation)
                    {
                        if (applied > generation)
                        {
                            detail = "ok trader-party-commit-stale";
                            return true;
                        }
                        var sameHash = ReplicationClientTraderPartyHashByEvent.TryGetValue(eventId, out var appliedHash)
                            && string.Equals(appliedHash, hash, StringComparison.OrdinalIgnoreCase);
                        detail = sameHash ? "ok trader-party-commit-already-applied" : "trader-party-same-generation-hash-conflict";
                        if (!sameHash) LatchReplicationTraderPartyFailure(eventId, generation, detail);
                        return sameHash;
                    }
                    detail = "trader-party-commit-begin-missing-retry";
                    return false;
                }
                if (transfer.EventId != eventId || transfer.Generation != generation
                    || transfer.PartyGeneration != partyGeneration
                    || !string.Equals(transfer.Hash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    detail = "trader-party-commit-conflict";
                    LatchReplicationTraderPartyFailure(eventId, generation, detail);
                    return false;
                }
                if (transfer.ReceivedCount != transfer.ChunkCount || transfer.ReceivedBytes != transfer.ExpectedBytes)
                {
                    detail = "trader-party-commit-incomplete-retry received="
                        + transfer.ReceivedCount.ToString(CultureInfo.InvariantCulture)
                        + "/" + transfer.ChunkCount.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            var bundle = new byte[transfer.ExpectedBytes];
            var offset = 0;
            for (var i = 0; i < transfer.Chunks.Length; i++)
            {
                var chunk = transfer.Chunks[i];
                if (chunk == null || offset + chunk.Length > bundle.Length)
                {
                    detail = "trader-party-commit-chunk-gap";
                    return false;
                }
                Buffer.BlockCopy(chunk, 0, bundle, offset, chunk.Length);
                offset += chunk.Length;
            }
            if (offset != bundle.Length || !string.Equals(ComputeTraderPartySha256(bundle), transfer.Hash, StringComparison.OrdinalIgnoreCase))
            {
                detail = "trader-party-commit-hash-mismatch";
                LatchReplicationTraderPartyFailure(eventId, generation, detail);
                return false;
            }
            var trackerCommit = new TraderPartyTransferCommit(
                transferId,
                eventId,
                partyGeneration,
                generation,
                transfer.ChunkCount,
                transfer.ExpectedBytes,
                transfer.MemberNetworkIds,
                hash);
            var trackerCommitResult = ReplicationClientTraderPartyTracker.RecordCommit(
                trackerCommit,
                TraderPartyTrackerNowMilliseconds());
            if (trackerCommitResult == TraderPartyTransferRecordResult.Stale)
            {
                lock (ReplicationTraderPartyLock) ReplicationClientTraderPartyTransfers.Remove(transferId);
                detail = "ok trader-party-commit-stale-tombstone";
                return true;
            }
            if ((trackerCommitResult != TraderPartyTransferRecordResult.Accepted
                    && trackerCommitResult != TraderPartyTransferRecordResult.Duplicate)
                || !ReplicationClientTraderPartyTracker.TryTakeComplete(transferId, out var trackedCompletion)
                || trackedCompletion == null
                || !trackedCompletion.GetPayloadCopy().SequenceEqual(bundle))
            {
                detail = "trader-party-commit-tracker-" + trackerCommitResult.ToString().ToLowerInvariant();
                LatchReplicationTraderPartyFailure(eventId, generation, detail);
                return false;
            }
            if (!TryDeserializeAndApplyTraderParty(transfer, bundle, out detail))
            {
                LatchReplicationTraderPartyFailure(eventId, generation, detail);
                return false;
            }

            lock (ReplicationTraderPartyLock)
            {
                ReplicationClientTraderPartyGenerationByEvent[eventId] = Math.Max(
                    generation,
                    ReplicationClientTraderPartyGenerationByEvent.TryGetValue(eventId, out var current) ? current : 0);
                ReplicationClientTraderPartyHashByEvent[eventId] = transfer.Hash;
                ReplicationClientTraderPartyTransfers.Remove(transferId);
                TryFinalizeClientReplicationTraderParty(eventId);
            }
            detail = "ok trader-party-commit " + detail;
            return true;
        }

        private static void LatchReplicationTraderPartyFailure(string eventId, int generation, string reason)
        {
            var key = eventId + "|" + generation.ToString(CultureInfo.InvariantCulture);
            lock (ReplicationTraderPartyLock)
                if (!ReplicationClientTraderPartyFailureLatches.Add(key)) return;
            instance?.LogReplicationWarning("Going Cooperative trader party failed closed eventId=" + eventId
                + " revision=" + generation.ToString(CultureInfo.InvariantCulture)
                + " reason=" + reason + "; scheduling one full session resync.");
            replicationTraderPartyRecoveryRequested = true;
            replicationTraderPartyRecoveryReason = reason;
        }

        private static bool TryDeserializeAndApplyTraderParty(ClientTraderPartyTransfer transfer, byte[] bundle, out string detail)
        {
            detail = string.Empty;
            try
            {
                if (!TryDecompressTraderPartyBundle(bundle, out var uncompressed, out detail)) return false;
                List<TraderPartyAgentBinding> descriptors;
                byte[] data;
                byte[] references;
                using (var stream = new MemoryStream(uncompressed, writable: false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != ReplicationTraderPartyBundleMagic
                        || reader.ReadInt32() != ReplicationTraderPartyBundleVersion
                        || reader.ReadString() != transfer.GameAssemblyMvid
                        || reader.ReadString() != transfer.EventId
                        || reader.ReadInt32() != transfer.PartyGeneration
                        || reader.ReadInt32() != transfer.Generation)
                    {
                        detail = "bundle-header";
                        return false;
                    }
                    var descriptorCount = reader.ReadInt32();
                    if (descriptorCount < 1 || descriptorCount > ReplicationTraderPartyMaxHumanoids + ReplicationTraderPartyMaxAnimals)
                    {
                        detail = "descriptor-count";
                        return false;
                    }
                    descriptors = new List<TraderPartyAgentBinding>(descriptorCount);
                    for (var i = 0; i < descriptorCount; i++) descriptors.Add(ReadTraderPartyDescriptor(reader, transfer));
                    data = ReadBoundedTraderPartyBytes(reader, out var dataHash);
                    references = ReadBoundedTraderPartyBytes(reader, out var referenceHash);
                    if (!string.Equals(dataHash, ComputeTraderPartySha256(data), StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(referenceHash, ComputeTraderPartySha256(references), StringComparison.OrdinalIgnoreCase))
                    {
                        detail = "bundle-component-hash";
                        return false;
                    }
                    if (stream.Position != stream.Length)
                    {
                        detail = "bundle-trailing-bytes";
                        return false;
                    }
                }

                ValidateClientTraderPartyManifest(descriptors, transfer);
                List<CreatureBase> creatures;
                using (var deserializer = new FVDeserializer(ReplicationTraderPartyWriterId, data))
                {
                    deserializer.ReadReferences(references);
                    creatures = deserializer.ReadObjectList("creatures", new List<CreatureBase>());
                }
                if (creatures.Count != descriptors.Count
                    || creatures.Count(creature => creature is HumanoidInstance) != transfer.ExpectedHumanoids
                    || creatures.Count(creature => creature is AnimalInstance) != transfer.ExpectedAnimals)
                {
                    detail = "deserialized-count";
                    return false;
                }

                return ApplyDeserializedTraderParty(transfer, descriptors, creatures, out detail);
            }
            catch (Exception ex)
            {
                detail = "deserialize=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        private static bool TryDecompressTraderPartyBundle(byte[] bundle, out byte[] uncompressed, out string detail)
        {
            uncompressed = Array.Empty<byte>();
            detail = string.Empty;
            try
            {
                using var input = new MemoryStream(bundle, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = gzip.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    if (output.Length + read > ReplicationTraderPartyMaxUncompressedBundleBytes)
                    {
                        detail = "bundle-decompression-cap";
                        return false;
                    }
                    output.Write(buffer, 0, read);
                }
                uncompressed = output.ToArray();
                return uncompressed.Length > 0;
            }
            catch (Exception ex)
            {
                detail = "bundle-decompression=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        private static TraderPartyAgentBinding ReadTraderPartyDescriptor(BinaryReader reader, ClientTraderPartyTransfer transfer)
        {
            var descriptor = new TraderPartyAgentBinding
            {
                EventId = transfer.EventId,
                Generation = transfer.PartyGeneration,
                NetworkId = ReadBoundedTraderPartyString(reader),
                Role = ReadBoundedTraderPartyString(reader),
                OwnerNetworkId = ReadBoundedTraderPartyString(reader),
                PriorStableEntityId = ReadBoundedTraderPartyString(reader),
                HostUniqueId = reader.ReadInt32(),
                Fingerprint = ReadBoundedTraderPartyString(reader),
                IsAnimal = reader.ReadBoolean(),
                Detached = reader.ReadBoolean(),
                Roped = reader.ReadBoolean()
            };
            if (!descriptor.NetworkId.StartsWith("event-agent:" + transfer.EventId + ":", StringComparison.Ordinal)
                || descriptor.Fingerprint.Length != 64)
                throw new InvalidDataException("Invalid trader party descriptor identity.");
            return descriptor;
        }

        private static string ReadBoundedTraderPartyString(BinaryReader reader)
        {
            var value = reader.ReadString();
            if (value.Length > ReplicationTraderPartyMaxStringChars) throw new InvalidDataException("Trader party string cap exceeded.");
            return value;
        }

        private static byte[] ReadBoundedTraderPartyBytes(BinaryReader reader, out string hash)
        {
            var length = reader.ReadInt32();
            hash = ReadBoundedTraderPartyString(reader);
            if (length < 0 || length > ReplicationTraderPartyMaxUncompressedBundleBytes
                || !IsTraderPartySha256(hash)) throw new InvalidDataException("Trader party byte/hash cap exceeded.");
            var value = reader.ReadBytes(length);
            if (value.Length != length) throw new EndOfStreamException();
            return value;
        }

        private static bool ApplyDeserializedTraderParty(
            ClientTraderPartyTransfer transfer,
            List<TraderPartyAgentBinding> descriptors,
            List<CreatureBase> creatures,
            out string detail)
        {
            detail = string.Empty;
            var newHumanoids = new List<HumanoidInstance>();
            var newAnimals = new List<AnimalInstance>();
            var plannedBindings = new List<TraderPartyAgentBinding>();
            var previousBindings = new List<TraderPartyAgentBinding>();
            var registeredBindings = new List<TraderPartyAgentBinding>();
            var actorSnapshots = new List<TraderPartyActorSnapshot>();
            var petSnapshots = new List<TraderPartyPetSnapshot>();
            try
            {
                if (!TryPreflightClientTraderPartyAdoption(
                    descriptors,
                    out var adoptionActors,
                    out var importedEvent,
                    out detail)) return false;

                var suppressedIds = BuildSuppressedTraderPartyNetworkIds(descriptors);
                if (suppressedIds.Count > 0)
                {
                    // The Core tracker should have dominated this transfer before apply.
                    // Reaching mutation with a tombstoned member is an integrity fault,
                    // never permission to destructively prune a partially staged graph.
                    throw new InvalidDataException("Manifest contains a precommit tombstoned member.");
                }

                var manifestIds = new HashSet<string>(descriptors.Select(descriptor => descriptor.NetworkId), StringComparer.Ordinal);
                lock (ReplicationTraderPartyLock)
                    if (ReplicationTraderPartyBindingByObject.Values.Any(binding =>
                        string.Equals(binding.EventId, transfer.EventId, StringComparison.Ordinal)
                        && binding.Generation == transfer.PartyGeneration
                        && !manifestIds.Contains(binding.NetworkId)))
                        throw new InvalidDataException("Manifest omission requires an explicit actor tombstone.");

                var allocatedLocalIds = new HashSet<int>();
                for (var i = 0; i < descriptors.Count; i++)
                {
                    var descriptor = descriptors[i];
                    var candidate = creatures[i];
                    if (candidate == null || descriptor.IsAnimal != (candidate is AnimalInstance))
                        throw new InvalidDataException("Creature descriptor type mismatch.");
                    if (candidate.UniqueId != descriptor.HostUniqueId
                        || !string.Equals(BuildTraderPartyFingerprint(candidate), descriptor.Fingerprint, StringComparison.Ordinal))
                        throw new InvalidDataException("Serialized actor identity mismatch.");

                    if (TryFindExistingTraderPartyActor(descriptor, adoptionActors, out var existing) && existing != null)
                    {
                        descriptor.Agent = existing;
                        descriptor.AdoptedExisting = true;
                        descriptor.SpawnedByReplication = false;
                        if (!TryValidateClientTraderPartyUniqueId(existing, out var uidDetail))
                            throw new InvalidDataException("Adopted actor UID is not globally unique: " + uidDetail);
                        actorSnapshots.Add(new TraderPartyActorSnapshot
                        {
                            Actor = existing,
                            Role = descriptor.Role,
                            OldOwner = GetHostPartyOwner(existing),
                            OldRope = existing.RopedTo()
                        });
                        lock (ReplicationTraderPartyLock)
                            if (ReplicationTraderPartyBindingByObject.TryGetValue(existing, out var previousBinding))
                                previousBindings.Add(previousBinding);
                    }
                    else
                    {
                        if (descriptor.Detached)
                            throw new InvalidDataException("Detached trader stock must adopt an existing global actor.");
                        ResetTraderPartyLocalUniqueId(candidate);
                        if (!TryValidateAllocatedTraderPartyUniqueId(candidate.UniqueId, allocatedLocalIds, out var uidDetail))
                            throw new InvalidDataException("Allocated actor UID conflicts globally: " + uidDetail);
                        descriptor.Agent = candidate;
                        descriptor.SpawnedByReplication = true;
                    }
                    plannedBindings.Add(descriptor);
                }

                var traderById = plannedBindings
                    .Where(binding => string.Equals(binding.Role, "Trader", StringComparison.Ordinal) && binding.Agent is HumanoidInstance)
                    .ToDictionary(binding => binding.NetworkId, binding => (HumanoidInstance)binding.Agent, StringComparer.Ordinal);
                if (traderById.Count == 0) throw new InvalidDataException("Trader descriptor missing.");

                var desiredOwners = new Dictionary<TraderPartyAgentBinding, CreatureBase?>();
                var ownersToSnapshot = new List<CreatureBase?>();
                for (var i = 0; i < plannedBindings.Count; i++)
                {
                    var binding = plannedBindings[i];
                    var owner = ResolveTraderPartyOwner(binding.OwnerNetworkId, traderById);
                    if (!string.IsNullOrEmpty(binding.OwnerNetworkId) && owner == null)
                        throw new InvalidDataException("Trader stock owner identity was not resolved.");
                    var ownerIsEventTrader = traderById.ContainsKey(binding.OwnerNetworkId);
                    if (!binding.Detached && IsTraderPartyStockRole(binding.Role) && !ownerIsEventTrader)
                        throw new InvalidDataException("Attached stock actor owner is not a same-event trader.");
                    if (binding.Detached && ownerIsEventTrader)
                        throw new InvalidDataException("Detached stock actor still references a same-event trader.");
                    desiredOwners[binding] = owner;
                    ownersToSnapshot.Add(owner);
                    if (binding.AdoptedExisting) ownersToSnapshot.Add(GetHostPartyOwner(binding.Agent));
                }
                petSnapshots = CaptureTraderPartyPetSnapshots(ownersToSnapshot.ToArray());

                replicationTraderPartyApplicationDepth++;

                // New deserialized actors need durable owner fields before native load,
                // but ropes wait until every required view/GoapAgent exists.
                for (var i = 0; i < plannedBindings.Count; i++)
                {
                    var binding = plannedBindings[i];
                    if (!binding.SpawnedByReplication || !IsTraderPartyStockRole(binding.Role)) continue;
                    var owner = desiredOwners[binding];
                    if (binding.Agent is AnimalInstance animal) animal.AssignPetOwner(owner);
                    else if (binding.Agent is HumanoidInstance prisoner)
                    {
                        if (prisoner.PrisonerBehaviour == null)
                            throw new InvalidDataException("Stock prisoner behaviour missing before spawn.");
                        prisoner.PrisonerBehaviour.Owner = owner as HumanoidInstance;
                    }
                }

                // Every humanoid is registered and spawned before any animal. Track it
                // before the first mutating native call so rollback covers partial throws.
                for (var i = 0; i < plannedBindings.Count; i++)
                {
                    var binding = plannedBindings[i];
                    if (!binding.SpawnedByReplication || binding.Agent is not HumanoidInstance humanoid) continue;
                    newHumanoids.Add(humanoid);
                    GlobalSaveController.CurrentVillageData.AddNPC(humanoid);
                    LoadReplicationTraderPartyNpc(humanoid);
                    if (!GlobalSaveController.CurrentVillageData.NPCs.Contains(humanoid)
                        || NPCManager.Instance.GetView(humanoid) == null)
                        throw new InvalidDataException("Native humanoid spawn postcondition failed.");
                }

                for (var i = 0; i < plannedBindings.Count; i++)
                {
                    var binding = plannedBindings[i];
                    if (!binding.SpawnedByReplication || binding.Agent is not AnimalInstance animal) continue;
                    newAnimals.Add(animal);
                    GlobalSaveController.CurrentVillageData.AddAnimal(animal);
                    AnimalManager.Instance.InstantiateAnimal(animal, true);
                    if (!GlobalSaveController.CurrentVillageData.Animals.Contains(animal)
                        || AnimalManager.Instance.GetView(animal) == null)
                        throw new InvalidDataException("Native animal spawn postcondition failed.");
                }

                for (var i = 0; i < plannedBindings.Count; i++)
                {
                    var actor = plannedBindings[i].Agent;
                    if (actor is HumanoidInstance humanoid
                        && (!GlobalSaveController.CurrentVillageData.NPCs.Contains(humanoid)
                            || NPCManager.Instance.GetView(humanoid) == null))
                        throw new InvalidDataException("Trader party humanoid view/save membership missing.");
                    if (actor is AnimalInstance animal
                        && (!GlobalSaveController.CurrentVillageData.Animals.Contains(animal)
                            || AnimalManager.Instance.GetView(animal) == null))
                        throw new InvalidDataException("Trader party animal view/save membership missing.");
                }

                // Ownership and ropes are the only live semantic merge in v1. FV
                // inventory/storage from a revision never replaces an integrated actor.
                for (var i = 0; i < plannedBindings.Count; i++)
                {
                    var binding = plannedBindings[i];
                    if (!IsTraderPartyStockRole(binding.Role)) continue;
                    var animalType = binding.Agent is AnimalInstance animal ? (int)animal.AnimalType : -1;
                    ApplyTraderPartyOwnershipState(
                        binding.Agent,
                        binding.Role,
                        desiredOwners[binding],
                        binding.Detached,
                        binding.Roped,
                        animalType);
                }

                // Commit identity only after every native spawn and reversible owner
                // mutation has passed its postconditions.
                lock (ReplicationTraderPartyLock)
                {
                    for (var i = 0; i < plannedBindings.Count; i++)
                    {
                        RegisterTraderPartyBinding(plannedBindings[i]);
                        registeredBindings.Add(plannedBindings[i]);
                    }
                }

                for (var i = 0; i < plannedBindings.Count; i++)
                    if (!TryValidateClientTraderPartyUniqueId(plannedBindings[i].Agent, out var finalUidDetail))
                        throw new InvalidDataException("Committed actor UID validation failed: " + finalUidDetail);

                // Quarantine is last. There are no destructive manifest-prune calls
                // after it; actor removal is exclusively an explicit tombstone lane.
                if (!TryQuarantineImportedClientTraderEvent(transfer.EventId, importedEvent, out var quarantineDetail))
                    throw new InvalidOperationException("Imported trader quarantine failed: " + quarantineDetail);

                detail = "spawnedHumanoids=" + newHumanoids.Count.ToString(CultureInfo.InvariantCulture)
                    + " spawnedAnimals=" + newAnimals.Count.ToString(CultureInfo.InvariantCulture)
                    + " adopted=" + plannedBindings.Count(binding => binding.AdoptedExisting).ToString(CultureInfo.InvariantCulture)
                    + " inventoryHotMerge=false";
                return true;
            }
            catch (Exception ex)
            {
                if (registeredBindings.Count > 0)
                {
                    try
                    {
                        lock (ReplicationTraderPartyLock)
                        {
                            for (var i = registeredBindings.Count - 1; i >= 0; i--)
                                UnregisterTraderPartyBinding(registeredBindings[i]);
                            foreach (var previousBinding in previousBindings.Distinct())
                            {
                                try { RegisterTraderPartyBinding(previousBinding); }
                                catch (Exception restoreEx)
                                {
                                    instance?.LogReplicationWarning("Going Cooperative trader binding rollback restore failed error="
                                        + restoreEx.GetType().Name + ":" + restoreEx.Message);
                                }
                            }
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        instance?.LogReplicationWarning("Going Cooperative trader binding rollback failed error="
                            + rollbackEx.GetType().Name + ":" + rollbackEx.Message);
                    }
                }
                for (var i = actorSnapshots.Count - 1; i >= 0; i--)
                {
                    var snapshot = actorSnapshots[i];
                    try
                    {
                        RestoreTraderPartyOwnershipState(
                            snapshot.Actor,
                            snapshot.Role,
                            snapshot.OldOwner,
                            snapshot.OldRope,
                            new List<TraderPartyPetSnapshot>());
                    }
                    catch { }
                }
                try { RestoreTraderPartyPetSnapshots(petSnapshots); } catch { }
                for (var i = newAnimals.Count - 1; i >= 0; i--)
                {
                    try { AnimalManager.Instance.RemoveAnimal(newAnimals[i], false); } catch { }
                    try
                    {
                        if (GlobalSaveController.CurrentVillageData.Animals.Contains(newAnimals[i]))
                            GlobalSaveController.CurrentVillageData.Animals.Remove(newAnimals[i]);
                    }
                    catch { }
                }
                for (var i = newHumanoids.Count - 1; i >= 0; i--)
                {
                    try { newHumanoids[i].DestroyStorage(); } catch { }
                    try { newHumanoids[i].DestroyEquipment(); } catch { }
                    try { NPCController.Instance.RemoveNPC(newHumanoids[i]); } catch { }
                    try
                    {
                        if (GlobalSaveController.CurrentVillageData.NPCs.Contains(newHumanoids[i]))
                            GlobalSaveController.CurrentVillageData.NPCs.Remove(newHumanoids[i]);
                    }
                    catch { }
                }
                detail = "apply=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
            finally
            {
                if (replicationTraderPartyApplicationDepth > 0) replicationTraderPartyApplicationDepth--;
            }
        }

        private static HashSet<string> BuildSuppressedTraderPartyNetworkIds(List<TraderPartyAgentBinding> descriptors)
        {
            var suppressed = new HashSet<string>(StringComparer.Ordinal);
            lock (ReplicationTraderPartyLock)
            {
                for (var i = 0; i < descriptors.Count; i++)
                {
                    var descriptor = descriptors[i];
                    if (ReplicationClientTraderPartyTombstones.Contains(
                        FormatTraderPartyTombstoneKey(descriptor.EventId, descriptor.Generation, descriptor.NetworkId)))
                        suppressed.Add(descriptor.NetworkId);
                }
            }
            var activeTraderIds = new HashSet<string>(descriptors
                .Where(descriptor => string.Equals(descriptor.Role, "Trader", StringComparison.Ordinal)
                    && !suppressed.Contains(descriptor.NetworkId))
                .Select(descriptor => descriptor.NetworkId), StringComparer.Ordinal);
            if (activeTraderIds.Count == 0)
            {
                for (var i = 0; i < descriptors.Count; i++) suppressed.Add(descriptors[i].NetworkId);
                return suppressed;
            }
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                if (!descriptor.Detached
                    && IsTraderPartyStockRole(descriptor.Role)
                    && descriptor.OwnerNetworkId.StartsWith("event-agent:", StringComparison.Ordinal)
                    && !activeTraderIds.Contains(descriptor.OwnerNetworkId)) suppressed.Add(descriptor.NetworkId);
            }
            return suppressed;
        }

        private static CreatureBase? ResolveTraderPartyOwner(
            string ownerNetworkId,
            Dictionary<string, HumanoidInstance> traders)
        {
            if (string.IsNullOrEmpty(ownerNetworkId)) return null;
            if (traders.TryGetValue(ownerNetworkId, out var owner)) return owner;
            return TryFindReplicationAgentOwnerByEntityId(ownerNetworkId, out var genericOwner, out _)
                ? genericOwner as CreatureBase
                : null;
        }

        private static bool IsClientSameEventTraderOwner(string eventId, CreatureBase? owner)
        {
            if (owner == null) return false;
            lock (ReplicationTraderPartyLock)
                return ReplicationTraderPartyBindingByObject.TryGetValue(owner, out var ownerBinding)
                    && string.Equals(ownerBinding.EventId, eventId, StringComparison.Ordinal)
                    && string.Equals(ownerBinding.Role, "Trader", StringComparison.Ordinal)
                    && !ownerBinding.Tombstoned;
        }

        private static bool TryPreflightClientTraderPartyAdoption(
            List<TraderPartyAgentBinding> descriptors,
            out Dictionary<string, CreatureBase> adoptionActors,
            out object? importedEvent,
            out string detail)
        {
            adoptionActors = new Dictionary<string, CreatureBase>(StringComparer.Ordinal);
            importedEvent = null;
            try
            {
                var eventOwnedDescriptors = descriptors.Where(descriptor => !descriptor.Detached).ToList();
                var system = NSMedieval.GameEventSystem.GameEventSystem.Instance;
                var runningTraderEvents = system.RunningEvents
                    .Where(IsReplicationTraderEventInstance)
                    .Where(nativeEvent =>
                    {
                        lock (ReplicationTraderPartyLock) return !ReplicationAdoptedTraderEvents.TryGetValue(nativeEvent, out _);
                    })
                    .ToList();
                if (runningTraderEvents.Count == 0)
                {
                    detail = "no-native-party";
                    return true;
                }

                var exact = new List<KeyValuePair<object, Dictionary<string, CreatureBase>>>();
                for (var i = 0; i < runningTraderEvents.Count; i++)
                {
                    if (TryMatchImportedTraderEvent(runningTraderEvents[i], eventOwnedDescriptors, out var actors))
                        exact.Add(new KeyValuePair<object, Dictionary<string, CreatureBase>>(runningTraderEvents[i], actors));
                }
                if (exact.Count != 1 || runningTraderEvents.Count != 1)
                {
                    detail = "native-trader-party-conflict running=" + runningTraderEvents.Count.ToString(CultureInfo.InvariantCulture)
                        + " exact=" + exact.Count.ToString(CultureInfo.InvariantCulture)
                        + "; full-session-resync-required";
                    return false;
                }
                importedEvent = exact[0].Key;
                adoptionActors = exact[0].Value;
                detail = "exact-native-attached-party detachedGlobal="
                    + (descriptors.Count - eventOwnedDescriptors.Count).ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = "native-trader-preflight=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        private static bool TryMatchImportedTraderEvent(
            object nativeEvent,
            List<TraderPartyAgentBinding> descriptors,
            out Dictionary<string, CreatureBase> actors)
        {
            actors = new Dictionary<string, CreatureBase>(StringComparer.Ordinal);
            var byRole = new Dictionary<string, List<CreatureBase>>(StringComparer.Ordinal)
            {
                ["Trader"] = ReadCreatureList(nativeEvent, "traders", "Trader"),
                ["Guard"] = ReadCreatureList(nativeEvent, "crowd", "Guards"),
                ["StockAnimal"] = new List<CreatureBase>(),
                ["StockPrisoner"] = new List<CreatureBase>()
            };
            var traders = byRole["Trader"].OfType<HumanoidInstance>().ToList();
            foreach (var animal in GlobalSaveController.CurrentVillageData.Animals)
                if (animal?.PetOwner != null && traders.Contains(animal.PetOwner)) byRole["StockAnimal"].Add(animal);
            var npcs = GlobalSaveController.CurrentVillageData.NPCs;
            for (var i = 0; i < npcs.Count; i++)
                if (npcs[i]?.PrisonerBehaviour?.Owner != null && traders.Contains(npcs[i].PrisonerBehaviour.Owner))
                    byRole["StockPrisoner"].Add(npcs[i]);

            if (byRole.Sum(pair => pair.Value.Count) != descriptors.Count) return false;
            var used = new HashSet<object>(ExternalReferenceComparer.Instance);
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                if (!byRole.TryGetValue(descriptor.Role, out var roleActors)) return false;
                var matches = roleActors.Where(candidate => candidate != null
                    && candidate.UniqueId == descriptor.HostUniqueId
                    && string.Equals(BuildTraderPartyFingerprint(candidate), descriptor.Fingerprint, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count != 1 || !used.Add(matches[0])) return false;
                actors[descriptor.NetworkId] = matches[0];
            }
            return actors.Count == descriptors.Count;
        }

        private static bool TryBuildNativeTraderPartyIdentity(
            object nativeEvent,
            out string fingerprint,
            out int memberCount,
            out List<CreatureBase> actors)
        {
            fingerprint = string.Empty;
            memberCount = 0;
            var identityActors = new List<CreatureBase>();
            actors = identityActors;
            if (nativeEvent == null || !IsReplicationTraderEventInstance(nativeEvent)) return false;
            var entries = new List<string>();
            var seen = new HashSet<object>(ExternalReferenceComparer.Instance);
            var traders = ReadCreatureList(nativeEvent, "traders", "Trader").OfType<HumanoidInstance>().ToList();
            var guards = ReadCreatureList(nativeEvent, "crowd", "Guards").OfType<HumanoidInstance>().ToList();

            bool Add(string role, CreatureBase? actor)
            {
                if (actor == null || !seen.Add(actor) || actor.UniqueId <= 0) return false;
                identityActors.Add(actor);
                entries.Add(role + "|" + actor.UniqueId.ToString(CultureInfo.InvariantCulture)
                    + "|" + BuildTraderPartyFingerprint(actor));
                return true;
            }

            for (var i = 0; i < traders.Count; i++) if (!Add("Trader", traders[i])) return false;
            for (var i = 0; i < guards.Count; i++) if (!Add("Guard", guards[i])) return false;
            foreach (var animal in GlobalSaveController.CurrentVillageData.Animals)
                if (animal?.PetOwner != null && traders.Contains(animal.PetOwner) && !Add("StockAnimal", animal)) return false;
            foreach (var npc in GlobalSaveController.CurrentVillageData.NPCs)
                if (npc?.PrisonerBehaviour?.Owner != null
                    && traders.Contains(npc.PrisonerBehaviour.Owner)
                    && !Add("StockPrisoner", npc)) return false;

            if (traders.Count < 1
                || identityActors.Count < 1
                || identityActors.Count > ReplicationTraderPartyMaxHumanoids + ReplicationTraderPartyMaxAnimals)
            {
                identityActors.Clear();
                entries.Clear();
                return false;
            }
            entries.Sort(StringComparer.Ordinal);
            memberCount = entries.Count;
            fingerprint = ComputeTraderPartySha256(Encoding.UTF8.GetBytes(string.Join("\n", entries)));
            return IsTraderPartySha256(fingerprint);
        }

        private static bool TryFindExistingTraderPartyActor(
            TraderPartyAgentBinding descriptor,
            Dictionary<string, CreatureBase> adoptionActors,
            out CreatureBase? actor)
        {
            actor = null;
            CreatureBase? ValidateAdoption(CreatureBase? candidate, string source)
            {
                if (candidate == null) return null;
                if (descriptor.IsAnimal != (candidate is AnimalInstance)
                    || !string.Equals(BuildTraderPartyFingerprint(candidate), descriptor.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("Trader party " + source + " actor identity conflict.");
                lock (ReplicationTraderPartyLock)
                {
                    if (ReplicationTraderPartyBindingByObject.TryGetValue(candidate, out var candidateBinding)
                        && !string.Equals(candidateBinding.NetworkId, descriptor.NetworkId, StringComparison.Ordinal))
                        throw new InvalidDataException("Trader party " + source + " actor already maps to another identity.");
                }
                return candidate;
            }

            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationTraderPartyObjectByNetworkId.TryGetValue(descriptor.NetworkId, out var mapped)
                    && mapped is CreatureBase mappedCreature)
                {
                    actor = ValidateAdoption(mappedCreature, "mapped");
                    return actor != null;
                }
            }

            if (!string.IsNullOrWhiteSpace(descriptor.PriorStableEntityId)
                && TryFindReplicationAgentOwnerByEntityId(descriptor.PriorStableEntityId, out var priorOwner, out _))
            {
                actor = ValidateAdoption(priorOwner as CreatureBase, "prior-stable");
                if (actor != null) return true;
            }

            var adoptionKey = FormatTraderPartyDetachedAdoptionKey(
                descriptor.HostUniqueId, descriptor.Fingerprint, descriptor.Role);
            lock (ReplicationTraderPartyLock)
                if (ReplicationDetachedTraderPartyAdoptions.TryGetValue(adoptionKey, out var weak)
                    && weak.Target is CreatureBase detachedActor)
                {
                    actor = ValidateAdoption(detachedActor, "same-world-orphan");
                    if (actor != null) return true;
                }

            if (!adoptionActors.TryGetValue(descriptor.NetworkId, out var adopted)) return false;
            actor = ValidateAdoption(adopted, "native-import");
            return actor != null;
        }

        private static void ResetTraderPartyLocalUniqueId(CreatureBase creature)
        {
            var field = AccessTools.Field(typeof(CreatureBase), "uniqueId")
                ?? throw new MissingFieldException(typeof(CreatureBase).FullName, "uniqueId");
            // Never call ResetUniqueId/ReassignUniqueId here: the deserialized host ID
            // may belong to a different live client object and those APIs release it.
            field.SetValue(creature, 0);
            _ = creature.UniqueId;
        }

        private static string FormatTraderPartyDetachedAdoptionKey(int hostUniqueId, string fingerprint, string role)
        {
            return hostUniqueId.ToString(CultureInfo.InvariantCulture) + "|" + fingerprint + "|" + role;
        }

        private static bool TryValidateClientTraderPartyUniqueId(CreatureBase actor, out string detail)
        {
            var all = new HashSet<object>(ExternalReferenceComparer.Instance);
            var byId = new Dictionary<int, List<CreatureBase>>();
            void Add(CreatureBase? creature)
            {
                if (creature == null || !all.Add(creature)) return;
                var id = creature.UniqueId;
                if (!byId.TryGetValue(id, out var values))
                {
                    values = new List<CreatureBase>();
                    byId[id] = values;
                }
                values.Add(creature);
            }
            foreach (var worker in GlobalSaveController.CurrentVillageData.Workers) Add(worker);
            foreach (var npc in GlobalSaveController.CurrentVillageData.NPCs) Add(npc);
            foreach (var animal in GlobalSaveController.CurrentVillageData.Animals) Add(animal);
            var collision = byId.FirstOrDefault(pair => pair.Key <= 0 || pair.Value.Count != 1);
            if (collision.Value != null)
            {
                detail = "global-uid-collision id=" + collision.Key.ToString(CultureInfo.InvariantCulture)
                    + " count=" + collision.Value.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!byId.TryGetValue(actor.UniqueId, out var match)
                || match.Count != 1
                || !ReferenceEquals(match[0], actor))
            {
                detail = "adoption-uid-not-globally-unique id=" + actor.UniqueId.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            detail = "ok uid=" + actor.UniqueId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryValidateAllocatedTraderPartyUniqueId(
            int uniqueId,
            HashSet<int> allocatedLocalIds,
            out string detail)
        {
            if (uniqueId <= 0 || !allocatedLocalIds.Add(uniqueId))
            {
                detail = "allocated-uid-invalid-or-duplicate id=" + uniqueId.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            var all = new HashSet<object>(ExternalReferenceComparer.Instance);
            void Check(CreatureBase? actor)
            {
                if (actor != null) all.Add(actor);
            }
            foreach (var worker in GlobalSaveController.CurrentVillageData.Workers) Check(worker);
            foreach (var npc in GlobalSaveController.CurrentVillageData.NPCs) Check(npc);
            foreach (var animal in GlobalSaveController.CurrentVillageData.Animals) Check(animal);
            if (all.Cast<CreatureBase>().Any(actor => actor.UniqueId == uniqueId))
            {
                detail = "allocated-uid-already-live id=" + uniqueId.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            detail = "ok uid=" + uniqueId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static List<TraderPartyPetSnapshot> CaptureTraderPartyPetSnapshots(params CreatureBase?[] owners)
        {
            var snapshots = new List<TraderPartyPetSnapshot>();
            var seen = new HashSet<object>(ExternalReferenceComparer.Instance);
            for (var i = 0; i < owners.Length; i++)
            {
                var owner = owners[i];
                if (owner == null || !seen.Add(owner)) continue;
                snapshots.Add(new TraderPartyPetSnapshot
                {
                    Owner = owner,
                    Pets = owner.Pets == null ? new List<AnimalInstance>() : owner.Pets.ToList(),
                    PetIds = owner.PetsIDs == null ? new List<int>() : owner.PetsIDs.ToList()
                });
            }
            return snapshots;
        }

        private static void RestoreTraderPartyPetSnapshots(List<TraderPartyPetSnapshot> snapshots)
        {
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                snapshot.Owner.Pets?.Clear();
                snapshot.Owner.PetsIDs?.Clear();
                if (snapshot.Owner.Pets != null)
                    for (var j = 0; j < snapshot.Pets.Count; j++) snapshot.Owner.Pets.Add(snapshot.Pets[j]);
                if (snapshot.Owner.PetsIDs != null)
                    for (var j = 0; j < snapshot.PetIds.Count; j++) snapshot.Owner.PetsIDs.Add(snapshot.PetIds[j]);
            }
        }

        private static void ApplyTraderPartyOwnershipState(
            CreatureBase actor,
            string role,
            CreatureBase? owner,
            bool detached,
            bool roped,
            int animalType)
        {
            var effectiveOwner = owner;
            if (!detached && IsTraderPartyStockRole(role) && effectiveOwner == null)
                throw new InvalidDataException("Attached trader stock owner is missing.");
            if (roped && effectiveOwner == null)
                throw new InvalidDataException("Roped trader stock owner is missing.");
            if (actor is AnimalInstance animal)
            {
                var priorOwner = animal.PetOwner;
                if (priorOwner != null && !ReferenceEquals(priorOwner, effectiveOwner)) priorOwner.RemovePet(animal);
                animal.AssignPetOwner(effectiveOwner);
                if (effectiveOwner != null) effectiveOwner.AssignPet(animal);
                if (animalType >= 0) animal.SetAnimalType((NSMedieval.Types.AnimalType)animalType);
                if (!ReferenceEquals(animal.PetOwner, effectiveOwner))
                    throw new InvalidDataException("Trader stock animal owner postcondition failed.");
                if (effectiveOwner != null
                    && (!effectiveOwner.Pets.Contains(animal) || !effectiveOwner.PetsIDs.Contains(animal.UniqueId)))
                    throw new InvalidDataException("Trader stock animal pet set postcondition failed.");
            }
            else if (actor is HumanoidInstance prisoner && string.Equals(role, "StockPrisoner", StringComparison.Ordinal))
            {
                if (prisoner.PrisonerBehaviour == null)
                    throw new InvalidDataException("Trader stock prisoner behaviour is missing.");
                prisoner.PrisonerBehaviour.Owner = effectiveOwner as HumanoidInstance;
                if (!ReferenceEquals(prisoner.PrisonerBehaviour.Owner, effectiveOwner))
                    throw new InvalidDataException("Trader stock prisoner owner postcondition failed.");
            }
            var ropeTarget = roped ? effectiveOwner : null;
            var ropeResult = actor.RopeTo(ropeTarget, false);
            if ((roped && (!ropeResult || !ReferenceEquals(actor.RopedTo(), ropeTarget)))
                || (!roped && actor.RopedTo() != null))
                throw new InvalidDataException("Trader stock rope postcondition failed.");
        }

        private static void RestoreTraderPartyOwnershipState(
            CreatureBase actor,
            string role,
            CreatureBase? oldOwner,
            object? oldRope,
            List<TraderPartyPetSnapshot> petSnapshots)
        {
            if (actor is AnimalInstance animal) animal.AssignPetOwner(oldOwner);
            else if (actor is HumanoidInstance prisoner
                && string.Equals(role, "StockPrisoner", StringComparison.Ordinal)
                && prisoner.PrisonerBehaviour != null) prisoner.PrisonerBehaviour.Owner = oldOwner as HumanoidInstance;
            actor.RopeTo(oldRope as NSMedieval.Goap.IGoapTargetable, false);
            RestoreTraderPartyPetSnapshots(petSnapshots);
        }

        private static bool TryQuarantineImportedClientTraderEvent(string eventId, object? nativeEvent, out string detail)
        {
            detail = string.Empty;
            if (replicationConfigHostMode || nativeEvent == null) return true;
            var markerRegistered = false;
            var registryAbsenceVerified = false;
            try
            {
                var system = NSMedieval.GameEventSystem.GameEventSystem.Instance;
                lock (ReplicationTraderPartyLock)
                {
                    if (ReplicationAdoptedTraderEventById.TryGetValue(eventId, out var existing)
                        && existing.Target is object existingEvent
                        && !ReferenceEquals(existingEvent, nativeEvent))
                    {
                        detail = "event-id-quarantine-collision";
                        return false;
                    }
                    ReplicationAdoptedTraderEvents.Remove(nativeEvent);
                    ReplicationAdoptedTraderEvents.Add(nativeEvent, new TraderPartyQuarantineMarker(eventId));
                    ReplicationAdoptedTraderEventById[eventId] = new WeakReference(nativeEvent);
                    markerRegistered = true;
                }

                // The reversible marker is installed before registry mutation so no
                // lifecycle hook can run between removal and quarantine. Native
                // subscriptions remain untouched unless registry removal succeeds.
                Exception? registryRemovalException = null;
                try { system.RemoveFromRunningEvents((GameEventInstance)nativeEvent); }
                catch (Exception ex) { registryRemovalException = ex; }
                if (system.RunningEvents.Contains((GameEventInstance)nativeEvent))
                {
                    detail = registryRemovalException == null
                        ? "native-event-remains-running"
                        : "native-event-removal=" + registryRemovalException.GetType().Name + ":" + registryRemovalException.Message;
                    ReleaseReplicationTraderEventQuarantine(eventId);
                    return false;
                }
                registryAbsenceVerified = true;
                if (registryRemovalException != null)
                    instance?.LogReplicationWarning("Going Cooperative trader registry removal threw after successful absence eventId="
                        + eventId + " error=" + registryRemovalException.GetType().Name + ":" + registryRemovalException.Message);

                Exception? unsubscribeException = null;
                try
                {
                    var unsubscribe = AccessTools.Method(nativeEvent.GetType(), "Unsubscribe")
                        ?? throw new MissingMethodException(nativeEvent.GetType().FullName, "Unsubscribe");
                    unsubscribe.Invoke(nativeEvent, Array.Empty<object>());
                }
                catch (Exception ex)
                {
                    unsubscribeException = ex;
                    instance?.LogReplicationWarning("Going Cooperative imported trader unsubscribe cleanup failed after quarantine eventId="
                        + eventId + " error=" + ex.GetType().Name + ":" + ex.Message);
                }
                try { AccessTools.Method(nativeEvent.GetType(), "RemoveWarningMessage")?.Invoke(nativeEvent, Array.Empty<object>()); }
                catch (Exception ex)
                {
                    instance?.LogReplicationWarning("Going Cooperative imported trader warning cleanup failed after quarantine eventId="
                        + eventId + " error=" + ex.GetType().Name + ":" + ex.Message);
                }
                if (unsubscribeException != null)
                {
                    // Registry removal is already verified, so never restore this
                    // event to RunningEvents. Keep the weak quarantine marker to
                    // suppress lifecycle callbacks and fail closed: stale native
                    // trade/chat subscriptions require checkpoint replacement.
                    detail = "native-event-unsubscribe=" + unsubscribeException.GetType().Name
                        + ":" + unsubscribeException.Message;
                    return false;
                }
                detail = "ok";
                instance?.LogReplicationInfo("Going Cooperative quarantined imported native trader event without OnEnd eventId=" + eventId);
                return true;
            }
            catch (Exception ex)
            {
                if (markerRegistered && !registryAbsenceVerified)
                {
                    try { ReleaseReplicationTraderEventQuarantine(eventId); } catch { }
                }
                detail = ex.GetType().Name + ":" + ex.Message;
                instance?.LogReplicationWarning("Going Cooperative imported trader quarantine failed eventId=" + eventId + " error=" + detail);
                return false;
            }
        }

        private static void ReleaseReplicationTraderEventQuarantine(string eventId)
        {
            if (!ReplicationAdoptedTraderEventById.TryGetValue(eventId, out var weak)) return;
            if (weak.Target is object nativeEvent) ReplicationAdoptedTraderEvents.Remove(nativeEvent);
            ReplicationAdoptedTraderEventById.Remove(eventId);
        }

        private static void ReleaseAllReplicationTraderEventQuarantines()
        {
            foreach (var weak in ReplicationAdoptedTraderEventById.Values)
                if (weak.Target is object nativeEvent) ReplicationAdoptedTraderEvents.Remove(nativeEvent);
            ReplicationAdoptedTraderEventById.Clear();
        }

        private static void LoadReplicationTraderPartyNpc(HumanoidInstance humanoid)
        {
            var loadSavedNpc = AccessTools.Method(typeof(NPCManager), "LoadSavedNPC", new[] { typeof(HumanoidInstance) })
                ?? throw new MissingMethodException(typeof(NPCManager).FullName, "LoadSavedNPC(HumanoidInstance)");
            loadSavedNpc.Invoke(NPCManager.Instance, new object[] { humanoid });
        }

        private static void SendHostReplicationTraderPartyAgentState(CreatureBase creature, string source)
        {
            TraderPartyAgentBinding? binding;
            var semanticRevision = 1;
            lock (ReplicationTraderPartyLock)
            {
                ReplicationTraderPartyBindingByObject.TryGetValue(creature, out binding);
                if (binding != null && ReplicationHostTraderParties.TryGetValue(binding.EventId, out var record))
                    semanticRevision = NextHostTraderPartySemanticRevision(record);
            }
            if (binding == null) return;
            var owner = GetHostPartyOwner(creature);
            ResolveHostTraderPartyOwnerState(
                binding.EventId, owner, out var ownerId, out var ownedBySameEventTrader);
            if (owner != null && string.IsNullOrEmpty(ownerId))
            {
                instance?.LogReplicationWarning("Going Cooperative skipped trader owner state without a stable owner identity entity="
                    + binding.NetworkId);
                return;
            }
            binding.Detached = IsTraderPartyStockRole(binding.Role)
                && TraderPartyRuntimePolicy.IsDetached(
                    ownedBySameEventTrader
                        ? TraderPartyOwnerClassification.SameEventTrader
                        : owner == null
                            ? TraderPartyOwnerClassification.None
                            : TraderPartyOwnerClassification.GenericWorldOwner);
            binding.OwnerNetworkId = ownerId;
            binding.Roped = owner != null && ReferenceEquals(creature.RopedTo(), owner);
            var animalType = creature is AnimalInstance animal ? ((int)animal.AnimalType).ToString(CultureInfo.InvariantCulture) : "-1";
            var leaving = creature is HumanoidInstance humanoid && humanoid.IsLeaving;
            SendHostTraderPartyDelta(
                ReplicationTraderPartyAgentStateDeltaKind,
                binding.EventId,
                binding.Generation,
                FormatReplicationTraderPartyEnvelope()
                    + " wire=" + ReplicationTraderPartyWireVersion
                    + " eventIdB64=" + EncodeReplicationDetailBase64(binding.EventId)
                    + " generation=" + binding.Generation.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + semanticRevision.ToString(CultureInfo.InvariantCulture)
                    + " entityIdB64=" + EncodeReplicationDetailBase64(binding.NetworkId)
                    + " ownerIdB64=" + EncodeReplicationDetailBase64(ownerId)
                    + " detached=" + (binding.Detached ? "true" : "false")
                    + " roped=" + (binding.Roped ? "true" : "false")
                    + " leaving=" + (leaving ? "true" : "false")
                    + " animalType=" + animalType
                    + " sourceB64=" + EncodeReplicationDetailBase64(source));
        }

        private static void ReconcileHostReplicationTraderPartyAfterTrade(HumanoidInstance trader)
        {
            HostTraderPartyRecord? record;
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationTraderPartyBindingByObject.TryGetValue(trader, out var traderBinding)
                    || !ReplicationHostTraderParties.TryGetValue(traderBinding.EventId, out record)
                    || record.EventEnded) return;
                InvalidateHostTraderPartyBootstrap(record, "host-trade-applied");
            }

            var traders = record.Agents
                .Where(binding => string.Equals(binding.Role, "Trader", StringComparison.Ordinal))
                .Select(binding => binding.Agent)
                .OfType<HumanoidInstance>()
                .ToList();
            var candidates = new List<CreatureBase>();
            foreach (var animal in GlobalSaveController.CurrentVillageData.Animals)
                if (animal?.PetOwner != null && traders.Contains(animal.PetOwner)) candidates.Add(animal);
            foreach (var npc in GlobalSaveController.CurrentVillageData.NPCs)
                if (npc?.PrisonerBehaviour?.Owner != null && traders.Contains(npc.PrisonerBehaviour.Owner)) candidates.Add(npc);

            var added = new List<TraderPartyAgentBinding>();
            lock (ReplicationTraderPartyLock)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (record.Agents.Any(binding => ReferenceEquals(binding.Agent, candidate))) continue;
                    TraderPartyAgentBinding? releasedBinding = null;
                    HostTraderPartyRecord? releasedRecord = null;
                    if (ReplicationTraderPartyBindingByObject.TryGetValue(candidate, out var existingBinding))
                    {
                        if (!existingBinding.Detached
                            || !ReplicationHostTraderParties.TryGetValue(existingBinding.EventId, out releasedRecord)
                            || !releasedRecord.EventEnded)
                        {
                            instance?.LogReplicationWarning("Going Cooperative skipped cross-party trader adoption collision entity="
                                + existingBinding.NetworkId);
                            continue;
                        }
                        releasedBinding = existingBinding;
                        RememberDetachedTraderPartyAdoption(existingBinding);
                        UnregisterTraderPartyBinding(existingBinding);
                    }
                    var owner = GetHostPartyOwner(candidate);
                    var ownerId = owner == null ? string.Empty : FindHostPartyNetworkId(record, owner);
                    var seen = new HashSet<object>(record.Agents.Select(binding => (object)binding.Agent), ExternalReferenceComparer.Instance);
                    var before = record.Agents.Count;
                    try
                    {
                        if (candidate is AnimalInstance)
                            AddHostPartyAgent(record, candidate, "StockAnimal", record.NextStockAnimalOrdinal++, ownerId, seen);
                        else if (candidate is HumanoidInstance)
                            AddHostPartyAgent(record, candidate, "StockPrisoner", record.NextStockPrisonerOrdinal++, ownerId, seen);
                        if (record.Agents.Count == before)
                        {
                            if (releasedBinding != null) RegisterTraderPartyBinding(releasedBinding);
                            continue;
                        }
                        var binding = record.Agents[record.Agents.Count - 1];
                        RegisterTraderPartyBinding(binding);
                        if (releasedBinding != null && releasedRecord != null)
                        {
                            releasedRecord.Agents.Remove(releasedBinding);
                            if (releasedBinding.Agent is HumanoidInstance oldHumanoid) releasedRecord.Humanoids.Remove(oldHumanoid);
                            if (releasedBinding.Agent is AnimalInstance oldAnimal) releasedRecord.Animals.Remove(oldAnimal);
                            if (releasedRecord.Agents.Count == 0)
                                ReplicationHostTraderParties.Remove(releasedRecord.EventId);
                        }
                        added.Add(binding);
                    }
                    catch
                    {
                        while (record.Agents.Count > before)
                        {
                            var rollback = record.Agents[record.Agents.Count - 1];
                            UnregisterTraderPartyBinding(rollback);
                            record.Agents.RemoveAt(record.Agents.Count - 1);
                            if (rollback.Agent is HumanoidInstance rollbackHumanoid) record.Humanoids.Remove(rollbackHumanoid);
                            if (rollback.Agent is AnimalInstance rollbackAnimal) record.Animals.Remove(rollbackAnimal);
                        }
                        if (releasedBinding != null) RegisterTraderPartyBinding(releasedBinding);
                        throw;
                    }
                }
            }

            for (var i = 0; i < added.Count; i++) SendHostReplicationTraderPartyMemberAdopt(added[i], record);
            var currentAgents = record.Agents.ToList();
            for (var i = 0; i < currentAgents.Count; i++)
                SendHostReplicationTraderPartyAgentState(currentAgents[i].Agent, "host-trade-converge");
            instance?.LogReplicationInfo("Going Cooperative trader trade semantic convergence eventId=" + record.EventId
                + " adoptedMembers=" + added.Count.ToString(CultureInfo.InvariantCulture)
                + " inventoryHotMerge=false bootstrapInvalidated=true");
        }

        private static void InvalidateHostTraderPartyBootstrap(HostTraderPartyRecord record, string source)
        {
            record.BootstrapDirty = true;
            if (record.BootstrapMutationRevision == int.MaxValue)
                throw new InvalidOperationException("Trader bootstrap mutation revision exhausted.");
            record.BootstrapMutationRevision++;
            if (!replicationRemoteHelloReceived && !record.TransferActive && !record.EverTransferred)
            {
                if (record.ManifestRevision == int.MaxValue)
                    throw new InvalidOperationException("Trader bootstrap manifest revision exhausted.");
                record.ManifestRevision++;
                record.CachedManifestRevision = 0;
                record.CachedBundle = Array.Empty<byte>();
                record.CachedHash = string.Empty;
                record.CachedMemberNetworkIds = Array.Empty<string>();
                record.CachedMemberManifestToken = string.Empty;
                record.NextCheckpointRealtime = 0f;
            }
            instance?.LogReplicationInfo("Going Cooperative trader bootstrap invalidated eventId=" + record.EventId
                + " source=" + source + " liveFullFvRevision=false");
        }

        private static int NextHostTraderPartySemanticRevision(HostTraderPartyRecord record)
        {
            var highWater = Math.Max(record.SemanticRevision, record.ManifestRevision);
            if (highWater == int.MaxValue)
                throw new InvalidOperationException("Trader semantic revision exhausted.");
            record.SemanticRevision = highWater + 1;
            return record.SemanticRevision;
        }

        private static void SendHostReplicationTraderPartyMemberAdopt(
            TraderPartyAgentBinding binding,
            HostTraderPartyRecord record)
        {
            var owner = GetHostPartyOwner(binding.Agent);
            ResolveHostTraderPartyOwnerState(
                binding.EventId, owner, out var ownerId, out var ownedBySameEventTrader);
            if (owner != null && string.IsNullOrEmpty(ownerId))
                throw new InvalidDataException("Adopted trader member owner has no stable world identity.");
            binding.OwnerNetworkId = ownerId;
            binding.Detached = IsTraderPartyStockRole(binding.Role)
                && TraderPartyRuntimePolicy.IsDetached(
                    ownedBySameEventTrader
                        ? TraderPartyOwnerClassification.SameEventTrader
                        : owner == null
                            ? TraderPartyOwnerClassification.None
                            : TraderPartyOwnerClassification.GenericWorldOwner);
            binding.Roped = owner != null && ReferenceEquals(binding.Agent.RopedTo(), owner);
            var revision = NextHostTraderPartySemanticRevision(record);
            var animalType = binding.Agent is AnimalInstance animal ? (int)animal.AnimalType : -1;
            SendHostTraderPartyDelta(
                ReplicationTraderPartyMemberAdoptDeltaKind,
                binding.EventId,
                binding.Generation,
                FormatReplicationTraderPartyEnvelope()
                    + " wire=" + ReplicationTraderPartyWireVersion
                    + " eventIdB64=" + EncodeReplicationDetailBase64(binding.EventId)
                    + " generation=" + binding.Generation.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                    + " entityIdB64=" + EncodeReplicationDetailBase64(binding.NetworkId)
                    + " roleB64=" + EncodeReplicationDetailBase64(binding.Role)
                    + " ownerIdB64=" + EncodeReplicationDetailBase64(binding.OwnerNetworkId)
                    + " priorIdB64=" + EncodeReplicationDetailBase64(binding.PriorStableEntityId)
                    + " hostUid=" + binding.HostUniqueId.ToString(CultureInfo.InvariantCulture)
                    + " fingerprint=" + binding.Fingerprint
                    + " animal=" + (binding.IsAnimal ? "true" : "false")
                    + " detached=" + (binding.Detached ? "true" : "false")
                    + " roped=" + (binding.Roped ? "true" : "false")
                    + " animalType=" + animalType.ToString(CultureInfo.InvariantCulture));
        }

        private static bool TryApplyReplicationTraderPartyAgentState(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadTraderPartyEventAgent(delta.Detail, out var eventId, out var generation, out var entityId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "revision", out var semanticRevision)
                || !TryReadTraderPartyTextOptional(delta.Detail, "ownerIdB64", out var ownerId)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "detached", out var detached)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "roped", out var roped)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "leaving", out var leaving)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "animalType", out var animalType)
                || semanticRevision <= 0)
            {
                detail = "trader-party-state-malformed";
                return false;
            }
            lock (ReplicationTraderPartyLock)
                if (ReplicationClientTerminalTraderParties.Contains(eventId)
                    || ReplicationClientTraderPartyTracker.IsMemberTombstoned(
                        eventId, generation, entityId, semanticRevision))
                {
                    detail = "ok trader-party-state-stale-tombstone";
                    return true;
                }
            var semanticDisposition = ClassifyClientTraderPartySemanticDelta(
                eventId, generation, entityId, semanticRevision, delta.Detail, out var semanticPayloadHash);
            if (semanticDisposition == ClientTraderPartySemanticDisposition.AckStale
                || semanticDisposition == ClientTraderPartySemanticDisposition.AckDuplicate)
            {
                detail = semanticDisposition == ClientTraderPartySemanticDisposition.AckStale
                    ? "ok trader-party-state-stale-revision"
                    : "ok trader-party-state-duplicate";
                return true;
            }
            if (semanticDisposition == ClientTraderPartySemanticDisposition.Conflict)
            {
                detail = "trader-party-state-same-revision-conflict";
                LatchReplicationTraderPartyFailure(eventId, semanticRevision, detail);
                return false;
            }
            TraderPartyAgentBinding? binding;
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationTraderPartyObjectByNetworkId.TryGetValue(entityId, out var value)
                    || !ReplicationTraderPartyBindingByObject.TryGetValue(value, out binding))
                {
                    detail = "trader-party-state-agent-missing-retry";
                    return false;
                }
            }
            if (binding.EventId != eventId || binding.Generation != generation)
            {
                detail = "trader-party-state-generation-conflict";
                return false;
            }

            CreatureBase? owner = null;
            lock (ReplicationTraderPartyLock)
                if (!string.IsNullOrEmpty(ownerId)
                    && ReplicationTraderPartyObjectByNetworkId.TryGetValue(ownerId, out var ownerObject)) owner = ownerObject as CreatureBase;
            if (owner == null && !string.IsNullOrEmpty(ownerId)
                && TryFindReplicationAgentOwnerByEntityId(ownerId, out var genericOwner, out _)) owner = genericOwner as CreatureBase;
            if (!string.IsNullOrEmpty(ownerId) && owner == null)
            {
                detail = "trader-party-state-owner-missing-retry";
                return false;
            }
            var ownerIsEventTrader = IsClientSameEventTraderOwner(eventId, owner);
            if (!detached && IsTraderPartyStockRole(binding.Role) && !ownerIsEventTrader)
            {
                detail = "trader-party-state-attached-owner-not-event-trader";
                return false;
            }
            if (detached && ownerIsEventTrader)
            {
                detail = "trader-party-state-detached-owner-is-event-trader";
                return false;
            }

            var oldOwner = GetHostPartyOwner(binding.Agent);
            var oldRope = binding.Agent.RopedTo();
            var oldDetached = binding.Detached;
            var oldRoped = binding.Roped;
            var oldOwnerNetworkId = binding.OwnerNetworkId;
            var oldLeaving = binding.Leaving;
            var oldAnimalType = binding.Agent is AnimalInstance oldAnimal ? (int)oldAnimal.AnimalType : -1;
            var petSnapshots = CaptureTraderPartyPetSnapshots(oldOwner, owner);
            replicationTraderPartyApplicationDepth++;
            try
            {
                ApplyTraderPartyOwnershipState(
                    binding.Agent,
                    binding.Role,
                    owner,
                    detached,
                    roped,
                    animalType);
                binding.Detached = detached;
                binding.Roped = roped;
                binding.OwnerNetworkId = ownerId;
                binding.Leaving = leaving;
                if (!CommitClientTraderPartySemanticDelta(
                    eventId, generation, entityId, semanticRevision, semanticPayloadHash))
                    throw new InvalidDataException("Trader party state semantic high-water commit conflict.");
                detail = "ok trader-party-state";
                return true;
            }
            catch (Exception ex)
            {
                try { RestoreTraderPartyOwnershipState(binding.Agent, binding.Role, oldOwner, oldRope, petSnapshots); } catch { }
                try
                {
                    if (oldAnimalType >= 0 && binding.Agent is AnimalInstance rollbackAnimal)
                        rollbackAnimal.SetAnimalType((NSMedieval.Types.AnimalType)oldAnimalType);
                }
                catch { }
                binding.Detached = oldDetached;
                binding.Roped = oldRoped;
                binding.OwnerNetworkId = oldOwnerNetworkId;
                binding.Leaving = oldLeaving;
                detail = "trader-party-state=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
            finally
            {
                replicationTraderPartyApplicationDepth--;
            }
        }

        private static bool TryApplyReplicationTraderPartyMemberAdopt(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadTraderPartyEventAgent(delta.Detail, out var eventId, out var generation, out var entityId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "revision", out var semanticRevision)
                || !TryReadTraderPartyText(delta.Detail, "roleB64", out var role)
                || !TryReadTraderPartyTextOptional(delta.Detail, "ownerIdB64", out var ownerId)
                || !TryReadTraderPartyTextOptional(delta.Detail, "priorIdB64", out var priorStableEntityId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "hostUid", out var hostUniqueId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "fingerprint", out var fingerprint)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "animal", out var isAnimal)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "detached", out var detached)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "roped", out var roped)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "animalType", out var animalType)
                || semanticRevision <= 0)
            {
                detail = "trader-party-member-adopt-malformed";
                return false;
            }

            var descriptor = new TraderPartyAgentBinding
            {
                EventId = eventId,
                Generation = generation,
                NetworkId = entityId,
                Role = role,
                OwnerNetworkId = ownerId,
                PriorStableEntityId = priorStableEntityId,
                HostUniqueId = hostUniqueId,
                Fingerprint = fingerprint,
                IsAnimal = isAnimal,
                Detached = detached,
                Roped = roped,
                AdoptedExisting = true,
                SpawnedByReplication = false
            };
            try { ValidateTraderPartyDescriptorShape(descriptor); }
            catch (Exception ex)
            {
                detail = "trader-party-member-adopt-descriptor=" + ex.Message;
                return false;
            }

            lock (ReplicationTraderPartyLock)
                if (ReplicationClientTerminalTraderParties.Contains(eventId)
                    || ReplicationClientTraderPartyTracker.IsMemberTombstoned(
                        eventId, generation, entityId, semanticRevision))
                {
                    detail = "ok trader-party-member-adopt-stale-tombstone";
                    return true;
                }

            var semanticDisposition = ClassifyClientTraderPartySemanticDelta(
                eventId, generation, entityId, semanticRevision, delta.Detail, out var semanticPayloadHash);
            if (semanticDisposition == ClientTraderPartySemanticDisposition.AckStale
                || semanticDisposition == ClientTraderPartySemanticDisposition.AckDuplicate)
            {
                detail = semanticDisposition == ClientTraderPartySemanticDisposition.AckStale
                    ? "ok trader-party-member-adopt-stale-revision"
                    : "ok trader-party-member-adopt-duplicate";
                return true;
            }
            if (semanticDisposition == ClientTraderPartySemanticDisposition.Conflict)
            {
                detail = "trader-party-member-adopt-same-revision-conflict";
                LatchReplicationTraderPartyFailure(eventId, semanticRevision, detail);
                return false;
            }

            CreatureBase? actor = null;
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationTraderPartyObjectByNetworkId.TryGetValue(entityId, out var mapped)
                    && mapped is CreatureBase mappedCreature)
                {
                    if (!string.Equals(BuildTraderPartyFingerprint(mappedCreature), fingerprint, StringComparison.Ordinal))
                    {
                        detail = "trader-party-member-adopt-mapped-conflict";
                        return false;
                    }
                    actor = mappedCreature;
                }
            }

            if (actor == null && !string.IsNullOrEmpty(priorStableEntityId)
                && TryFindReplicationAgentOwnerByEntityId(priorStableEntityId, out var priorOwner, out _))
                actor = priorOwner as CreatureBase;
            if (actor == null)
            {
                var adoptionKey = FormatTraderPartyDetachedAdoptionKey(hostUniqueId, fingerprint, role);
                lock (ReplicationTraderPartyLock)
                    if (ReplicationDetachedTraderPartyAdoptions.TryGetValue(adoptionKey, out var weak)
                        && weak.Target is CreatureBase detachedActor) actor = detachedActor;
            }
            if (actor == null)
            {
                detail = "trader-party-member-adopt-prior-actor-missing-retry";
                return false;
            }
            if (isAnimal != (actor is AnimalInstance)
                || !string.Equals(BuildTraderPartyFingerprint(actor), fingerprint, StringComparison.Ordinal))
            {
                detail = "trader-party-member-adopt-identity-conflict";
                return false;
            }
            if (!TryValidateClientTraderPartyUniqueId(actor, out var uidDetail))
            {
                detail = "trader-party-member-adopt-uid=" + uidDetail;
                return false;
            }

            var currentTraders = new Dictionary<string, HumanoidInstance>(StringComparer.Ordinal);
            lock (ReplicationTraderPartyLock)
                foreach (var currentBinding in ReplicationTraderPartyBindingByObject.Values)
                    if (string.Equals(currentBinding.EventId, eventId, StringComparison.Ordinal)
                        && string.Equals(currentBinding.Role, "Trader", StringComparison.Ordinal)
                        && currentBinding.Agent is HumanoidInstance currentTrader)
                        currentTraders[currentBinding.NetworkId] = currentTrader;
            CreatureBase? newOwner = ResolveTraderPartyOwner(
                ownerId,
                currentTraders);
            if (!string.IsNullOrEmpty(ownerId) && newOwner == null)
            {
                detail = "trader-party-member-adopt-owner-missing-retry";
                return false;
            }
            var newOwnerIsEventTrader = IsClientSameEventTraderOwner(eventId, newOwner);
            if (!detached && IsTraderPartyStockRole(role) && !newOwnerIsEventTrader)
            {
                detail = "trader-party-member-adopt-attached-owner-not-event-trader";
                return false;
            }
            if (detached && newOwnerIsEventTrader)
            {
                detail = "trader-party-member-adopt-detached-owner-is-event-trader";
                return false;
            }

            TraderPartyAgentBinding? previousBinding = null;
            var oldRope = actor.RopedTo();
            var oldOwner = GetHostPartyOwner(actor);
            var petSnapshots = CaptureTraderPartyPetSnapshots(oldOwner, newOwner);
            replicationTraderPartyApplicationDepth++;
            try
            {
                lock (ReplicationTraderPartyLock)
                {
                    if (ReplicationTraderPartyBindingByObject.TryGetValue(actor, out previousBinding)
                        && !string.Equals(previousBinding.NetworkId, entityId, StringComparison.Ordinal))
                        throw new InvalidDataException("Prior actor is mapped to another replicated identity.");
                }
                ApplyTraderPartyOwnershipState(actor, role, newOwner, detached, roped, animalType);
                descriptor.Agent = actor;
                lock (ReplicationTraderPartyLock)
                {
                    RegisterTraderPartyBinding(descriptor);
                    if (!CommitClientTraderPartySemanticDelta(
                        eventId, generation, entityId, semanticRevision, semanticPayloadHash))
                        throw new InvalidDataException("Trader party member adoption semantic high-water commit conflict.");
                    ReplicationDetachedTraderPartyAdoptions.Remove(
                        FormatTraderPartyDetachedAdoptionKey(hostUniqueId, fingerprint, role));
                }
                detail = "ok trader-party-member-adopt semanticRevision="
                    + semanticRevision.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                try { RestoreTraderPartyOwnershipState(actor, role, oldOwner, oldRope, petSnapshots); } catch { }
                lock (ReplicationTraderPartyLock)
                {
                    UnregisterTraderPartyBinding(descriptor);
                    if (previousBinding != null) RegisterTraderPartyBinding(previousBinding);
                }
                detail = "trader-party-member-adopt=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
            finally
            {
                replicationTraderPartyApplicationDepth--;
            }
        }

        private static void SendHostReplicationTraderPartyTombstone(CreatureBase creature, string reason)
        {
            TraderPartyAgentBinding? binding = null;
            HostTraderPartyRecord? record = null;
            var semanticRevision = 1;
            var restartInitialBootstrap = false;
            lock (ReplicationTraderPartyLock)
            {
                ReplicationTraderPartyBindingByObject.TryGetValue(creature, out binding);
                if (binding == null || binding.Tombstoned) return;
                binding.Tombstoned = true;
                UnregisterTraderPartyBinding(binding);
                if (ReplicationHostTraderParties.TryGetValue(binding.EventId, out record))
                {
                    record.Agents.Remove(binding);
                    if (binding.Agent is HumanoidInstance removedHumanoid) record.Humanoids.Remove(removedHumanoid);
                    if (binding.Agent is AnimalInstance removedAnimal) record.Animals.Remove(removedAnimal);

                    if (!record.Agents.Any(agent => string.Equals(agent.Role, "Trader", StringComparison.Ordinal)))
                    {
                        record.EventEnded = true;
                        record.NextCheckpointRealtime = float.PositiveInfinity;
                        record.CachedBundle = Array.Empty<byte>();
                        record.CachedHash = string.Empty;
                        record.CachedManifestRevision = 0;
                        ReplicationTraderInitCreaturesByEvent.Remove(record.NativeEvent);
                        CancelHostTraderPartyTransfersForEvent(record.EventId, "last-trader-removed");
                    }
                    else if (TraderPartyRuntimePolicy.DecideBootstrapDisposition(
                            record.EverTransferred,
                            membershipRemoved: true,
                            record.BootstrapDirty,
                            newPeerRefresh: false) == TraderPartyBootstrapDisposition.SupersedeUnappliedBootstrap)
                    {
                        SupersedeHostTraderPartyInitialBootstrap(record, "member-tombstone-before-bootstrap");
                        restartInitialBootstrap = replicationRemoteHelloReceived;
                    }
                    else
                    {
                        // Once this peer has successfully applied a bootstrap, member
                        // removal converges only through this semantic tombstone. The
                        // dirty FV roster is reserved for a future peer/epoch.
                        InvalidateHostTraderPartyBootstrap(record, "actor-removed-after-bootstrap");
                    }
                    semanticRevision = NextHostTraderPartySemanticRevision(record);
                    if (record.Agents.Count == 0) ReplicationHostTraderParties.Remove(binding.EventId);
                }
            }
            if (binding == null) return;
            SendHostTraderPartyDelta(
                ReplicationTraderPartyTombstoneDeltaKind,
                binding.EventId,
                binding.Generation,
                FormatReplicationTraderPartyEnvelope()
                    + " wire=" + ReplicationTraderPartyWireVersion
                    + " eventIdB64=" + EncodeReplicationDetailBase64(binding.EventId)
                    + " generation=" + binding.Generation.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + semanticRevision.ToString(CultureInfo.InvariantCulture)
                    + " entityIdB64=" + EncodeReplicationDetailBase64(binding.NetworkId)
                    + " reasonB64=" + EncodeReplicationDetailBase64(reason));
            if (restartInitialBootstrap && record != null && !record.EventEnded)
                TryStartHostTraderPartyTransfer(record, "member-tombstone-bootstrap-restart");
        }

        private static void SupersedeHostTraderPartyInitialBootstrap(HostTraderPartyRecord record, string source)
        {
            if (record.EverTransferred)
                throw new InvalidOperationException("Cannot supersede a successfully applied trader bootstrap.");
            CancelHostTraderPartyTransfersForEvent(record.EventId, source);
            if (record.ManifestRevision == int.MaxValue)
                throw new InvalidOperationException("Trader bootstrap manifest revision exhausted.");
            if (record.BootstrapMutationRevision == int.MaxValue)
                throw new InvalidOperationException("Trader bootstrap mutation revision exhausted.");
            record.ManifestRevision++;
            record.BootstrapMutationRevision++;
            record.CachedManifestRevision = 0;
            record.CachedBundle = Array.Empty<byte>();
            record.CachedHash = string.Empty;
            record.CachedMemberNetworkIds = Array.Empty<string>();
            record.CachedMemberManifestToken = string.Empty;
            record.CachedHumanoidCount = 0;
            record.CachedAnimalCount = 0;
            record.SerializationFailureRevision = 0;
            record.BootstrapDirty = true;
            record.BootstrapRevisionPrepared = true;
            record.BootstrapRefreshForNewPeer = false;
            record.BootstrapAdvertisePending = replicationRemoteHelloReceived;
            record.NextCheckpointRealtime = 0f;
            instance?.LogReplicationInfo("Going Cooperative superseded pre-apply trader bootstrap eventId="
                + record.EventId + " revision=" + record.ManifestRevision.ToString(CultureInfo.InvariantCulture)
                + " source=" + source);
        }

        private static void CancelHostTraderPartyTransfersForEvent(string eventId, string source)
        {
            var activeTransfers = ReplicationHostTraderPartyTransfers.Values
                .Where(transfer => string.Equals(transfer.EventId, eventId, StringComparison.Ordinal))
                .ToList();
            for (var i = 0; i < activeTransfers.Count; i++)
            {
                PurgeHostTraderPartyTransferSequences(activeTransfers[i].TransferId);
                ReplicationHostTraderPartyTransfers.Remove(activeTransfers[i].TransferId);
                activeTransfers[i].Bundle = Array.Empty<byte>();
            }
            if (ReplicationHostTraderParties.TryGetValue(eventId, out var record)) record.TransferActive = false;
            if (activeTransfers.Count > 0)
                instance?.LogReplicationInfo("Going Cooperative cancelled stale trader bootstrap eventId=" + eventId
                    + " transfers=" + activeTransfers.Count.ToString(CultureInfo.InvariantCulture)
                    + " source=" + source);
        }

        private static bool TryApplyReplicationTraderPartyTombstone(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadTraderPartyEventAgent(delta.Detail, out var eventId, out var generation, out var entityId))
            {
                detail = "trader-party-tombstone-malformed";
                return false;
            }
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "revision", out var semanticRevision)
                || semanticRevision <= 0)
            {
                detail = "trader-party-tombstone-revision-malformed";
                return false;
            }
            lock (ReplicationTraderPartyLock)
                if (ReplicationClientTerminalTraderParties.Contains(eventId))
                {
                    detail = "ok trader-party-tombstone-terminal";
                    return true;
                }
            var semanticDisposition = ClassifyClientTraderPartySemanticDelta(
                eventId, generation, entityId, semanticRevision, delta.Detail, out var semanticPayloadHash);
            if (semanticDisposition == ClientTraderPartySemanticDisposition.AckStale
                || semanticDisposition == ClientTraderPartySemanticDisposition.AckDuplicate)
            {
                detail = semanticDisposition == ClientTraderPartySemanticDisposition.AckStale
                    ? "ok trader-party-tombstone-stale-revision"
                    : "ok trader-party-tombstone-duplicate";
                return true;
            }
            if (semanticDisposition == ClientTraderPartySemanticDisposition.Conflict)
            {
                detail = "trader-party-tombstone-same-revision-conflict";
                LatchReplicationTraderPartyFailure(eventId, semanticRevision, detail);
                return false;
            }
            var trackerResult = ReplicationClientTraderPartyTracker.RecordMemberTombstone(
                eventId,
                generation,
                entityId,
                semanticRevision);
            if (trackerResult == TraderPartyTransferRecordResult.Conflict
                || trackerResult == TraderPartyTransferRecordResult.CapacityExceeded)
            {
                detail = "trader-party-tombstone-tracker-" + trackerResult.ToString().ToLowerInvariant();
                LatchReplicationTraderPartyFailure(eventId, semanticRevision, detail);
                return false;
            }
            if (trackerResult == TraderPartyTransferRecordResult.Stale)
            {
                detail = "ok trader-party-tombstone-tracker-stale";
                return true;
            }
            var key = FormatTraderPartyTombstoneKey(eventId, generation, entityId);
            TraderPartyAgentBinding? binding = null;
            lock (ReplicationTraderPartyLock)
            {
                RecordClientReplicationTraderPartyTombstone(key);
                if (ReplicationTraderPartyObjectByNetworkId.TryGetValue(entityId, out var value))
                    ReplicationTraderPartyBindingByObject.TryGetValue(value, out binding);
            }
            if (binding != null && !DespawnClientTraderPartyAgent(binding, "host-tombstone"))
            {
                detail = "trader-party-tombstone-despawn-retry";
                return false;
            }
            if (!CommitClientTraderPartySemanticDelta(
                eventId, generation, entityId, semanticRevision, semanticPayloadHash))
            {
                detail = "trader-party-tombstone-high-water-commit-conflict";
                LatchReplicationTraderPartyFailure(eventId, semanticRevision, detail);
                return false;
            }
            detail = binding == null ? "ok trader-party-tombstone-pending" : "ok trader-party-tombstone-applied";
            return true;
        }

        private static void RecordClientReplicationTraderPartyTombstone(string key)
        {
            if (!ReplicationClientTraderPartyTombstones.Add(key)) return;
            ReplicationClientTraderPartyTombstoneOrder.Enqueue(key);
            while (ReplicationClientTraderPartyTombstoneOrder.Count > ReplicationTraderPartyMaxTombstones)
                ReplicationClientTraderPartyTombstones.Remove(ReplicationClientTraderPartyTombstoneOrder.Dequeue());
        }

        private static bool DespawnClientTraderPartyAgent(TraderPartyAgentBinding binding, string reason)
        {
            if (binding.Tombstoned) return true;
            var alreadyAbsent = IsTraderPartyActorDisposed(binding.Agent)
                || (binding.Agent is AnimalInstance existingAnimal
                    && !GlobalSaveController.CurrentVillageData.Animals.Contains(existingAnimal))
                || (binding.Agent is HumanoidInstance existingHumanoid
                    && !GlobalSaveController.CurrentVillageData.NPCs.Contains(existingHumanoid));
            if (alreadyAbsent)
            {
                binding.Tombstoned = true;
                lock (ReplicationTraderPartyLock) UnregisterTraderPartyBinding(binding);
                lock (ReplicationTraderPartyLock) TryFinalizeClientReplicationTraderParty(binding.EventId);
                return true;
            }
            binding.Tombstoned = true;
            var success = false;
            replicationTraderPartyApplicationDepth++;
            try
            {
                if (binding.Agent is AnimalInstance animal)
                {
                    AnimalManager.Instance.RemoveAnimal(animal, false);
                }
                else if (binding.Agent is HumanoidInstance humanoid)
                {
                    humanoid.DestroyStorage();
                    humanoid.DestroyEquipment();
                    NPCController.Instance.RemoveNPC(humanoid);
                    if (GlobalSaveController.CurrentVillageData.NPCs.Contains(humanoid))
                        GlobalSaveController.CurrentVillageData.NPCs.Remove(humanoid);
                }
                success = binding.Agent is AnimalInstance removedAnimal
                    ? !GlobalSaveController.CurrentVillageData.Animals.Contains(removedAnimal)
                    : binding.Agent is HumanoidInstance removedHumanoid
                        && !GlobalSaveController.CurrentVillageData.NPCs.Contains(removedHumanoid);
                if (!success) throw new InvalidOperationException("Native save-list removal postcondition failed.");
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative trader party despawn failed entity="
                    + binding.NetworkId + " reason=" + reason + " error=" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                lock (ReplicationTraderPartyLock)
                {
                    if (success) UnregisterTraderPartyBinding(binding);
                    else binding.Tombstoned = false;
                    if (success) TryFinalizeClientReplicationTraderParty(binding.EventId);
                }
                replicationTraderPartyApplicationDepth--;
            }
            return success;
        }

        internal static void RemoveClientReplicationTraderParty(string eventId, string reason)
        {
            if (string.IsNullOrWhiteSpace(eventId)) return;
            lock (ReplicationTraderPartyLock)
            {
                ReplicationClientEndedTraderParties.Add(eventId);
                var detached = ReplicationTraderPartyBindingByObject.Values
                    .Where(binding => string.Equals(binding.EventId, eventId, StringComparison.Ordinal) && binding.Detached)
                    .Distinct()
                    .ToList();
                for (var i = 0; i < detached.Count; i++)
                {
                    if (!RememberDetachedTraderPartyAdoption(detached[i]))
                    {
                        replicationTraderPartyRecoveryRequested = true;
                        replicationTraderPartyRecoveryReason = "event-end-detached-adoption-alias-conflict";
                        continue;
                    }
                    UnregisterTraderPartyBinding(detached[i]);
                }
                // The imported native event was removed from RunningEvents and
                // unsubscribed during commit. Once the authoritative presentation
                // ends, release its weak lifecycle quarantine even if detached
                // purchases remain bound as ordinary settlement actors.
                ReleaseReplicationTraderEventQuarantine(eventId);
                TryFinalizeClientReplicationTraderParty(eventId);
                instance?.LogReplicationInfo("Going Cooperative released detached client trader identities eventId="
                    + eventId + " count=" + detached.Count.ToString(CultureInfo.InvariantCulture));
            }
            // Event end is not actor death. Purchased animals/prisoners and merchants
            // still walking to the edge remain until host native removal tombstones them.
            // An in-flight late-join roster must likewise be allowed to commit.
            instance?.LogReplicationInfo("Going Cooperative trader event presentation ended eventId=" + eventId + " reason=" + reason);
        }

        private static void TryFinalizeClientReplicationTraderParty(string eventId)
        {
            if (!ReplicationClientEndedTraderParties.Contains(eventId)
                || ReplicationClientTraderPartyTransfers.Values.Any(transfer => string.Equals(transfer.EventId, eventId, StringComparison.Ordinal))
                || ReplicationTraderPartyBindingByObject.Values.Any(binding => string.Equals(binding.EventId, eventId, StringComparison.Ordinal))) return;
            ReplicationClientEndedTraderParties.Remove(eventId);
            ReleaseReplicationTraderEventQuarantine(eventId);
            RemoveClientTraderPartySemanticHighWaters(eventId);
            if (ReplicationClientTerminalTraderParties.Add(eventId)) ReplicationClientTerminalTraderPartyOrder.Enqueue(eventId);
            while (ReplicationClientTerminalTraderPartyOrder.Count > ReplicationTraderPartyMaxTerminalEvents)
            {
                var expiredEvent = ReplicationClientTerminalTraderPartyOrder.Dequeue();
                ReplicationClientTerminalTraderParties.Remove(expiredEvent);
                ReplicationClientTraderPartyGenerationByEvent.Remove(expiredEvent);
                ReplicationClientTraderPartyHashByEvent.Remove(expiredEvent);
            }
        }

        private static void ResetReplicationTraderParties(ReplicationTraderPartyResetContext context)
        {
            var bindings = new List<TraderPartyAgentBinding>();
            var pendingAborts = new List<PendingHostTraderPartyAbort>();
            var sameWorldAdoptionConflict = false;
            lock (ReplicationTraderPartyLock)
            {
                bindings = ReplicationTraderPartyBindingByObject.Values.Distinct().ToList();
                pendingAborts = ReplicationPendingHostTraderPartyAborts.ToList();
            }

            if (context != ReplicationTraderPartyResetContext.WorldReloadPending)
            {
                for (var i = 0; i < pendingAborts.Count; i++)
                {
                    while (!pendingAborts[i].CleanupComplete && pendingAborts[i].CleanupAttempts < 3)
                    {
                        pendingAborts[i].CleanupAttempts++;
                        pendingAborts[i].CleanupComplete = TryCleanupAbortedHostTraderParty(pendingAborts[i], out _);
                    }
                }

                if (!replicationConfigHostMode)
                {
                    for (var i = 0; i < bindings.Count; i++)
                    {
                        var binding = bindings[i];
                        if (binding.Agent == null || IsTraderPartyActorDisposed(binding.Agent)) continue;
                        if (!binding.Detached)
                        {
                            if (DespawnClientTraderPartyAgent(binding, "same-world-runtime-reset")) continue;
                            instance?.LogReplicationWarning("Going Cooperative preserved reset actor after native despawn failure entity="
                                + binding.NetworkId + "; reconnect adoption will reuse it instead of duplicating it.");
                        }
                        // Detached purchases are ordinary settlement actors now. Keep
                        // only their bounded weak alias so a reconnect can adopt the
                        // same object rather than respawning host stock.
                        if (!RememberDetachedTraderPartyAdoption(binding)) sameWorldAdoptionConflict = true;
                    }
                }
            }

            lock (ReplicationTraderPartyLock)
            {
                for (var i = 0; i < bindings.Count; i++) UnregisterTraderPartyBinding(bindings[i]);
                if (context == ReplicationTraderPartyResetContext.WorldReloadPending
                    || !ReplicationPendingHostTraderPartyAborts.Any(abort => !abort.CleanupComplete))
                    ReplicationTraderInitCreaturesByEvent.Clear();
                ReplicationHostTraderParties.Clear();
                ReplicationHostTraderPartyTransfers.Clear();
                ReplicationHostTraderPartySequenceBindings.Clear();
                ReplicationHostTraderPartyPrepareFailures.Clear();
                if (context == ReplicationTraderPartyResetContext.WorldReloadPending)
                {
                    ReplicationPendingHostTraderPartyAborts.Clear();
                    ReplicationAbortedHostTraderEvents.Clear();
                    ReplicationDetachedTraderPartyAdoptions.Clear();
                }
                else
                {
                    ReplicationPendingHostTraderPartyAborts.RemoveAll(abort => abort.CleanupComplete);
                    var staleAdoptions = ReplicationDetachedTraderPartyAdoptions
                        .Where(pair => pair.Value.Target is not CreatureBase actor || IsTraderPartyActorDisposed(actor))
                        .Select(pair => pair.Key)
                        .ToList();
                    for (var i = 0; i < staleAdoptions.Count; i++) ReplicationDetachedTraderPartyAdoptions.Remove(staleAdoptions[i]);
                }
                ReplicationClientTraderPartyTransfers.Clear();
                ReplicationClientTraderPartyGenerationByEvent.Clear();
                ReplicationClientTraderPartyHashByEvent.Clear();
                ReplicationClientTraderPartyFailureLatches.Clear();
                ReplicationClientTraderPartyTombstones.Clear();
                ReplicationClientTraderPartyTombstoneOrder.Clear();
                ReplicationClientTraderPartySemanticHighWaters.Clear();
                ReplicationClientTraderPartySemanticHighWaterOrder.Clear();
                ReplicationTraderPartyBindingByObject.Clear();
                ReplicationTraderPartyObjectByNetworkId.Clear();
                ReleaseAllReplicationTraderEventQuarantines();
                ReplicationClientEndedTraderParties.Clear();
                ReplicationClientTerminalTraderParties.Clear();
                ReplicationClientTerminalTraderPartyOrder.Clear();
            }
            ReplicationClientTraderPartyTracker.Reset();
            replicationTraderPartyApplicationDepth = 0;
            replicationTraderPartyTransferCounter = 0L;
            replicationNextTraderPartyCheckpointRealtime = 0f;
            replicationTraderPartyObservedRemoteReady = false;
            replicationTraderPartyRecoveryRequested = sameWorldAdoptionConflict;
            replicationTraderPartyRecoveryAttempted = false;
            replicationTraderPartyRecoveryReason = sameWorldAdoptionConflict
                ? "same-world-adoption-key-conflict-or-capacity"
                : string.Empty;
        }

        private static bool RememberDetachedTraderPartyAdoption(TraderPartyAgentBinding binding)
        {
            if (binding.Agent == null || IsTraderPartyActorDisposed(binding.Agent)) return true;
            var key = FormatTraderPartyDetachedAdoptionKey(binding.HostUniqueId, binding.Fingerprint, binding.Role);
            lock (ReplicationTraderPartyLock)
            {
                var staleKeys = ReplicationDetachedTraderPartyAdoptions
                    .Where(pair => pair.Value.Target is not CreatureBase actor || IsTraderPartyActorDisposed(actor))
                    .Select(pair => pair.Key)
                    .ToList();
                for (var i = 0; i < staleKeys.Count; i++) ReplicationDetachedTraderPartyAdoptions.Remove(staleKeys[i]);
                if (ReplicationDetachedTraderPartyAdoptions.TryGetValue(key, out var existing)
                    && existing.Target is CreatureBase existingActor
                    && !ReferenceEquals(existingActor, binding.Agent))
                {
                    instance?.LogReplicationWarning("Going Cooperative trader reset adoption key conflict key=" + key);
                    return false;
                }
                if (!ReplicationDetachedTraderPartyAdoptions.ContainsKey(key)
                    && ReplicationDetachedTraderPartyAdoptions.Count >= ReplicationTraderPartyMaxTerminalEvents)
                {
                    instance?.LogReplicationWarning("Going Cooperative trader reset adoption alias capacity reached.");
                    return false;
                }
                ReplicationDetachedTraderPartyAdoptions[key] = new WeakReference(binding.Agent);
                return true;
            }
        }

        private static bool TryGetReplicationTraderPartyNetworkId(object owner, out string networkId)
        {
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationTraderPartyBindingByObject.TryGetValue(owner, out var binding) && !binding.Tombstoned)
                {
                    networkId = binding.NetworkId;
                    return true;
                }
            }
            networkId = string.Empty;
            return false;
        }

        private static bool TryGetReplicationTraderPartyObject(string networkId, out object? owner)
        {
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationTraderPartyObjectByNetworkId.TryGetValue(networkId, out var value))
                {
                    owner = value;
                    return true;
                }
            }
            owner = null;
            return false;
        }

        private static void RegisterTraderPartyBinding(TraderPartyAgentBinding binding)
        {
            if (binding.Agent == null) return;
            if (ReplicationTraderPartyObjectByNetworkId.TryGetValue(binding.NetworkId, out var previous)
                && !ReferenceEquals(previous, binding.Agent))
                throw new InvalidOperationException("Trader party network ID collision: " + binding.NetworkId);
            if (ReplicationTraderPartyBindingByObject.TryGetValue(binding.Agent, out var previousBinding)
                && !string.Equals(previousBinding.NetworkId, binding.NetworkId, StringComparison.Ordinal))
                throw new InvalidOperationException("Trader party object already has a different network ID.");
            ReplicationTraderPartyObjectByNetworkId[binding.NetworkId] = binding.Agent;
            ReplicationTraderPartyBindingByObject[binding.Agent] = binding;
        }

        private static void UnregisterTraderPartyBinding(TraderPartyAgentBinding binding)
        {
            if (binding.Agent != null
                && ReplicationTraderPartyBindingByObject.TryGetValue(binding.Agent, out var currentBinding)
                && ReferenceEquals(currentBinding, binding)) ReplicationTraderPartyBindingByObject.Remove(binding.Agent);
            if (ReplicationTraderPartyObjectByNetworkId.TryGetValue(binding.NetworkId, out var current)
                && ReferenceEquals(current, binding.Agent)) ReplicationTraderPartyObjectByNetworkId.Remove(binding.NetworkId);
        }

        private static string BuildTraderPartyFingerprint(CreatureBase creature)
        {
            var text = creature.GetType().FullName
                + "|" + creature.Id;
            return ComputeTraderPartySha256(Encoding.UTF8.GetBytes(text));
        }

        private static string ComputeTraderPartySha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(bytes);
            var builder = new StringBuilder(digest.Length * 2);
            for (var i = 0; i < digest.Length; i++) builder.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static bool TryEncodeTraderPartyMemberManifest(
            IReadOnlyList<string> memberNetworkIds,
            out string token,
            out string detail)
        {
            token = string.Empty;
            detail = string.Empty;
            try
            {
                if (memberNetworkIds.Count < 1
                    || memberNetworkIds.Count > ReplicationTraderPartyMaxHumanoids + ReplicationTraderPartyMaxAnimals
                    || memberNetworkIds.Distinct(StringComparer.Ordinal).Count() != memberNetworkIds.Count)
                {
                    detail = "member-manifest-count";
                    return false;
                }
                var raw = Encoding.UTF8.GetBytes(string.Join("\u001f", memberNetworkIds));
                if (raw.Length <= 0 || raw.Length > ReplicationTraderPartyMaxMemberManifestBytes)
                {
                    detail = "member-manifest-raw-size=" + raw.Length.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                byte[] compressed;
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                        gzip.Write(raw, 0, raw.Length);
                    compressed = output.ToArray();
                }
                if (compressed.Length <= 0 || compressed.Length > ReplicationTraderPartyMaxCompressedMemberManifestBytes)
                {
                    detail = "member-manifest-compressed-size=" + compressed.Length.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                token = Convert.ToBase64String(compressed);
                return true;
            }
            catch (Exception ex)
            {
                detail = "member-manifest=" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        private static bool TryDecodeTraderPartyMemberManifest(
            string token,
            string eventId,
            int partyGeneration,
            int expectedCount,
            out string[] memberNetworkIds)
        {
            memberNetworkIds = Array.Empty<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(token)
                    || token.Length > ((ReplicationTraderPartyMaxCompressedMemberManifestBytes + 2) / 3) * 4
                    || token.Length % 4 != 0) return false;
                var compressed = Convert.FromBase64String(token);
                if (compressed.Length <= 0 || compressed.Length > ReplicationTraderPartyMaxCompressedMemberManifestBytes) return false;
                using var input = new MemoryStream(compressed, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var read = gzip.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    if (output.Length + read > ReplicationTraderPartyMaxMemberManifestBytes) return false;
                    output.Write(buffer, 0, read);
                }
                var raw = new UTF8Encoding(false, true).GetString(output.ToArray());
                memberNetworkIds = raw.Split(new[] { '\u001f' }, StringSplitOptions.None);
                if (memberNetworkIds.Length != expectedCount
                    || memberNetworkIds.Distinct(StringComparer.Ordinal).Count() != memberNetworkIds.Length) return false;
                var prefix = "event-agent:" + eventId + ":" + partyGeneration.ToString(CultureInfo.InvariantCulture) + ":";
                return memberNetworkIds.All(id => id.Length <= ReplicationTraderPartyMaxStringChars
                    && id.StartsWith(prefix, StringComparison.Ordinal));
            }
            catch
            {
                memberNetworkIds = Array.Empty<string>();
                return false;
            }
        }

        private static long TraderPartyTrackerNowMilliseconds()
        {
            return Math.Max(0L, (long)(Time.realtimeSinceStartup * 1000f));
        }

        private static bool IsTraderPartySha256(string value) => IsTraderPartyLowerHex(value, 64);

        private static bool IsTraderPartyGameAssemblyMvid(string value) => IsTraderPartyLowerHex(value, 32);

        private static bool IsTraderPartyLowerHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static bool IsTraderPartyActorDisposed(CreatureBase creature)
        {
            try
            {
                var property = AccessTools.Property(creature.GetType(), "HasDisposed")
                    ?? AccessTools.Property(typeof(CreatureBase), "HasDisposed");
                return property?.GetValue(creature, null) is bool disposed && disposed;
            }
            catch { return true; }
        }

        private static bool IsTraderPartyStockRole(string role)
        {
            return string.Equals(role, "StockAnimal", StringComparison.Ordinal)
                || string.Equals(role, "StockPrisoner", StringComparison.Ordinal);
        }

        private static void ValidateTraderPartyDescriptorShape(TraderPartyAgentBinding descriptor)
        {
            var roleAllowed = string.Equals(descriptor.Role, "Trader", StringComparison.Ordinal)
                || string.Equals(descriptor.Role, "Guard", StringComparison.Ordinal)
                || string.Equals(descriptor.Role, "StockAnimal", StringComparison.Ordinal)
                || string.Equals(descriptor.Role, "StockPrisoner", StringComparison.Ordinal);
            if (!roleAllowed
                || descriptor.HostUniqueId <= 0
                || !IsTraderPartySha256(descriptor.Fingerprint)
                || (!string.IsNullOrEmpty(descriptor.PriorStableEntityId)
                    && !IsReplicationStableEntityId(descriptor.PriorStableEntityId))
                || !descriptor.NetworkId.StartsWith(
                    "event-agent:" + descriptor.EventId + ":" + descriptor.Generation.ToString(CultureInfo.InvariantCulture) + ":",
                    StringComparison.Ordinal)
                || descriptor.IsAnimal != string.Equals(descriptor.Role, "StockAnimal", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid trader party role/type/identity descriptor.");

            if (IsTraderPartyStockRole(descriptor.Role))
            {
                var sameEventOwnerPrefix = "event-agent:" + descriptor.EventId + ":"
                    + descriptor.Generation.ToString(CultureInfo.InvariantCulture) + ":";
                if ((!descriptor.Detached
                        && (string.IsNullOrEmpty(descriptor.OwnerNetworkId)
                            || !descriptor.OwnerNetworkId.StartsWith(sameEventOwnerPrefix, StringComparison.Ordinal)))
                    || (descriptor.Detached
                        && descriptor.OwnerNetworkId.StartsWith(sameEventOwnerPrefix, StringComparison.Ordinal))
                    || (descriptor.Roped && string.IsNullOrEmpty(descriptor.OwnerNetworkId))
                    || (!string.IsNullOrEmpty(descriptor.OwnerNetworkId)
                        && !IsReplicationStableEntityId(descriptor.OwnerNetworkId)))
                    throw new InvalidDataException("Invalid trader party stock ownership descriptor.");
            }
            else if (!string.IsNullOrEmpty(descriptor.OwnerNetworkId) || descriptor.Detached || descriptor.Roped)
            {
                throw new InvalidDataException("Trader/guard descriptor cannot have an owner.");
            }
        }

        private static void ValidateClientTraderPartyManifest(
            List<TraderPartyAgentBinding> descriptors,
            ClientTraderPartyTransfer transfer)
        {
            var networkIds = new HashSet<string>(StringComparer.Ordinal);
            var hostIds = new HashSet<int>();
            var traders = 0;
            var humanoids = 0;
            var animals = 0;
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                ValidateTraderPartyDescriptorShape(descriptor);
                if (!networkIds.Add(descriptor.NetworkId) || !hostIds.Add(descriptor.HostUniqueId))
                    throw new InvalidDataException("Duplicate trader party manifest identity.");
                if (string.Equals(descriptor.Role, "Trader", StringComparison.Ordinal)) traders++;
                if (descriptor.IsAnimal) animals++; else humanoids++;
            }
            ValidateTraderPartyOwnerRelationships(descriptors);
            if (traders < 1 || humanoids != transfer.ExpectedHumanoids || animals != transfer.ExpectedAnimals)
                throw new InvalidDataException("Trader party manifest role/count mismatch.");
        }

        private static void ValidateTraderPartyOwnerRelationships(IList<TraderPartyAgentBinding> descriptors)
        {
            var byId = descriptors.ToDictionary(descriptor => descriptor.NetworkId, StringComparer.Ordinal);
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                if (!IsTraderPartyStockRole(descriptor.Role)) continue;
                TraderPartyAgentBinding? owner = null;
                var ownerIsManifestMember = !string.IsNullOrEmpty(descriptor.OwnerNetworkId)
                    && byId.TryGetValue(descriptor.OwnerNetworkId, out owner);
                if (!descriptor.Detached
                    && (!ownerIsManifestMember || !string.Equals(owner!.Role, "Trader", StringComparison.Ordinal)))
                    throw new InvalidDataException("Event-owned stock must reference a trader in the same manifest.");
                if (descriptor.Detached && ownerIsManifestMember)
                    throw new InvalidDataException("Detached stock cannot reference an event manifest owner.");
            }
        }

        private static string FormatReplicationTraderPartyEnvelope()
        {
            return "scope=" + replicationEventHostSessionNonce
                + " epoch=" + replicationEventHostEpoch.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatReplicationTraderPartyWorldDeltaKey(ReplicationWorldObjectDelta delta)
        {
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyAbortDeltaKind, StringComparison.Ordinal))
            {
                if (!TryReadTraderPartyText(delta.Detail, "eventIdB64", out var abortedEventId)) return string.Empty;
                return delta.DeltaKind
                    + "|eventLength=" + abortedEventId.Length.ToString(CultureInfo.InvariantCulture)
                    + "|event=" + abortedEventId;
            }
            if (string.Equals(delta.DeltaKind, ReplicationTraderPartyBeginDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, ReplicationTraderPartyChunkDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, ReplicationTraderPartyCommitDeltaKind, StringComparison.Ordinal))
            {
                if (!TryReadTraderPartyText(delta.Detail, "transferB64", out var transferId)) return string.Empty;
                var key = delta.DeltaKind + "|transferLength=" + transferId.Length.ToString(CultureInfo.InvariantCulture)
                    + "|transfer=" + transferId;
                if (string.Equals(delta.DeltaKind, ReplicationTraderPartyChunkDeltaKind, StringComparison.Ordinal))
                {
                    if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "index", out var index)) return string.Empty;
                    key += "|index=" + index.ToString(CultureInfo.InvariantCulture);
                }
                return key;
            }
            return TryReadTraderPartyEventAgent(delta.Detail, out var eventId, out var partyGeneration, out var entityId)
                ? delta.DeltaKind
                    + "|eventLength=" + eventId.Length.ToString(CultureInfo.InvariantCulture)
                    + "|event=" + eventId
                    + "|partyGeneration=" + partyGeneration.ToString(CultureInfo.InvariantCulture)
                    + "|entityLength=" + entityId.Length.ToString(CultureInfo.InvariantCulture)
                    + "|entity=" + entityId
                : string.Empty;
        }

        private static bool TryReadTraderPartyWire(string detail, out string transferId)
        {
            transferId = string.Empty;
            return TryReadReplicationWorldObjectDetailToken(detail, "wire", out var wire)
                && string.Equals(wire, ReplicationTraderPartyWireVersion, StringComparison.Ordinal)
                && TryReadTraderPartyText(detail, "transferB64", out transferId)
                && !string.IsNullOrWhiteSpace(transferId);
        }

        private static bool TryReadTraderPartyEventAgent(string detail, out string eventId, out int generation, out string entityId)
        {
            eventId = string.Empty;
            generation = 0;
            entityId = string.Empty;
            return TryReadReplicationWorldObjectDetailToken(detail, "wire", out var wire)
                && string.Equals(wire, ReplicationTraderPartyWireVersion, StringComparison.Ordinal)
                && TryReadTraderPartyText(detail, "eventIdB64", out eventId)
                && TryReadReplicationWorldObjectDetailInt(detail, "generation", out generation)
                && generation > 0
                && TryReadTraderPartyText(detail, "entityIdB64", out entityId)
                && entityId.StartsWith("event-agent:" + eventId + ":", StringComparison.Ordinal);
        }

        private static bool TryReadTraderPartyText(string detail, string key, out string value)
        {
            value = string.Empty;
            return TryReadReplicationWorldObjectDetailToken(detail, key, out var token)
                && TryDecodeReplicationDetailBase64(token, out value)
                && value.Length <= ReplicationTraderPartyMaxStringChars;
        }

        private static bool TryReadTraderPartyTextOptional(string detail, string key, out string value)
        {
            value = string.Empty;
            if (!TryReadReplicationWorldObjectDetailToken(detail, key, out var token)) return false;
            if (string.Equals(token, "_", StringComparison.Ordinal)) return true;
            return TryDecodeReplicationDetailBase64(token, out value) && value.Length <= ReplicationTraderPartyMaxStringChars;
        }

        private static string FormatTraderPartyTombstoneKey(string eventId, int generation, string entityId)
        {
            return eventId + "|" + generation.ToString(CultureInfo.InvariantCulture) + "|" + entityId;
        }

        private static ClientTraderPartySemanticDisposition ClassifyClientTraderPartySemanticDelta(
            string eventId,
            int generation,
            string entityId,
            int revision,
            string payload,
            out string payloadHash)
        {
            payloadHash = ComputeTraderPartySha256(Encoding.UTF8.GetBytes(payload ?? string.Empty));
            var key = FormatTraderPartyTombstoneKey(eventId, generation, entityId);
            lock (ReplicationTraderPartyLock)
            {
                if (!ReplicationClientTraderPartySemanticHighWaters.TryGetValue(key, out var highWater))
                    return ClientTraderPartySemanticDisposition.Apply;
                if (revision < highWater.Revision) return ClientTraderPartySemanticDisposition.AckStale;
                if (revision > highWater.Revision) return ClientTraderPartySemanticDisposition.Apply;
                return string.Equals(payloadHash, highWater.PayloadHash, StringComparison.Ordinal)
                    ? ClientTraderPartySemanticDisposition.AckDuplicate
                    : ClientTraderPartySemanticDisposition.Conflict;
            }
        }

        private static bool CommitClientTraderPartySemanticDelta(
            string eventId,
            int generation,
            string entityId,
            int revision,
            string payloadHash)
        {
            var key = FormatTraderPartyTombstoneKey(eventId, generation, entityId);
            lock (ReplicationTraderPartyLock)
            {
                if (ReplicationClientTraderPartySemanticHighWaters.TryGetValue(key, out var existing))
                {
                    if (revision < existing.Revision) return false;
                    if (revision == existing.Revision)
                        return string.Equals(payloadHash, existing.PayloadHash, StringComparison.Ordinal);
                    existing.Revision = revision;
                    existing.PayloadHash = payloadHash;
                    return true;
                }

                while (ReplicationClientTraderPartySemanticHighWaters.Count >= ReplicationTraderPartyMaxSemanticHighWaters
                    && ReplicationClientTraderPartySemanticHighWaterOrder.Count > 0)
                {
                    var expired = ReplicationClientTraderPartySemanticHighWaterOrder.Dequeue();
                    ReplicationClientTraderPartySemanticHighWaters.Remove(expired);
                }
                if (ReplicationClientTraderPartySemanticHighWaters.Count >= ReplicationTraderPartyMaxSemanticHighWaters)
                    return false;
                ReplicationClientTraderPartySemanticHighWaters[key] = new ClientTraderPartySemanticHighWater
                {
                    EventId = eventId,
                    Revision = revision,
                    PayloadHash = payloadHash
                };
                ReplicationClientTraderPartySemanticHighWaterOrder.Enqueue(key);
                return true;
            }
        }

        private static void RemoveClientTraderPartySemanticHighWaters(string eventId)
        {
            lock (ReplicationTraderPartyLock)
            {
                var keys = ReplicationClientTraderPartySemanticHighWaters
                    .Where(pair => string.Equals(pair.Value.EventId, eventId, StringComparison.Ordinal))
                    .Select(pair => pair.Key)
                    .ToList();
                for (var i = 0; i < keys.Count; i++) ReplicationClientTraderPartySemanticHighWaters.Remove(keys[i]);
                if (keys.Count > 0)
                {
                    var retained = ReplicationClientTraderPartySemanticHighWaterOrder
                        .Where(key => ReplicationClientTraderPartySemanticHighWaters.ContainsKey(key))
                        .ToList();
                    ReplicationClientTraderPartySemanticHighWaterOrder.Clear();
                    for (var i = 0; i < retained.Count; i++) ReplicationClientTraderPartySemanticHighWaterOrder.Enqueue(retained[i]);
                }
            }
        }

        private sealed class HostTraderPartyRecord
        {
            public object NativeEvent = null!;
            public string EventId = string.Empty;
            public int Generation;
            public int ManifestRevision;
            public int SemanticRevision = 1;
            public int BootstrapMutationRevision;
            public int NextStockAnimalOrdinal;
            public int NextStockPrisonerOrdinal;
            public readonly List<TraderPartyAgentBinding> Agents = new List<TraderPartyAgentBinding>();
            public readonly List<HumanoidInstance> Humanoids = new List<HumanoidInstance>();
            public readonly List<AnimalInstance> Animals = new List<AnimalInstance>();
            public bool TransferActive;
            public bool EventEnded;
            public bool PublicationReady;
            public bool EverTransferred;
            public bool BootstrapDirty;
            public bool BootstrapRevisionPrepared;
            public bool BootstrapAdvertisePending;
            public bool BootstrapRefreshForNewPeer;
            public float NextCheckpointRealtime;
            public int CachedManifestRevision;
            public int SerializationFailureRevision;
            public int CachedHumanoidCount;
            public int CachedAnimalCount;
            public string CachedHash = string.Empty;
            public string CachedGameAssemblyMvid = string.Empty;
            public string CachedMemberManifestToken = string.Empty;
            public string[] CachedMemberNetworkIds = Array.Empty<string>();
            public byte[] CachedBundle = Array.Empty<byte>();
        }

        private sealed class ClientTraderPartySemanticHighWater
        {
            public string EventId = string.Empty;
            public int Revision;
            public string PayloadHash = string.Empty;
        }

        private sealed class HostTraderPartyTransfer
        {
            public string TransferId = string.Empty;
            public string EventId = string.Empty;
            public string Hash = string.Empty;
            public string Source = string.Empty;
            public int Generation;
            public int PartyGeneration;
            public int ChunkCount;
            public int HumanoidCount;
            public int AnimalCount;
            public int BootstrapMutationRevision;
            public string MemberManifestToken = string.Empty;
            public string[] MemberNetworkIds = Array.Empty<string>();
            public int NextChunkIndex;
            public long BeginSequence;
            public long CommitSequence;
            public bool BeginAcknowledged;
            public bool CommitSent;
            public float CreatedRealtime;
            public byte[] Bundle = Array.Empty<byte>();
            public readonly HashSet<int> InFlightChunks = new HashSet<int>();
        }

        private sealed class HostTraderPartySequenceBinding
        {
            public string TransferId = string.Empty;
            public int ChunkIndex;
            public string Kind = string.Empty;
        }

        private sealed class ClientTraderPartyTransfer
        {
            public string TransferId = string.Empty;
            public string EventId = string.Empty;
            public string Hash = string.Empty;
            public int Generation;
            public int PartyGeneration;
            public string GameAssemblyMvid = string.Empty;
            public int ExpectedBytes;
            public int ChunkCount;
            public int ExpectedHumanoids;
            public int ExpectedAnimals;
            public string[] MemberNetworkIds = Array.Empty<string>();
            public int ReceivedCount;
            public int ReceivedBytes;
            public float LastTouchedRealtime;
            public byte[][] Chunks = Array.Empty<byte[]>();
            public bool[] Received = Array.Empty<bool>();
        }

        private sealed class TraderPartyAgentBinding
        {
            public string EventId = string.Empty;
            public string NetworkId = string.Empty;
            public string Role = string.Empty;
            public string OwnerNetworkId = string.Empty;
            public string PriorStableEntityId = string.Empty;
            public string Fingerprint = string.Empty;
            public int Generation;
            public int HostUniqueId;
            public bool IsAnimal;
            public bool AdoptedExisting;
            public bool SpawnedByReplication;
            public bool Detached;
            public bool Roped;
            public bool Leaving;
            public bool Tombstoned;
            public CreatureBase Agent = null!;
        }

        private sealed class HostTraderPartyPrepareFailure
        {
            public string Reason = string.Empty;
            public float NextRetryRealtime;
            public List<TraderPartyAgentBinding> CapturedAgents = new List<TraderPartyAgentBinding>();
        }

        private sealed class PendingHostTraderPartyAbort
        {
            public object NativeEvent = null!;
            public string EventId = string.Empty;
            public string Reason = string.Empty;
            public string PartyFingerprint = string.Empty;
            public int PartyMemberCount;
            public List<CreatureBase> CapturedActors = new List<CreatureBase>();
            public int CleanupAttempts;
            public bool AbortSent;
            public bool CleanupComplete;
        }

        private sealed class TraderPartyQuarantineMarker
        {
            public TraderPartyQuarantineMarker(string eventId)
            {
                EventId = eventId;
            }

            public string EventId { get; }
        }

        private sealed class TraderPartyPetSnapshot
        {
            public CreatureBase Owner = null!;
            public List<AnimalInstance> Pets = new List<AnimalInstance>();
            public List<int> PetIds = new List<int>();
        }

        private sealed class TraderPartyActorSnapshot
        {
            public CreatureBase Actor = null!;
            public string Role = string.Empty;
            public CreatureBase? OldOwner;
            public object? OldRope;
        }

        private sealed class ExternalReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ExternalReferenceComparer Instance = new ExternalReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}

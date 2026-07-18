using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using GoingCooperative.Core;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    [BepInPlugin(GoingCooperativeConstants.ModId, GoingCooperativeConstants.ModName, GoingCooperativeConstants.Version)]
    public sealed partial class GoingCooperativePlugin : BaseUnityPlugin, IRuntimeCommandActions
    {
        private static GoingCooperativePlugin? instance;
        private RuntimeCommandExecutor? runtimeCommandExecutor;
        private Harmony? harmony;
        private string? diagnosticDirectory;
        private bool applicationQuittingObserved;
        private static readonly object PluginLogBufferLock = new object();
        private static readonly StringBuilder PluginLogBuffer = new StringBuilder(16 * 1024);
        private static float pluginLogLastFlushRealtime;
        private const int PluginLogFlushChars = 16 * 1024;
        private const float PluginLogFlushSeconds = 0.5f;

        private static object? gameSpeedManagerInstance;
        private static object? groundManagerInstance;
        private static object? buildingPlacementManagerInstance;
        private static object? buildingsPoolInstance;
        private static object? plantResourceManagerInstance;

        private void Awake()
        {
            instance = this;
            runtimeCommandExecutor = new RuntimeCommandExecutor(this);
            InitializeFileDiagnostics();
            TryLoadReplicationConfig(this);
            LogReplicationPerfFpsProbeConfigured("awake");

            Logger.LogInfo("Going Cooperative host-authoritative replication plugin loaded.");
            AppendPluginLog("Going Cooperative replication plugin loaded version=" + GoingCooperativeConstants.Version);

            harmony = new Harmony(GoingCooperativeConstants.ModId);
            if (replicationConfigEnabled || replicationConfigMultiplayerMenuEnabled)
            {
                TryInstallReplicationCommandCapture(harmony);
                TryInstallReplicationHostRuntimePump(harmony);
                TryInstallReplicationClientSimulationSuppression(harmony);
                TryInstallReplicationHostMovementAuthority(harmony);
                TryInstallReplicationGoapActionProbe(harmony);
                TryInstallReplicationWorldObjectDeltaCapture(harmony);
                TryInstallReplicationWorldObjectDeltaClientHooks(harmony);
                TryInstallReplicationBuildingLifecycleV2Hooks(harmony);
                TryInstallReplicationNeedsHooks(harmony);
                TryInstallReplicationResultLifecycleProbes(harmony);
                TryInstallReplicationCombatHooks(harmony);
                TryInstallReplicationCombatDiagnostics(harmony);
                TryInstallReplicationEventHooks(harmony);
                TryInstallReplicationExternalEventAgentHooks(harmony);
                TryInstallMultiplayerUiLifecyclePatches(harmony);
            }

            Camera.onPreCull += OnReplicationCameraPreCull;
            TryStartReplicationRuntime();
        }

        private void Update()
        {
            UpdateReplicationRuntime();
            FlushPluginLogBufferIfDue(force: false);
        }

        private void OnGUI()
        {
            if (replicationConfigMultiplayerMenuEnabled)
            {
                // Going Medieval does not dispatch OnGUI reliably to the BaseUnityPlugin
                // component. The persistent runtime driver owns the replacement surface.
                return;
            }

            // Rollback path: multiplayerMenu=false preserves the original config-driven
            // runtime and client-only resync control exactly as it behaved before the
            // replacement menu was introduced.
            DrawReplicationResyncControlGui();
        }

        private void OnApplicationQuit()
        {
            applicationQuittingObserved = true;
            AppendPluginLog("Going Cooperative Application.quitting event observed.");
            FlushPluginLogBufferIfDue(force: true);
        }

        private void OnDestroy()
        {
            AppendPluginLog("Going Cooperative replication plugin destroyed applicationQuitting=" + applicationQuittingObserved);
            if (!applicationQuittingObserved)
            {
                AppendPluginLog("Going Cooperative replication plugin destroy ignored because application is still running; Harmony/runtime remain active.");
                FlushPluginLogBufferIfDue(force: true);
                return;
            }

            Camera.onPreCull -= OnReplicationCameraPreCull;
            multiplayerSaveTransfer.Dispose();
            StopReplicationRuntime(ReplicationTraderPartyResetContext.WorldReloadPending);
            if (harmony != null)
            {
                try
                {
                    harmony.UnpatchSelf();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Going Cooperative Harmony unpatch failed: " + ex.GetType().Name + " " + ex.Message);
                }

                harmony = null;
            }

            if (ReferenceEquals(instance, this))
            {
                instance = null;
            }

            FlushPluginLogBufferIfDue(force: true);
        }

        private static void OnReplicationCameraPreCull(Camera camera)
        {
            var current = instance;
            if (!ReferenceEquals(current, null))
            {
                current.PreCullReplicationRuntime();
            }
        }

        private static RuntimeCommandResult ApplyRuntimeCommand(GoingCooperativePlugin current, LockstepCommand command)
        {
            if (current.runtimeCommandExecutor == null)
            {
                current.runtimeCommandExecutor = new RuntimeCommandExecutor(current);
            }

            var result = current.runtimeCommandExecutor.Apply(command);
            current.AppendPluginLog("Going Cooperative replication command applied invoked="
                + (result.Invoked ? "yes" : "no")
                + " detail="
                + result.Detail
                + " "
                + FormatRuntimeCommandSummary(command));
            return result;
        }

        private static string FormatRuntimeCommandSummary(LockstepCommand command)
        {
            return "player="
                + command.PlayerId
                + " sequence="
                + command.Sequence.ToString(CultureInfo.InvariantCulture)
                + " kind="
                + command.Kind
                + " payloadHash="
                + DeterminismHash.Format(command.PayloadHash)
                + " targetTick="
                + command.TargetTick.ToString(CultureInfo.InvariantCulture)
                + " target="
                + (command.TargetStableId ?? "<none>")
                + " map="
                + (command.MapX.HasValue
                    ? command.MapX.Value.ToString(CultureInfo.InvariantCulture) + "," + (command.MapY ?? 0).ToString(CultureInfo.InvariantCulture) + "," + (command.MapZ ?? 0).ToString(CultureInfo.InvariantCulture)
                    : "<none>");
        }

        private void LogReplicationInfo(string message)
        {
            Logger.LogInfo(message);
            AppendPluginLog(message);
        }

        private void LogReplicationWarning(string message)
        {
            Logger.LogWarning(message);
            AppendPluginLog(message);
        }

        private void InitializeFileDiagnostics()
        {
            try
            {
                diagnosticDirectory = Path.Combine(Paths.BepInExRootPath, "GoingCooperative");
                Directory.CreateDirectory(diagnosticDirectory);
            }
            catch (Exception ex)
            {
                diagnosticDirectory = null;
                Logger.LogWarning("Going Cooperative could not initialize file diagnostics: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private void AppendPluginLog(string message)
        {
            if (diagnosticDirectory == null)
            {
                return;
            }

            try
            {
                lock (PluginLogBufferLock)
                {
                    PluginLogBuffer.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    PluginLogBuffer.Append(' ');
                    PluginLogBuffer.Append(message);
                    PluginLogBuffer.AppendLine();
                }

                FlushPluginLogBufferIfDue(force: false);
            }
            catch
            {
                // File diagnostics should never affect gameplay.
            }
        }

        private void FlushPluginLogBufferIfDue(bool force)
        {
            if (diagnosticDirectory == null)
            {
                return;
            }

            try
            {
                var now = Time.realtimeSinceStartup;
                string? payload = null;
                lock (PluginLogBufferLock)
                {
                    if (PluginLogBuffer.Length == 0)
                    {
                        return;
                    }

                    if (!force
                        && PluginLogBuffer.Length < PluginLogFlushChars
                        && now - pluginLogLastFlushRealtime < PluginLogFlushSeconds)
                    {
                        return;
                    }

                    payload = PluginLogBuffer.ToString();
                    PluginLogBuffer.Length = 0;
                    pluginLogLastFlushRealtime = now;
                }

                File.AppendAllText(Path.Combine(diagnosticDirectory, "plugin.log"), payload);
            }
            catch
            {
                // File diagnostics should never affect gameplay.
            }
        }

        private static bool TryParseConfigBool(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
            {
                return true;
            }

            if (string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (string.Equals(value, "0", StringComparison.Ordinal)
                || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            result = false;
            return false;
        }

        private static Type? ResolveTypeForPatch(string typeName)
        {
            switch (typeName)
            {
                case "System.String":
                    return typeof(string);
                case "System.Int32":
                    return typeof(int);
                case "System.Boolean":
                    return typeof(bool);
                case "System.Single":
                    return typeof(float);
                case "UnityEngine.Vector3":
                    return typeof(Vector3);
                case "UnityEngine.Vector2Int":
                    return typeof(Vector2Int);
                default:
                    return AccessTools.TypeByName(typeName);
            }
        }

        private static string FormatReflectionExceptionDetail(Exception ex)
        {
            if (ex is TargetInvocationException targetInvocation && targetInvocation.InnerException != null)
            {
                return targetInvocation.GetType().Name
                    + " inner="
                    + targetInvocation.InnerException.GetType().Name
                    + ":"
                    + targetInvocation.InnerException.Message;
            }

            return ex.GetType().Name + ":" + ex.Message;
        }

        private static long GetPassiveCommandSurfaceCount(string key)
        {
            return 0L;
        }

    }
}

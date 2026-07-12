using System;
using System.Globalization;
using System.IO;
using BepInEx;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationConfigRelativePath = @"GoingCooperative\replication.cfg";
        private const string DiagnosticsConfigRelativePath = @"GoingCooperative\diagnostics.cfg";
        private const string ReplicationHostPeerId = "host";
        private const string ReplicationClientPeerId = "client";

        private static bool replicationConfigLoadAttempted;
        private static bool replicationConfigEnabled;
        private static bool replicationConfigHostMode = true;
        private static bool replicationConfigApplySnapshots;
        private static bool replicationConfigSuppressClientSimulation = true;
        private static bool replicationConfigBasicDeterminismAssist;
        private static bool replicationConfigBasicDeterminismAssistCreatureManager;
        private static bool replicationConfigBasicDeterminismAssistPathing;
        private static bool replicationConfigBasicDeterminismAssistCancelPathMovement;
        private static bool replicationConfigHostDrivenGoapPlayback;
        private static bool replicationConfigHostDrivenGoapPlaybackTick;
        private static bool replicationConfigPuppetVisualTick;
        private static bool replicationConfigActionPhaseReplication;
        private static bool replicationConfigActionPhaseVisualReplay;
        private static bool replicationConfigActionParameterReplication;
        private static bool replicationConfigNativeAnimationControllerReplay;
        private static bool replicationConfigActionAnimatorStateSampling;
        private static bool replicationConfigAnimateReplicatedMovement = true;
        private static bool replicationConfigSmoothReplicatedMovement = true;
        private static bool replicationConfigNeedsReplication = true;
        private static bool replicationConfigForceHostMovement;
        private static bool replicationConfigSendSnapshots = true;
        private static bool replicationConfigLogSnapshots = true;
        private static bool replicationConfigValidateSnapshots = true;
        private static bool replicationConfigSendProofIntent;
        private static bool replicationConfigResultLifecycleProbes = true;
        private static bool replicationConfigAnimationDiagnostics;
        private static bool replicationConfigCharacterStateDiagnostics;
        private static bool replicationConfigCarryDiagnostics;
        private static bool replicationConfigGoapActionProbe;
        private static bool replicationConfigMultiplayerMenuEnabled = true;
        private static bool replicationConfigPerfFpsProbe = true;
        private static bool replicationConfigResourcePileStateSnapshots;
        private static bool replicationConfigResourceContainerReplication;
        private static string replicationConfigHost = "127.0.0.1";
        private static string replicationConfigProofIntent = "speed-normal";
        private static string replicationConfigCommandCaptureMode = "send";
        private static string replicationConfigRegionCommandMode = "shadow";
        private static string replicationConfigGoapActionProbeEntityId = string.Empty;
        private static string replicationConfigWorldObjectDeltaMode = "shadow";
        private static int replicationConfigPort = 47692;
        private static int replicationConfigSnapshotHz = 12;
        private static int replicationConfigFullResyncSeconds = 45;
        private static int replicationConfigInterpolationMs = 150;
        private static int replicationConfigMaxSnapshotEntities = 128;
        private static int replicationConfigSnapshotValidationSeconds = 10;
        private static int replicationConfigProofIntentDelaySeconds = 5;
        private static int replicationConfigWorldObjectDeltaApplyBudgetPerFrame = 12;
        private static int replicationConfigWorldObjectDeltaApplyQueueMax = 2048;
        private static float replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame = 8f;

        private static void TryLoadReplicationConfig(GoingCooperativePlugin current)
        {
            if (replicationConfigLoadAttempted)
            {
                return;
            }

            replicationConfigLoadAttempted = true;
            var configPath = Path.Combine(Paths.GameRootPath, ReplicationConfigRelativePath);
            if (!File.Exists(configPath))
            {
                current.Logger.LogInfo("Going Cooperative replication config missing; replication disabled path=" + configPath);
                return;
            }

            try
            {
                var lines = File.ReadAllLines(configPath);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        current.Logger.LogWarning("Going Cooperative replication config ignored malformed line=" + (i + 1).ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    var value = line.Substring(separator + 1).Trim();
                    ApplyReplicationConfigValue(current, key, value, i + 1);
                }

                current.Logger.LogInfo("Going Cooperative replication config loaded enabled="
                    + replicationConfigEnabled
                    + " mode="
                    + (replicationConfigHostMode ? "host" : "client")
                    + " host="
                    + replicationConfigHost
                    + " port="
                    + replicationConfigPort.ToString(CultureInfo.InvariantCulture)
                    + " snapshotHz="
                    + replicationConfigSnapshotHz.ToString(CultureInfo.InvariantCulture)
                    + " applySnapshots="
                    + replicationConfigApplySnapshots
                    + " suppressClientSimulation="
                    + replicationConfigSuppressClientSimulation
                    + " basicDeterminismAssist="
                    + replicationConfigBasicDeterminismAssist
                    + " basicDeterminismAssistCreatureManager="
                    + replicationConfigBasicDeterminismAssistCreatureManager
                    + " basicDeterminismAssistPathing="
                    + replicationConfigBasicDeterminismAssistPathing
                    + " basicDeterminismAssistCancelPathMovement="
                    + replicationConfigBasicDeterminismAssistCancelPathMovement
                    + " hostDrivenGoapPlayback="
                    + replicationConfigHostDrivenGoapPlayback
                    + " hostDrivenGoapPlaybackTick="
                    + replicationConfigHostDrivenGoapPlaybackTick
                    + " puppetVisualTick="
                    + replicationConfigPuppetVisualTick
                    + " actionPhaseReplication="
                    + replicationConfigActionPhaseReplication
                    + " actionPhaseVisualReplay="
                    + replicationConfigActionPhaseVisualReplay
                    + " actionParameterReplication="
                    + replicationConfigActionParameterReplication
                    + " nativeAnimationControllerReplay="
                    + replicationConfigNativeAnimationControllerReplay
                    + " actionAnimatorStateSampling="
                    + replicationConfigActionAnimatorStateSampling
                    + " animateReplicatedMovement="
                    + replicationConfigAnimateReplicatedMovement
                    + " smoothReplicatedMovement="
                    + replicationConfigSmoothReplicatedMovement
                    + " needsReplication="
                    + replicationConfigNeedsReplication
                    + " interpolationMs="
                    + replicationConfigInterpolationMs.ToString(CultureInfo.InvariantCulture)
                    + " forceHostMovement="
                    + replicationConfigForceHostMovement
                    + " validateSnapshots="
                    + replicationConfigValidateSnapshots
                    + " sendProofIntent="
                    + replicationConfigSendProofIntent
                    + " proofIntent="
                    + replicationConfigProofIntent
                    + " commandCaptureMode="
                    + replicationConfigCommandCaptureMode
                    + " regionCommandMode="
                    + replicationConfigRegionCommandMode
                    + " worldObjectDeltaMode="
                    + replicationConfigWorldObjectDeltaMode
                    + " resultLifecycleProbes="
                    + replicationConfigResultLifecycleProbes
                    + " animationDiagnostics="
                    + replicationConfigAnimationDiagnostics
                    + " characterStateDiagnostics="
                    + replicationConfigCharacterStateDiagnostics
                    + " carryDiagnostics="
                    + replicationConfigCarryDiagnostics
                    + " goapActionProbe="
                    + replicationConfigGoapActionProbe
                    + " goapActionProbeEntityId="
                    + replicationConfigGoapActionProbeEntityId
                    + " multiplayerMenu="
                    + replicationConfigMultiplayerMenuEnabled
                    + " perfFpsProbe="
                    + replicationConfigPerfFpsProbe
                    + " resourcePileStateSnapshots="
                    + replicationConfigResourcePileStateSnapshots
                    + " resourceContainerReplication="
                    + replicationConfigResourceContainerReplication
                    + " worldObjectDeltaApplyBudgetPerFrame="
                    + replicationConfigWorldObjectDeltaApplyBudgetPerFrame.ToString(CultureInfo.InvariantCulture)
                    + " worldObjectDeltaApplyQueueMax="
                    + replicationConfigWorldObjectDeltaApplyQueueMax.ToString(CultureInfo.InvariantCulture)
                    + " worldObjectDeltaApplyBudgetMsPerFrame="
                    + replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame.ToString("0.###", CultureInfo.InvariantCulture)
                    + " maxSnapshotEntities="
                    + replicationConfigMaxSnapshotEntities.ToString(CultureInfo.InvariantCulture));

                TryLoadDiagnosticsConfig(current);
            }
            catch (Exception ex)
            {
                current.Logger.LogWarning("Going Cooperative replication config load failed path="
                    + configPath
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
                replicationConfigEnabled = false;
            }
        }

        private static void ApplyReplicationConfigValue(GoingCooperativePlugin current, string key, string value, int lineNumber)
        {
            switch (key)
            {
                case "enabled":
                case "enable":
                    if (TryParseConfigBool(value, out var enabled))
                    {
                        replicationConfigEnabled = enabled;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "mode":
                    if (string.Equals(value, "host", StringComparison.OrdinalIgnoreCase))
                    {
                        replicationConfigHostMode = true;
                    }
                    else if (string.Equals(value, "client", StringComparison.OrdinalIgnoreCase))
                    {
                        replicationConfigHostMode = false;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "host":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        replicationConfigHost = value;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port <= 65535)
                    {
                        replicationConfigPort = port;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "snapshothz":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snapshotHz) && snapshotHz > 0 && snapshotHz <= 60)
                    {
                        replicationConfigSnapshotHz = snapshotHz;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "fullresyncseconds":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds <= 3600)
                    {
                        replicationConfigFullResyncSeconds = seconds;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "interpolationms":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interpolationMs) && interpolationMs >= 0 && interpolationMs <= 5000)
                    {
                        replicationConfigInterpolationMs = interpolationMs;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "smoothreplicatedmovement":
                case "smoothmovement":
                case "snapshotinterpolation":
                    if (TryParseConfigBool(value, out var smoothReplicatedMovement))
                    {
                        replicationConfigSmoothReplicatedMovement = smoothReplicatedMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "needsreplication":
                case "hungersleepreplication":
                case "needsync":
                    if (TryParseConfigBool(value, out var needsReplication))
                    {
                        replicationConfigNeedsReplication = needsReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "applysnapshots":
                    if (TryParseConfigBool(value, out var applySnapshots))
                    {
                        replicationConfigApplySnapshots = applySnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "suppressclientsimulation":
                    if (TryParseConfigBool(value, out var suppressClientSimulation))
                    {
                        replicationConfigSuppressClientSimulation = suppressClientSimulation;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassist":
                case "basicdeterminism":
                case "simassist":
                    if (TryParseConfigBool(value, out var basicDeterminismAssist))
                    {
                        replicationConfigBasicDeterminismAssist = basicDeterminismAssist;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassistcreaturemanager":
                case "basicdeterminismassistcreatures":
                case "simassistcreaturemanager":
                    if (TryParseConfigBool(value, out var basicDeterminismAssistCreatureManager))
                    {
                        replicationConfigBasicDeterminismAssistCreatureManager = basicDeterminismAssistCreatureManager;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassistpathing":
                case "basicdeterminismassistpathfinder":
                case "simassistpathing":
                    if (TryParseConfigBool(value, out var basicDeterminismAssistPathing))
                    {
                        replicationConfigBasicDeterminismAssistPathing = basicDeterminismAssistPathing;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "basicdeterminismassistcancelpathmovement":
                case "basicdeterminismassistcancelwalking":
                case "simassistcancelpathmovement":
                    if (TryParseConfigBool(value, out var basicDeterminismAssistCancelPathMovement))
                    {
                        replicationConfigBasicDeterminismAssistCancelPathMovement = basicDeterminismAssistCancelPathMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "hostdrivengoapplayback":
                case "goapplayback":
                    if (TryParseConfigBool(value, out var hostDrivenGoapPlayback))
                    {
                        replicationConfigHostDrivenGoapPlayback = hostDrivenGoapPlayback;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "hostdrivengoapplaybacktick":
                case "goapplaybacktick":
                    if (TryParseConfigBool(value, out var hostDrivenGoapPlaybackTick))
                    {
                        replicationConfigHostDrivenGoapPlaybackTick = hostDrivenGoapPlaybackTick;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "puppetvisualtick":
                case "replicationpuppetvisualtick":
                    if (TryParseConfigBool(value, out var puppetVisualTick))
                    {
                        replicationConfigPuppetVisualTick = puppetVisualTick;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionphasereplication":
                case "goapactionphases":
                    if (TryParseConfigBool(value, out var actionPhaseReplication))
                    {
                        replicationConfigActionPhaseReplication = actionPhaseReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionphasevisualreplay":
                case "goapphasevisualreplay":
                    if (TryParseConfigBool(value, out var actionPhaseVisualReplay))
                    {
                        replicationConfigActionPhaseVisualReplay = actionPhaseVisualReplay;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionparameterreplication":
                case "goapactionparameters":
                    if (TryParseConfigBool(value, out var actionParameterReplication))
                    {
                        replicationConfigActionParameterReplication = actionParameterReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "nativeanimationcontrollerreplay":
                case "animationcontrollerreplay":
                    if (TryParseConfigBool(value, out var nativeAnimationControllerReplay))
                    {
                        replicationConfigNativeAnimationControllerReplay = nativeAnimationControllerReplay;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;

                case "actionanimatorstatesampling":
                case "actionanimatorstream":
                    if (TryParseConfigBool(value, out var actionAnimatorStateSampling))
                    {
                        replicationConfigActionAnimatorStateSampling = actionAnimatorStateSampling;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "forcehostmovement":
                case "hostmovementauthority":
                    if (TryParseConfigBool(value, out var forceHostMovement))
                    {
                        replicationConfigForceHostMovement = forceHostMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "animatereplicatedmovement":
                    if (TryParseConfigBool(value, out var animateReplicatedMovement))
                    {
                        replicationConfigAnimateReplicatedMovement = animateReplicatedMovement;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "sendsnapshots":
                    if (TryParseConfigBool(value, out var sendSnapshots))
                    {
                        replicationConfigSendSnapshots = sendSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "logsnapshots":
                    if (TryParseConfigBool(value, out var logSnapshots))
                    {
                        replicationConfigLogSnapshots = logSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "validatesnapshots":
                    if (TryParseConfigBool(value, out var validateSnapshots))
                    {
                        replicationConfigValidateSnapshots = validateSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "sendproofintent":
                    if (TryParseConfigBool(value, out var sendProofIntent))
                    {
                        replicationConfigSendProofIntent = sendProofIntent;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "resultlifecycleprobes":
                    if (TryParseConfigBool(value, out var resultLifecycleProbes))
                    {
                        replicationConfigResultLifecycleProbes = resultLifecycleProbes;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "animationdiagnostics":
                    if (TryParseConfigBool(value, out var animationDiagnostics))
                    {
                        replicationConfigAnimationDiagnostics = animationDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "characterstatediagnostics":
                    if (TryParseConfigBool(value, out var characterStateDiagnostics))
                    {
                        replicationConfigCharacterStateDiagnostics = characterStateDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "carrydiagnostics":
                case "carryprobe":
                    if (TryParseConfigBool(value, out var carryDiagnostics))
                    {
                        replicationConfigCarryDiagnostics = carryDiagnostics;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "goapactionprobe":
                case "goapprobe":
                    if (TryParseConfigBool(value, out var goapActionProbe))
                    {
                        replicationConfigGoapActionProbe = goapActionProbe;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "goapactionprobeentityid":
                case "goapprobeentityid":
                    replicationConfigGoapActionProbeEntityId = value.Trim();
                    break;
                case "multiplayermenu":
                case "multiplayermenuenabled":
                case "enablemultiplayermenu":
                    if (TryParseConfigBool(value, out var multiplayerMenuEnabled))
                    {
                        replicationConfigMultiplayerMenuEnabled = multiplayerMenuEnabled;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "perffpsprobe":
                    if (TryParseConfigBool(value, out var perfFpsProbe))
                    {
                        replicationConfigPerfFpsProbe = perfFpsProbe;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "resourcepilestatesnapshots":
                case "resourcepilesnapshots":
                case "sendresourcepilestatesnapshots":
                    if (TryParseConfigBool(value, out var resourcePileStateSnapshots))
                    {
                        replicationConfigResourcePileStateSnapshots = resourcePileStateSnapshots;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "resourcecontainerreplication":
                case "resourcecontainers":
                case "resourceownershipreplication":
                    if (TryParseConfigBool(value, out var resourceContainerReplication))
                    {
                        replicationConfigResourceContainerReplication = resourceContainerReplication;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "worldobjectdeltaapplybudgetperframe":
                case "worlddeltaapplybudgetperframe":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var applyBudget)
                        && applyBudget >= 0
                        && applyBudget <= 256)
                    {
                        replicationConfigWorldObjectDeltaApplyBudgetPerFrame = applyBudget;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "worldobjectdeltaapplyqueuemax":
                case "worlddeltaapplyqueuemax":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var queueMax)
                        && queueMax >= 64
                        && queueMax <= 65536)
                    {
                        replicationConfigWorldObjectDeltaApplyQueueMax = queueMax;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "worldobjectdeltaapplybudgetmsperframe":
                case "worlddeltaapplybudgetmsperframe":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var applyBudgetMs)
                        && applyBudgetMs >= 0f
                        && applyBudgetMs <= 100f)
                    {
                        replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame = applyBudgetMs;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "proofintent":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        replicationConfigProofIntent = value.Trim();
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "commandcapturemode":
                    if (TryParseReplicationCaptureMode(value, out var commandCaptureMode))
                    {
                        replicationConfigCommandCaptureMode = commandCaptureMode;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "regioncommandmode":
                    if (TryParseReplicationCaptureMode(value, out var regionCommandMode))
                    {
                        replicationConfigRegionCommandMode = regionCommandMode;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "worldobjectdeltamode":
                    if (TryParseReplicationWorldObjectDeltaMode(value, out var worldObjectDeltaMode))
                    {
                        replicationConfigWorldObjectDeltaMode = worldObjectDeltaMode;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "proofintentdelayseconds":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var proofIntentDelaySeconds)
                        && proofIntentDelaySeconds >= 0
                        && proofIntentDelaySeconds <= 300)
                    {
                        replicationConfigProofIntentDelaySeconds = proofIntentDelaySeconds;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "snapshotvalidationseconds":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var validationSeconds)
                        && validationSeconds >= 1
                        && validationSeconds <= 300)
                    {
                        replicationConfigSnapshotValidationSeconds = validationSeconds;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                case "maxsnapshotentities":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxEntities) && maxEntities >= 0 && maxEntities <= 2048)
                    {
                        replicationConfigMaxSnapshotEntities = maxEntities;
                    }
                    else
                    {
                        LogReplicationConfigInvalidValue(current, lineNumber, key, value);
                    }

                    break;
                default:
                    current.Logger.LogWarning("Going Cooperative replication config ignored unknown key line="
                        + lineNumber.ToString(CultureInfo.InvariantCulture)
                        + " key="
                        + key);
                    break;
            }
        }

        private static void TryLoadDiagnosticsConfig(GoingCooperativePlugin current)
        {
            var configPath = Path.Combine(Paths.GameRootPath, DiagnosticsConfigRelativePath);
            if (!File.Exists(configPath))
            {
                return;
            }

            try
            {
                var lines = File.ReadAllLines(configPath);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    var value = line.Substring(separator + 1).Trim();
                    if (!string.Equals(key, "perffpsprobe", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (TryParseConfigBool(value, out var perfFpsProbe))
                    {
                        replicationConfigPerfFpsProbe = perfFpsProbe;
                    }
                    else
                    {
                        current.Logger.LogWarning("Going Cooperative diagnostics config ignored invalid value line="
                            + (i + 1).ToString(CultureInfo.InvariantCulture)
                            + " key="
                            + key
                            + " value="
                            + value);
                    }
                }

                current.Logger.LogInfo("Going Cooperative diagnostics config loaded perfFpsProbe=" + replicationConfigPerfFpsProbe);
            }
            catch (Exception ex)
            {
                current.Logger.LogWarning("Going Cooperative diagnostics config load failed path="
                    + configPath
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private static void LogReplicationConfigInvalidValue(GoingCooperativePlugin current, int lineNumber, string key, string value)
        {
            current.Logger.LogWarning("Going Cooperative replication config ignored invalid value line="
                + lineNumber.ToString(CultureInfo.InvariantCulture)
                + " key="
                + key
                + " value="
                + value);
        }

        private static bool TryParseReplicationCaptureMode(string value, out string mode)
        {
            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "send", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "authoritative", StringComparison.OrdinalIgnoreCase))
            {
                mode = value.Trim().ToLowerInvariant();
                return true;
            }

            mode = string.Empty;
            return false;
        }

        private static bool TryParseReplicationWorldObjectDeltaMode(string value, out string mode)
        {
            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "apply", StringComparison.OrdinalIgnoreCase))
            {
                mode = value.Trim().ToLowerInvariant();
                return true;
            }

            mode = string.Empty;
            return false;
        }
    }
}

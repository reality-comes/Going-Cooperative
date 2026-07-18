[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnet
$compiler = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot "sdk") -Recurse -Filter "csc.dll" |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$managed = Join-Path (Split-Path -Parent $repositoryRoot) "Going Medieval_Data\Managed"
$outputDirectory = Join-Path $repositoryRoot "artifacts\tests"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory "AgentPresentationPolicyTests.exe"
$sources = @(
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\Replication\ReplicationAgentPresentationContracts.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\AgentPresentationOrderingPolicy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\AgentWorkPresentationPolicy.cs"),
    (Join-Path $repositoryRoot "tests\AgentPresentationPolicyTests.cs")
)

& $dotnet $compiler -noconfig -nostdlib+ -target:exe -langversion:10.0 -nullable:enable "-out:$output" `
    "-r:$(Join-Path $managed 'mscorlib.dll')" `
    "-r:$(Join-Path $managed 'System.dll')" `
    "-r:$(Join-Path $managed 'System.Core.dll')" `
    "-r:$(Join-Path $managed 'netstandard.dll')" @sources
if ($LASTEXITCODE -ne 0) { throw "Agent presentation policy test compilation failed." }
& $output
if ($LASTEXITCODE -ne 0) { throw "Agent presentation policy tests failed." }

$codecOutput = Join-Path $outputDirectory "ReplicationMotionCodecTests.exe"
$codecSources = @(
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\CommandKind.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\DeterminismHash.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommand.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\TransportContracts.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\Replication\ReplicationAgentPresentationContracts.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\Replication\ReplicationMessages.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\Replication\ReplicationPayloadCodec.cs"),
    (Join-Path $repositoryRoot "tests\ReplicationMotionCodecTests.cs")
)

& $dotnet $compiler -noconfig -nostdlib+ -target:exe -langversion:10.0 -nullable:enable "-out:$codecOutput" `
    "-r:$(Join-Path $managed 'mscorlib.dll')" `
    "-r:$(Join-Path $managed 'System.dll')" `
    "-r:$(Join-Path $managed 'System.Core.dll')" `
    "-r:$(Join-Path $managed 'netstandard.dll')" @codecSources
if ($LASTEXITCODE -ne 0) { throw "Replication motion codec test compilation failed." }
& $codecOutput
if ($LASTEXITCODE -ne 0) { throw "Replication motion codec tests failed." }

$semanticMotionSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSemanticMotion.cs"
$semanticMotionSource = Get-Content -LiteralPath $semanticMotionSourcePath -Raw
$semanticMotionRequirements = @(
    @{ Text = "| BindingFlags.Static"; Name = "semantic motion lookup includes static methods" },
    @{ Text = "| BindingFlags.DeclaredOnly"; Name = "semantic motion lookup walks declared methods" },
    @{ Text = "current = current.BaseType"; Name = "semantic motion lookup walks base types" },
    @{ Text = "viewAccessors.GetAgentPathDriver.IsStatic ? null : view"; Name = "static path-driver accessor uses a null invocation target" },
    @{ Text = "viewAccessors.IsRunning.IsStatic ? null : view"; Name = "private-base running accessor supports static or instance invocation" },
    @{ Text = "ReplicationSemanticMotionDiagnosticCategory.PathDriverAccessorMissing"; Name = "path-driver accessor failures are categorized" },
    @{ Text = "ReplicationSemanticMotionLoggedDiagnosticMaskByType"; Name = "semantic motion diagnostics are cached per runtime type" }
)
foreach ($requirement in $semanticMotionRequirements) {
    if (-not $semanticMotionSource.Contains($requirement.Text)) {
        throw "Semantic motion accessor policy failed: $($requirement.Name)."
    }
}

$collectorSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationTransformCollector.cs") -Raw
if ($collectorSource -notmatch "replicationConfigSemanticAgentPresentation\s*&&[\s\S]*?TryCollectReplicationSemanticMotionMetadata") {
    throw "Semantic motion accessor policy failed: semantic collection must remain behind the feature gate."
}

$worldDeltaSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs") -Raw
$targetIdentityRequirements = @(
    @{ Text = 'out var resolvedTargetId'; Name = "nested target IDs are read into a temporary" },
    @{ Text = 'resolvedTargetId != 0L'; Name = "zero-valued nested IDs cannot clear a durable target identity" },
    @{ Text = 'targetId = resolvedTargetId'; Name = "resolved target IDs are merged without clearing prior identity" },
    @{ Text = 'out var resolvedBlueprintId'; Name = "nested target blueprints are read into a temporary" },
    @{ Text = 'out var resolvedX, out var resolvedY, out var resolvedZ'; Name = "nested target coordinates are merged explicitly" }
)
foreach ($requirement in $targetIdentityRequirements) {
    if (-not $worldDeltaSource.Contains($requirement.Text)) {
        throw "Semantic work target policy failed: $($requirement.Name)."
    }
}
if ($worldDeltaSource.Contains('TryReadReplicationWorldObjectLongMember(target, "UniqueId", "uniqueId", out targetId);')) {
    throw "Semantic work target policy failed: a missing nested ID must not clear an identity already collected from ObjectInstance."
}

$smoothingSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSnapshotSmoothing.cs") -Raw
$agentMotionPresentationSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationAgentMotionPresentation.cs") -Raw
$semanticInterpolationRequirements = @(
    @{ Text = "TryApplyReplicationSemanticNormalInterpolation"; Name = "normal interpolation consults semantic path guidance" },
    @{ Text = "smoothSemanticCurves="; Name = "normal interpolation guidance is observable in telemetry" },
    @{ Text = "ReplicationSemanticAnimalPresentationByEntityId.Clear();"; Name = "animal presentation state resets with smoothing" },
    @{ Text = "motion.Gait == ReplicationAgentLocomotionGait.Sprint"; Name = "semantic locomotion carries sprint presentation" }
)
foreach ($requirement in $semanticInterpolationRequirements) {
    if (-not $smoothingSource.Contains($requirement.Text)) {
        throw "Semantic interpolation policy failed: $($requirement.Name)."
    }
}
if (-not $agentMotionPresentationSource.Contains("pathInterpolationGuided=")) {
    throw "Semantic interpolation policy failed: semantic motion telemetry separates interpolation guidance."
}
if (-not $semanticMotionSource.Contains('movementSpeed >= sprintThreshold') -or
    -not $semanticMotionSource.Contains('ReplicationAgentLocomotionGait.Sprint')) {
    throw "Semantic interpolation policy failed: the host must be able to produce Sprint gait metadata."
}
if ($smoothingSource -notmatch "(?s)UpdateReplicationSmoothLocomotion\(.*?IsReplicationSemanticWorkPresentationActive\(entityId\).*?return false;.*?motion\.HasValue") {
    throw "Semantic work ownership policy failed: active work must guard every smooth locomotion branch."
}

$snapshotApplierSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSnapshotApplier.cs") -Raw
if ($snapshotApplierSource -notmatch "semanticWorkActive\s*=\s*IsReplicationSemanticWorkPresentationActive\(entity\.EntityId\)") {
    throw "Semantic work ownership policy failed: the smooth-disabled snapshot path must guard active work."
}

$workSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationAgentWorkPresentation.cs") -Raw
if (-not $workSource.Contains("semanticWorkPresentation: true")) {
    throw "Semantic work animation policy failed: semantic Start must select the isolated direct-view adapter."
}
$semanticWorkGateRequirements = @(
    @{ Text = "replicationConfigSemanticAgentPresentation"; Name = "semantic work remains feature-gated" },
    @{ Text = "AgentWorkPresentationPolicy.TryResolve(goalId, actionId"; Name = "runtime work claims use the tested semantic mapping policy" },
    @{ Text = "ReplicationSemanticWorkAdvisoryDurationSeconds"; Name = "open-ended repair work remains anchored instead of falling back" },
    @{ Text = 'source = "anchored-advisory"'; Name = "advisory duration fallback is observable" },
    @{ Text = "replicationConfigSemanticWorkCycleDriver"; Name = "the cycle driver has an independent rollback gate" },
    @{ Text = "MaintainReplicationSemanticWorkTrigger"; Name = "the narrow client trigger-maintenance controller is present" },
    @{ Text = "UsesClientTriggerMaintenance"; Name = "trigger maintenance is selected through the tested core policy" },
    @{ Text = "UsesHostCyclePulse"; Name = "Dig cycle behavior is selected through the tested core policy" },
    @{ Text = "ReplicationSemanticWorkDigLeaseSeconds"; Name = "Dig has a bounded host-owned presentation lease" },
    @{ Text = "SendReplicationSemanticWorkCyclePulse"; Name = "Dig lease states are sampled and emitted by the host" },
    @{ Text = "TryCaptureReplicationSemanticWorkControllerAnimation"; Name = "observed low-level Dig transitions can advance the next lease sample" },
    @{ Text = 'phase=Pulse'; Name = "Dig cycle pulses have an explicit semantic phase" },
    @{ Text = 'InvokeReplicationSemanticWorkMaintainedTrigger(view, "ForceQuit")'; Name = "maintenance uses vanilla interrupt-mode ForceQuit sequencing" }
)
foreach ($requirement in $semanticWorkGateRequirements) {
    if (-not $workSource.Contains($requirement.Text)) {
        throw "Semantic work migration policy failed: $($requirement.Name)."
    }
}
if ($workSource -notmatch '(?s)TryCaptureReplicationSemanticWorkAnimation\(.*?if \(!CanUseReplicationSemanticHostPresentation\(\)') {
    throw "Semantic work migration policy failed: animation capture must remain behind semantic host capability."
}
if ($workSource -notmatch '(?s)ProcessReplicationSemanticAgentWorkPresentation\(.*?IsReplicationSemanticWorkCyclePulseEnabled\(state\.WorkKind\).*?now >= state\.NextCyclePulseRealtime.*?SendReplicationSemanticWorkCyclePulse') {
    throw "Semantic work migration policy failed: active Dig must periodically renew its authoritative animation lease."
}
if ($workSource -notmatch '(?s)TryRecordReplicationSemanticWorkPhase\(.*?if \(!replicationConfigSemanticAgentPresentation\)') {
    throw "Semantic work migration policy failed: lifecycle capture must remain behind semanticAgentPresentation."
}
if ($workSource -notmatch '(?s)TryApplyReplicationSemanticWorkDelta\(.*?if \(!replicationConfigSemanticAgentPresentation') {
    throw "Semantic work migration policy failed: client playback must remain behind semanticAgentPresentation."
}
$authoritativeWorkVisualRequirements = @(
    @{ Text = "ReplicationSemanticWorkInitialVisualSampleSeconds"; Name = "work visuals are sampled after the vanilla transition frame" },
    @{ Text = '" animatorStateB64=" + EncodeReplicationDetailBase64(state.AnimatorStateDetail)'; Name = "work anchors carry authoritative animator state" },
    @{ Text = "ApplyReplicationAnimatorStateDetail(view, animatorStateDetail)"; Name = "clients apply authoritative work animator state" },
    @{ Text = "ApplyReplicationAnimatorStateDetailIfDiverged"; Name = "periodic anchors use drift-only layer correction" },
    @{ Text = "semanticWorkVisualCorrectionsApplied="; Name = "drift corrections are independently observable" },
    @{ Text = "semanticWorkLocalCorrectionsApplied="; Name = "local early-exit repairs are independently observable" },
    @{ Text = "semanticWorkTriggersRearmed="; Name = "maintained animation triggers are independently observable" },
    @{ Text = "semanticWorkTriggerFallbacks="; Name = "fallback trigger timing is independently observable" },
    @{ Text = "semanticWorkForceQuits="; Name = "interrupt-mode ForceQuit transitions are independently observable" },
    @{ Text = "semanticWorkCyclePulsesSent="; Name = "authoritative Dig pulses sent are observable" },
    @{ Text = "semanticWorkCyclePulsesApplied="; Name = "authoritative Dig pulses applied are observable" },
    @{ Text = "semanticWorkVisualAnchorsApplied="; Name = "authoritative visual anchors are observable in telemetry" }
)
foreach ($requirement in $authoritativeWorkVisualRequirements) {
    if (-not $workSource.Contains($requirement.Text)) {
        throw "Semantic work animation policy failed: $($requirement.Name)."
    }
}
$workPumpStart = $workSource.IndexOf("private static void ProcessReplicationSemanticAgentWorkPresentation", [System.StringComparison]::Ordinal)
$workPumpEnd = $workSource.IndexOf("private static bool IsReplicationSemanticWorkPresentationActive", $workPumpStart, [System.StringComparison]::Ordinal)
if ($workPumpStart -lt 0 -or $workPumpEnd -le $workPumpStart) {
    throw "Semantic work animation policy failed: could not inspect the client hold pump."
}
$workPump = $workSource.Substring($workPumpStart, $workPumpEnd - $workPumpStart)
if ($workPump.Contains('SetInstancePropertyIfPresent(view, view.GetType(), "TriggeredAnimationRunning", true);')) {
    throw "Semantic work animation policy failed: the hold pump must not restart vanilla animation-ended callbacks."
}
if ($workPump.Contains("ApplyReplicationPuppetActionAnimatorParameters(view, state.AnimationToken);")) {
    throw "Semantic work animation policy failed: the hold pump must not overwrite authoritative work parameters."
}
if (-not $workPump.Contains("state.AnimatorStateDetail") -or
    -not $workPump.Contains("ApplyReplicationAnimatorStateDetailIfDiverged")) {
    throw "Semantic work animation policy failed: the client hold pump must repair early animation-state exits locally."
}
$triggerMaintenanceStart = $workSource.IndexOf("private static void MaintainReplicationSemanticWorkTrigger", [System.StringComparison]::Ordinal)
$triggerMaintenanceEnd = $workSource.IndexOf("private static void ProcessReplicationSemanticAgentWorkPresentation", $triggerMaintenanceStart, [System.StringComparison]::Ordinal)
if ($triggerMaintenanceStart -lt 0 -or $triggerMaintenanceEnd -le $triggerMaintenanceStart) {
    throw "Semantic work animation policy failed: could not inspect trigger maintenance."
}
$triggerMaintenance = $workSource.Substring($triggerMaintenanceStart, $triggerMaintenanceEnd - $triggerMaintenanceStart)
if ($triggerMaintenance.Contains("animator.Play")) {
    throw "Semantic work animation policy failed: trigger maintenance must not replay animator states directly."
}

$legacyTriggerStart = $worldDeltaSource.IndexOf("private static string InvokeReplicationAgentViewAnimationTrigger", [System.StringComparison]::Ordinal)
$semanticTriggerStart = $worldDeltaSource.IndexOf("private static string InvokeReplicationSemanticWorkAnimationTrigger", [System.StringComparison]::Ordinal)
$nativeTriggerStart = $worldDeltaSource.IndexOf("private static bool TryInvokeReplicationNativeAnimationController", [System.StringComparison]::Ordinal)
if ($legacyTriggerStart -lt 0 -or $semanticTriggerStart -le $legacyTriggerStart -or $nativeTriggerStart -le $semanticTriggerStart) {
    throw "Semantic work animation policy failed: could not inspect legacy and semantic trigger adapters."
}
$legacyTriggerBlock = $worldDeltaSource.Substring($legacyTriggerStart, $semanticTriggerStart - $legacyTriggerStart)
$semanticTriggerBlock = $worldDeltaSource.Substring($semanticTriggerStart, $nativeTriggerStart - $semanticTriggerStart)
if ($legacyTriggerBlock.IndexOf("TryInvokeReplicationNativeAnimationController", [System.StringComparison]::Ordinal) -gt
    $legacyTriggerBlock.IndexOf('FindReplicationInstanceMethod(view.GetType(), "OnTriggerAnimation"', [System.StringComparison]::Ordinal)) {
    throw "Semantic work animation policy failed: the shared legacy/combat adapter must remain native-first."
}
if ($semanticTriggerBlock.IndexOf('FindReplicationInstanceMethod(view.GetType(), "OnTriggerAnimation"', [System.StringComparison]::Ordinal) -gt
    $semanticTriggerBlock.IndexOf("TryInvokeReplicationNativeAnimationController", [System.StringComparison]::Ordinal)) {
    throw "Semantic work animation policy failed: semantic work must deliver to the resolved view before native fallback."
}
if ($semanticTriggerBlock.Contains("ApplyReplicationPuppetActionAnimatorParameters")) {
    throw "Semantic work animation policy failed: semantic Start must preserve authoritative work parameters."
}
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Test-SemanticWorkGameSurfaces.ps1")
if ($LASTEXITCODE -ne 0) { throw "Semantic work installed-game surface tests failed." }
Write-Host "PASS SemanticMotionAccessorPolicy"
Write-Host "PASS SemanticWorkTargetIdentityPolicy"
Write-Host "PASS SemanticInterpolationPolicy"
Write-Host "PASS SemanticWorkAnimationPolicy"

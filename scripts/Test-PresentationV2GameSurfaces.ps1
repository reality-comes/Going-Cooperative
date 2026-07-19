[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$configSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$needsSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationNeeds.cs"
$worldObjectDeltaSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
$smoothingSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSnapshotSmoothing.cs"
$snapshotApplierSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSnapshotApplier.cs"
$hostMovementSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationHostMovementAuthority.cs"
$configPath = Join-Path $repositoryRoot "config\replication.cfg"

foreach ($path in @($cecilPath, $gameAssemblyPath, $configSourcePath, $needsSourcePath, $worldObjectDeltaSourcePath, $smoothingSourcePath, $snapshotApplierSourcePath, $hostMovementSourcePath, $configPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Presentation-v2 contract input missing: $path" }
}

$configSource = Get-Content -LiteralPath $configSourcePath -Raw
$needsSource = Get-Content -LiteralPath $needsSourcePath -Raw
$worldObjectDeltaSource = Get-Content -LiteralPath $worldObjectDeltaSourcePath -Raw
$smoothingSource = Get-Content -LiteralPath $smoothingSourcePath -Raw
$snapshotApplierSource = Get-Content -LiteralPath $snapshotApplierSourcePath -Raw
$hostMovementSource = Get-Content -LiteralPath $hostMovementSourcePath -Raw
$config = Get-Content -LiteralPath $configPath -Raw

foreach ($required in @(
    "replicationConfigHostSleepPresentationV2",
    "hostSleepPresentationV2",
    "ApplyReplicationNeedsHostSleepPresentationV2",
    "ReplicationSleepPresentationV2State",
    "VisualAppliedForDesiredState",
    "sleep-v2 visual applied",
    "IsReplicationNeedsSleepPresentationActive",
    "TryStabilizeReplicationNeedsSleepPresentationPose",
    "sleep-v2 pose locked",
    "ReplicationNeedsFloatingElementHolderRefreshPositionPrefix",
    "sleep-v2 overlay hook locked",
    "ReplicationNeedsIsSleepingPrefix",
    "suppressed contradictory client IsSleeping write",
    "Sleep=suppressed-authoritative-sleep",
    "ApplyReplicationNeedsWakeVisualCleanup",
    "replicationConfigSemanticAnimalPresentationV2",
    "semanticAnimalPresentationV2",
    "ApplyReplicationSemanticAnimalLocomotionV2",
    "TryApplyReplicationSemanticAnimalLocomotionWithoutMetadataV2",
    "ObserveReplicationSemanticAnimalAuthoritativeFacing",
    "ReplicationPresentationAnimalV2MoveStopConfirmSeconds",
    "ApplyReplicationAuthoritativeAnimalTargetRotation",
    "animatedView.TargetRotation = rotation")) {
    if (($configSource + $needsSource + $worldObjectDeltaSource + $smoothingSource + $snapshotApplierSource + $config) -notmatch [regex]::Escape($required)) {
        throw "Presentation-v2 source contract missing: $required"
    }
}

$installerMatch = [regex]::Match(
    $hostMovementSource,
    'private void TryInstallReplicationHostMovementAuthority[\s\S]*?private int TryPatchReplicationHostMovementMethod')
if (-not $installerMatch.Success) {
    throw "Host-movement patch installer source contract missing."
}
if ($installerMatch.Value -match 'replicationConfigEnabled|replicationConfigHostMode|replicationConfigApplySnapshots') {
    throw "Host-movement installer incorrectly depends on the pre-UI session role/state."
}
if ($installerMatch.Value -notmatch 'replicationConfigForceHostMovement') {
    throw "Host-movement installer feature gate missing."
}
if ($hostMovementSource -notmatch 'ReplicationHostMovementPrefix[\s\S]*?replicationConfigEnabled[\s\S]*?replicationConfigHostMode[\s\S]*?replicationConfigApplySnapshots') {
    throw "Host-movement live prefix safety guards missing."
}

if ($config -notmatch '(?m)^hostSleepPresentationV2=true$' -or
    $config -notmatch '(?m)^semanticAnimalPresentationV2=true$') {
    throw "Presentation-v2 tested package gates must be enabled."
}
if ($needsSource -notmatch 'pending\.HasSleep\s*&&\s*replicationConfigHostSleepPresentationV2\s*&&\s*pending\.IsSleeping') {
    throw "Sleep-v2 periodic character-state stat suppression contract missing."
}
$sleepCaptureMatch = [regex]::Match(
    $worldObjectDeltaSource,
    'private static void ReplicationActionAnimationTriggerPostfix[\s\S]*?private static void ReplicationGoapActionLifecyclePostfix')
if (-not $sleepCaptureMatch.Success -or
    $sleepCaptureMatch.Value -notmatch 'replicationConfigHostSleepPresentationV2[\s\S]*?"Sleep"[\s\S]*?"Rest"[\s\S]*?return;[\s\S]*?TryCaptureReplicationSemanticWorkAnimation') {
    throw "Sleep-v2 generic action-animation carveout contract missing."
}
if ($smoothingSource -notmatch 'MovementSpeed\s*<\s*ReplicationPresentationAnimalV2TangentMinimumSpeed') {
    throw "Animal-v2 low-speed tangent bypass contract missing."
}
if ($smoothingSource -notmatch 'UpdateReplicationSmoothLocomotion[\s\S]*?IsReplicationNeedsSleepPresentationActive\(entityId\)[\s\S]*?return false;') {
    throw "Sleep-v2 locomotion presentation ownership contract missing."
}
if ($smoothingSource -notmatch 'EvaluateReplicationPresentationTrack[\s\S]*?TryStabilizeReplicationNeedsSleepPresentationPose[\s\S]*?view\.Transform\.position') {
    throw "Sleep-v2 interpolation-aligned pose target contract missing."
}

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)
function Get-AllTypes($types) {
    foreach ($type in $types) {
        $type
        Get-AllTypes $type.NestedTypes
    }
}
$types = @(Get-AllTypes $assembly.MainModule.Types)
$creatureType = $types | Where-Object FullName -eq "NSMedieval.State.CreatureBase"
if ($null -eq $creatureType) { throw "CreatureBase native sleep surface missing." }
$sleepGetter = $creatureType.Methods | Where-Object Name -eq "get_IsSleeping" | Select-Object -First 1
$sleepSetter = $creatureType.Methods | Where-Object Name -eq "set_IsSleeping" | Select-Object -First 1
if ($null -eq $sleepGetter -or $null -eq $sleepSetter -or -not $sleepSetter.HasBody) {
    throw "CreatureBase.IsSleeping getter/setter native contract missing."
}
$setterIl = ($sleepSetter.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
if ($setterIl -notmatch 'AnimatedAgentView::TrySetParameter' -or $setterIl -notmatch 'ldstr "Sleep"') {
    throw "CreatureBase.IsSleeping no longer drives the native Sleep animator parameter."
}

$sleepGoalType = $types | Where-Object FullName -eq "NSMedieval.Goap.Goals.SleepGoal"
if ($null -eq $sleepGoalType) { throw "SleepGoal native surface missing." }
$sleepGoalIl = ($sleepGoalType.Methods | Where-Object HasBody | ForEach-Object { $_.Body.Instructions } | ForEach-Object { $_.ToString() }) -join "`n"
if ($sleepGoalIl -notmatch 'set_IsSleeping') {
    throw "SleepGoal no longer owns the native IsSleeping transition."
}

$workerViewType = $types | Where-Object FullName -eq "NSMedieval.View.WorkerView"
if ($null -eq $workerViewType) { throw "WorkerView native overlay surface missing." }
$overlayHookField = $workerViewType.Fields | Where-Object Name -eq "gameplayOverlayHook" | Select-Object -First 1
$overlayHookMethod = $workerViewType.Methods | Where-Object Name -eq "GetGuiOverlayHookTransform" | Select-Object -First 1
if ($null -eq $overlayHookField -or $null -eq $overlayHookMethod -or -not $overlayHookMethod.HasBody) {
    throw "WorkerView gameplay overlay hook contract missing."
}
$overlayHookIl = ($overlayHookMethod.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
if ($overlayHookIl -notmatch 'gameplayOverlayHook') {
    throw "WorkerView GUI overlay no longer prefers gameplayOverlayHook."
}

$holderType = $types | Where-Object FullName -eq "NSMedieval.FloatingOverlaySystem.FloatingElementHolder"
$holderRefresh = $holderType.Methods | Where-Object Name -eq "RefreshPosition" | Select-Object -First 1
if ($null -eq $holderType -or $null -eq $holderRefresh -or -not $holderRefresh.HasBody) {
    throw "FloatingElementHolder.RefreshPosition native surface missing."
}
$holderRefreshIl = ($holderRefresh.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
if ($holderRefreshIl -notmatch 'followingToTransform') {
    throw "FloatingElementHolder.RefreshPosition no longer follows its transform directly."
}

Write-Output "PASS PresentationV2 sleep-state/animal-motion/native contracts"

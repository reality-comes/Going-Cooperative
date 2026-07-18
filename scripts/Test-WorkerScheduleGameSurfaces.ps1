[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"

if (-not (Test-Path -LiteralPath $cecilPath -PathType Leaf)) { throw "Mono.Cecil is missing at $cecilPath." }
if (-not (Test-Path -LiteralPath $gameAssemblyPath -PathType Leaf)) { throw "Assembly-CSharp is missing at $gameAssemblyPath." }

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)

function Get-TypeTree {
    param([Parameter(Mandatory)] $Type)
    $Type
    foreach ($nested in $Type.NestedTypes) { Get-TypeTree -Type $nested }
}

$types = @($assembly.MainModule.Types | ForEach-Object { Get-TypeTree -Type $_ })
function Get-GameType {
    param([Parameter(Mandatory)][string] $FullName)
    $type = $types | Where-Object FullName -eq $FullName | Select-Object -First 1
    if ($null -eq $type) { throw "Game type is missing: $FullName" }
    return $type
}

function Assert-Method {
    param(
        [Parameter(Mandatory)] $Type,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ParameterTypes
    )
    $method = $Type.Methods | Where-Object {
        $_.Name -eq $Name -and
        $_.Parameters.Count -eq $ParameterTypes.Count -and
        (Compare-Object @($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) $ParameterTypes).Count -eq 0
    } | Select-Object -First 1
    if ($null -eq $method) { throw "$($Type.FullName).$Name($($ParameterTypes -join ',')) is missing." }
    return $method
}

function Assert-Calls {
    param(
        [Parameter(Mandatory)] $Method,
        [Parameter(Mandatory)][string] $Target
    )
    $operands = @($Method.Body.Instructions | ForEach-Object { [string]$_.Operand })
    if (-not ($operands -match [regex]::Escape($Target))) {
        throw "$($Method.FullName) no longer reaches $Target."
    }
}

$hourType = "NSMedieval.Goap.HourType"
$soundButton = "NSEipix.View.UI.SoundButton"
$scheduleManager = Get-GameType "NSMedieval.UI.WorkerScheduleManager"
$changeFromButton = Assert-Method $scheduleManager "ChangeHourType" @($soundButton, $hourType)
$changeFromIndex = Assert-Method $scheduleManager "ChangeHourType" @("System.Int32", $hourType)
$loadHourColours = Assert-Method $scheduleManager "LoadHourColours" @()
$setWorker = Assert-Method $scheduleManager "SetWorker" @("NSMedieval.State.HumanoidInstance", "NSMedieval.UI.WorkerPanelManager")
Assert-Calls $changeFromButton "WorkerScheduleManager::ChangeHourType(System.Int32,NSMedieval.Goap.HourType)"
Assert-Calls $changeFromIndex "HumanoidInstance::ChangeSchedule(System.Int32,NSMedieval.Goap.HourType)"
Assert-Calls $loadHourColours "WorkerScheduleManager::ChangeHourType(NSEipix.View.UI.SoundButton,NSMedieval.Goap.HourType)"
Assert-Calls $setWorker "WorkerScheduleManager::LoadHourColours()"

$schedulePanel = Get-GameType "NSMedieval.UI.SchedulePanelManager"
$pasteToWorker = Assert-Method $schedulePanel "PasteToWorker" @("NSMedieval.State.HumanoidInstance")
Assert-Calls $pasteToWorker "HumanoidInstance::ChangeSchedule(System.Int32,NSMedieval.Goap.HourType)"

$humanoid = Get-GameType "NSMedieval.State.HumanoidInstance"
[void](Assert-Method $humanoid "ChangeSchedule" @("System.Int32", $hourType))

$changeScheduleCallers = @($types | ForEach-Object {
    $_.Methods | Where-Object { $_.HasBody } | Where-Object {
        @($_.Body.Instructions | ForEach-Object { [string]$_.Operand }) -match
            [regex]::Escape("HumanoidInstance::ChangeSchedule(System.Int32,NSMedieval.Goap.HourType)")
    }
})
$changeScheduleCallerNames = @($changeScheduleCallers | ForEach-Object { $_.FullName })
if (($changeScheduleCallers.Count -ne 2) -or ($changeScheduleCallerNames -notcontains $changeFromIndex.FullName) -or ($changeScheduleCallerNames -notcontains $pasteToWorker.FullName)) {
    throw "HumanoidInstance.ChangeSchedule caller topology changed: $($changeScheduleCallers.FullName -join '; ')."
}

$managementSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationManagement.cs") -Raw
$runtimeSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs") -Raw
$worldDeltaSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs") -Raw
$commandSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandApplication.cs") -Raw
$captureSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.cs") -Raw
$payloadSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs") -Raw
foreach ($marker in @(
    '"NSMedieval.State.HumanoidInstance"',
    '"ChangeSchedule"',
    'nameof(ReplicationWorkerScheduleModelPrefix)',
    'nameof(ReplicationWorkerScheduleModelPostfix)',
    'nameof(ReplicationWorkerSchedulePastePrefix)',
    'nameof(ReplicationWorkerSchedulePastePostfix)',
    'replicationWorkerScheduleAuthoritativeApplyDepth',
    'currentValue == requestedValue',
    'SendReplicationManagementIntent(__state, "worker-schedule-model")',
    'SendReplicationManagementIntent(updatePayload, "worker-schedule-paste")',
    'CreateWorkerScheduleUpdatePayload',
    'TryApplyReplicationWorkerScheduleUpdate',
    'TryApplyReplicationWorkerScheduleState',
    'CompleteReplicationWorkerScheduleStateProof',
    'TryCreateReplicationWorkerScheduleStatePayload',
    'result.Invoked ? "accepted-command" : "rejected-command-correction"',
    'ReplicationHostWorkerScheduleIntentSequenceByHour',
    'replicationApplyingRemoteManagementCommandSequence',
    'TryReadReplicationWorkerScheduleHour',
    'TryResolveReplicationWorkerScheduleColor',
    'button.GetComponent(imageType)',
    'colorProperty.SetValue(image, colors[values[cell]], null)')) {
    if (-not $managementSource.Contains($marker)) { throw "Worker Schedule replication source is missing $marker." }
}
if ($managementSource.Contains('ReplicationWorkerScheduleButtonPrefix')) {
    throw "Worker Schedule still depends on the UI-only capture that misses copy/paste."
}
if (-not $runtimeSource.Contains('replicationWorkerScheduleAuthoritativeApplyDepth = 0;')) {
    throw "Worker Schedule apply guard is not reset with the replication runtime."
}
foreach ($marker in @(
    'replicationApplyingRemoteManagementCommandSequence = command.Sequence;',
    'replicationApplyingRemoteManagementCommandSequence = 0L;',
    'ReplicationHostWorkerScheduleIntentSequenceByHour.Clear();',
    'ReplicationDeferredPreHelloEnvelopes.Clear();',
    'ignored-incompatible-or-prehello',
    'replicationLastRemoteHelloRealtime = Time.realtimeSinceStartup;',
    'ReplicationManagementWireVersion = "3"',
    '"|management="')) {
    if (-not $runtimeSource.Contains($marker)) { throw "Worker Schedule runtime is missing $marker." }
}
foreach ($marker in @(
    'TryReadWorkerScheduleUpdatePayload',
    'TryApplyReplicationWorkerScheduleUpdate')) {
    if (-not $commandSource.Contains($marker)) { throw "Worker Schedule command application is missing $marker." }
}
foreach ($marker in @(
    'IsReplicationWorkerScheduleUpdateCommand',
    'ReplicationManagementIntentDormantRetrySeconds',
    'CoalesceReplicationPendingWorkerScheduleIntent',
    'ReplicationPendingWorkerScheduleIntentTargetLimit',
    'ReplicationRemoteHelloFreshSeconds')) {
    if (-not $captureSource.Contains($marker)) { throw "Worker Schedule reliable intent path is missing $marker." }
}
foreach ($marker in @(
    'WorkerScheduleUpdateAction',
    'WorkerScheduleStateAction',
    'TryReadWorkerScheduleUpdatePayload',
    'TryReadWorkerScheduleStatePayload')) {
    if (-not $payloadSource.Contains($marker)) { throw "Worker Schedule payload contract is missing $marker." }
}
if (-not $worldDeltaSource.Contains('|policy=WorkerScheduleState|target=')) {
    throw "Worker Schedule full state lacks a worker-scoped ordering/coalescing key."
}
foreach ($marker in @(
    'IsReplicationWorkerScheduleStateDelta',
    'ReplicationWorkerScheduleStateDurableRetrySeconds',
    'durableWorkerScheduleState')) {
    if (-not $worldDeltaSource.Contains($marker)) { throw "Worker Schedule durable state path is missing $marker." }
}
if (-not $worldDeltaSource.Contains('every ManagementState uses the same empty')) {
    throw "Worker Schedule state is not exempt from the generic envelope-only duplicate filter."
}

Write-Host "PASS WorkerScheduleGameSurfaces click/paste/model/reliable-intent/guard/state/targeted-refresh contracts"

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
        [Parameter(Mandatory)][string[]] $ParameterTypes
    )
    $method = $Type.Methods | Where-Object {
        $_.Name -eq $Name -and
        $_.Parameters.Count -eq $ParameterTypes.Count -and
        (Compare-Object @($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) $ParameterTypes).Count -eq 0
    } | Select-Object -First 1
    if ($null -eq $method) { throw "$($Type.FullName).$Name($($ParameterTypes -join ',')) is missing." }
    return $method
}

$worker = Get-GameType "NSMedieval.State.WorkerBehaviour"
[void](Assert-Method $worker "SetSelfTendingAllowed" @("System.Boolean"))
[void](Assert-Method $worker "set_UseRallyPoints" @("System.Boolean"))
[void](Assert-Method $worker "UpdateSingleManagePreset" @("System.String", "System.String", "System.Boolean"))

$row = Get-GameType "NSMedieval.UI.WorkerManageRowItem"
$selfTend = Assert-Method $row "OnSelfTendValueChange" @("System.Boolean")
$rally = Assert-Method $row "OnUseRallyPointsChange" @("System.Boolean")
$preset = Assert-Method $row "ChangePreset" @("System.String", "System.String")

if (-not (($selfTend.Body.Instructions | ForEach-Object { [string]$_.Operand }) -match "WorkerBehaviour::SetSelfTendingAllowed")) {
    throw "WorkerManageRowItem.OnSelfTendValueChange no longer reaches SetSelfTendingAllowed."
}
if (-not (($rally.Body.Instructions | ForEach-Object { [string]$_.Operand }) -match "WorkerBehaviour::set_UseRallyPoints")) {
    throw "WorkerManageRowItem.OnUseRallyPointsChange no longer reaches UseRallyPoints."
}
if (-not (($preset.Body.Instructions | ForEach-Object { [string]$_.Operand }) -match "WorkerBehaviour::UpdateSingleManagePreset")) {
    throw "WorkerManageRowItem.ChangePreset no longer reaches UpdateSingleManagePreset."
}

$managementSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationManagement.cs") -Raw
$worldDeltaSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs") -Raw
$commandSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandApplication.cs") -Raw
$payloadSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs") -Raw
foreach ($marker in @(
    '"WorkerSelfTend"',
    '"WorkerRallyPoints"',
    'ReplicationWorkerManageBooleanPrefix',
    'ReplicationWorkerManagePresetPrefix',
    'ReplicationWorkerManageBooleanUiPostfix',
    'ReplicationWorkerManagePresetUiPostfix',
    'TryApplyReplicationWorkerManagePreset',
    'replicationWorkerManageAuthoritativeApplyDepth',
    'SendReplicationManagementDelta(command.PayloadJson, "accepted-command")',
    '__2 = false;',
    'RefreshReplicationWorkerManageUi')) {
    if (-not $managementSource.Contains($marker)) { throw "Worker Manage replication source is missing $marker." }
}
if ($managementSource.Contains('if (replicationConfigHostMode)' + [Environment]::NewLine + '            {' + [Environment]::NewLine + '                return "host-model";')) {
    throw "Worker Manage authoritative apply still skips host UI refresh."
}
if (-not $worldDeltaSource.Contains('every ManagementState uses the same empty')) {
    throw "Worker Manage state is not exempt from the generic envelope-only duplicate filter."
}
if (-not $commandSource.Contains('TryReadWorkerManagePresetPayload')) { throw "Worker Manage command application is missing preset parsing." }
if (-not $payloadSource.Contains('WorkerManagePresetAction')) { throw "Worker Manage core payload contract is missing." }

Write-Host "PASS WorkerManageGameSurfaces self-tend/rally/preset assignment contracts"

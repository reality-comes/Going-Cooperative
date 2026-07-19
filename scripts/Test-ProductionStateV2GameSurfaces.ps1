[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$productionPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationProductionStateV2.cs"
$runtimePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$managementPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationManagement.cs"
$containersPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationResourceContainers.cs"
$workstationPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorkstationRuntimePresentation.cs"
$configPath = Join-Path $repositoryRoot "config\replication.cfg"
$configSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"

foreach ($path in @($cecilPath, $gameAssemblyPath, $productionPath, $runtimePath, $managementPath, $containersPath, $workstationPath, $configPath, $configSourcePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Production-v2 contract input missing: $path" }
}

$production = Get-Content -LiteralPath $productionPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$management = Get-Content -LiteralPath $managementPath -Raw
$containers = Get-Content -LiteralPath $containersPath -Raw
$workstation = Get-Content -LiteralPath $workstationPath -Raw
$config = Get-Content -LiteralPath $configPath -Raw
$configSource = Get-Content -LiteralPath $configSourcePath -Raw

foreach ($marker in @(
    'replicationConfigProductionStateV2',
    'ReplicationProductionTicketIdentityV2',
    'ReplicationProductionTicketV2ByHostId',
    'ReplicationProductionGetComponentMethodByBuildingType',
    'ReplicationProductionProgressDirtyQueue',
    'ReplicationProductionContainerDirtyQueue',
    'ReplicationProductionRecoverySeconds',
    'production-v2-primary',
    'production-v2-secondary',
    'CreateProductionQueueV2Payload',
    'TryApplyReplicationProductionQueueV2')) {
    if (($production + $management + $containers) -notmatch [regex]::Escape($marker)) {
        throw "Production-v2 source contract missing: $marker"
    }
}

$heapScanCount = ([regex]::Matches($production, 'FindObjectsOfTypeAll')).Count
if ($heapScanCount -ne 1) { throw "Production-v2 must contain exactly one bounded bootstrap heap scan; found $heapScanCount." }
if ($production -notmatch 'replicationProductionBootstrapCollectedV2' -or
    $production -notmatch 'ReplicationProductionBootstrapViewsPerFrame') {
    throw "Production-v2 bootstrap is not one-shot and frame-budgeted."
}
if ($production -match 'AccessTools\.Property\(record\.') {
    throw "Production-v2 dirty drain performs uncached property discovery."
}
if ($production -notmatch 'record\.LastProgressFingerprint = string\.Empty' -or
    $production -notmatch 'ForceContainerCheckpoint' -or
    $production -notmatch 'previous\.Revision \+ 1L') {
    throw "Production-v2 recovery does not force authoritative progress and container checkpoints."
}
if ($production -notmatch 'record\.QueueIndex < 0') {
    throw "Production-v2 does not suppress removed production tickets."
}

$runtimeBranch = [regex]::Match($runtime, 'SendHostReplicationResourceContainersIfDue\(\);[\s\S]*?if \(!replicationConfigProductionStateV2\)[\s\S]*?UpdateReplicationWorkstationRuntimePresentation\(\);')
if (-not $runtimeBranch.Success) {
    throw "Production-v2 runtime does not keep agent inventory sending while gating only legacy workstation presentation."
}
if ($containers -notmatch 'CollectReplicationAgentStorageContainer[\s\S]*?if \(!replicationConfigProductionStateV2\)[\s\S]*?CollectReplicationProductionStorageContainers') {
    throw "Production-v2 container routing does not preserve agent inventories while excluding legacy production containers."
}
if ($configSource -notmatch 'agent-inventory="[\s\S]*?production-containers="' -or
    $configSource -notmatch 'agent-inventory=none') {
    throw "Replication startup does not declare resolved inventory ownership or warn on an orphaned lane."
}
if ($workstation -notmatch 'TryResolveOrBindReplicationProductionTicketV2' -or
    $workstation -notmatch 'TryFindReplicationProductionComponentV2AtGrid') {
    throw "Workstation progress does not consume the production-v2 registry."
}
if ($containers -notmatch 'TryApplyReplicationProductionStorageContainerV2') {
    throw "Resource containers do not consume stable production-v2 ticket identities."
}
if ($production -notmatch 'IdentityPublished' -or
    $production -notmatch 'replicationRemoteHelloReceived' -or
    $production -notmatch 'packet-self-bind') {
    throw "Production-v2 identity publication is not connection-aware and self-healing."
}
if ($production -notmatch 'TryApplyReplicationProductionRuntimeStateV2' -or
    $production -notmatch 'HasAuthoritativeRuntimeState' -or
    $production -notmatch 'ReplicationProductionStateV2Prefix') {
    throw "Production-v2 does not project and protect authoritative ticket state."
}
foreach ($gate in @('productionStateV2', 'productionTicketOrdersV2', 'workstationRuntimePresentation', 'resourceContainerReplication')) {
    if ($config -notmatch "(?m)^$gate=true\r?$") { throw "Production-v2 tested package gate must be enabled: $gate" }
}

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)
function Get-TypeTree {
    param([Parameter(Mandatory)] $Type)
    $Type
    foreach ($nested in $Type.NestedTypes) { Get-TypeTree -Type $nested }
}
$types = @($assembly.MainModule.Types | ForEach-Object { Get-TypeTree -Type $_ })
function Require-Type([string] $fullName) {
    $type = $types | Where-Object FullName -eq $fullName | Select-Object -First 1
    if ($null -eq $type) { throw "Production-v2 native type missing: $fullName" }
    return $type
}
function Require-Method($type, [string] $name) {
    if ($null -eq ($type.Methods | Where-Object Name -eq $name | Select-Object -First 1)) {
        throw "Production-v2 native method missing: $($type.FullName).$name"
    }
}

$system = Require-Type 'NSMedieval.State.ProductionSystemInstance'
foreach ($name in @('AddNewProduction', 'RemoveProduction', 'ChangePriority', 'SetCurrentActiveProduction')) { Require-Method $system $name }
$ticket = Require-Type 'NSMedieval.State.ProductionInstance'
foreach ($name in @('SetProductTargetCount', 'SetOrder', 'StorageItemUpdated', 'DeliverResource', 'StartStep', 'EndCurrentStep')) { Require-Method $ticket $name }
$step = Require-Type 'NSMedieval.State.ProductionStepInstance'
if ($null -eq ($step.Fields | Where-Object Name -eq 'ownerProductionInstance' | Select-Object -First 1)) {
    throw 'ProductionStepInstance.ownerProductionInstance native identity link is missing.'
}
$component = Require-Type 'NSMedieval.BuildingComponents.ProductionComponent'
foreach ($name in @('PreSpawnInitialization', 'OnBaseBuildingEnterFinishedState', 'OnCurrentProductionChanged')) { Require-Method $component $name }

Write-Host 'PASS ProductionStateV2GameSurfaces'

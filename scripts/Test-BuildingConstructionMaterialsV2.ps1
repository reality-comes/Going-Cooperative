[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $root
$sourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildingConstructionMaterialsV2.cs"
$containersPath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationResourceContainers.cs"
$lifecyclePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildingLifecycleV2.cs"
$configSourcePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$runtimePath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$pluginPath = Join-Path $root "src\GoingCooperative.Plugin.BepInEx\Plugin.cs"
$policyPath = Join-Path $root "src\GoingCooperative.Core\BuildingReplicationV2Policy.cs"
$configPath = Join-Path $root "config\replication.cfg"
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"

foreach ($path in @($sourcePath, $containersPath, $lifecyclePath, $configSourcePath, $runtimePath, $pluginPath, $policyPath, $configPath, $cecilPath, $gameAssemblyPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Construction-material V2 verification input missing: $path" }
}

$source = Get-Content -LiteralPath $sourcePath -Raw
$containers = Get-Content -LiteralPath $containersPath -Raw
$lifecycle = Get-Content -LiteralPath $lifecyclePath -Raw
$configSource = Get-Content -LiteralPath $configSourcePath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$plugin = Get-Content -LiteralPath $pluginPath -Raw
$policy = Get-Content -LiteralPath $policyPath -Raw
$config = Get-Content -LiteralPath $configPath -Raw

if ($config -notmatch '(?m)^buildingConstructionMaterialsV2=true\r?$') { throw "The tested package does not enable buildingConstructionMaterialsV2." }
if ($configSource -notmatch 'replicationConfigBuildingConstructionMaterialsV2' -or $configSource -notmatch 'case "buildingconstructionmaterialsv2"') { throw "The construction-material gate is not declared and parsed." }
if ($plugin -notmatch 'TryInstallReplicationBuildingConstructionMaterialsV2Hooks\(harmony\)') { throw "Construction-material mutation hooks are not installed." }
foreach ($marker in @(
    'OnResourceAdded',
    'DropConstructionResources',
    'ReplicationBuildingConstructionMaterialsDirtyQueueV2',
    'ReplicationBuildingConstructionMaterialsForcedSetV2',
    'ReplicationBuildingConstructionMaterialsRecoveryBudgetV2',
    'TryEnsureReplicationHostBuildingTrackerV2',
    'GetReplicationBuildBatchEpoch',
    'TryResolveReplicationBuildingTargetV2',
    'TryReadReplicationStorageEntries',
    'TryMutateReplicationStorage')) {
    if (-not $source.Contains($marker)) { throw "Construction-material source contract missing: $marker" }
}
if ($source.Contains('Resources.FindObjectsOfTypeAll')) { throw "Construction-material replication must not perform heap scans." }
if ($source -notmatch 'ReplicationBuildingConstructionMaterialsDirtySetV2\.Add\(hostId\)[\s\S]*?Enqueue\(hostId\)') { throw "Repeated deliveries are not coalesced by authoritative building ID." }
if ($source -notmatch 'replicationBuildingConstructionMaterialsRecoveryRemainingV2[\s\S]*?ReplicationBuildingConstructionMaterialsRecoveryBudgetV2') { throw "Recovery is not spread across frames." }
if ($source -notmatch 'phase\.IndexOf\("Finished"[\s\S]*?return true') { throw "A delayed material state can repopulate a finished building." }
if ($containers -notmatch 'ReplicationBuildingConstructionMaterialsKindV2[\s\S]*?TryApplyReplicationBuildingConstructionMaterialsV2') { throw "Resource-container dispatch does not route construction materials." }
if ($containers -notmatch 'applyingRuntimeCommandDepth\+\+[\s\S]*?TryApplyReplicationResourceContainer[\s\S]*?applyingRuntimeCommandDepth--') { throw "Construction storage apply is not protected by the no-rebound mutation scope." }
if ($lifecycle -notmatch 'UpdateReplicationBuildingConstructionMaterialsV2\(\)' -or $lifecycle -notmatch 'ResetReplicationBuildingConstructionMaterialsV2\(\)') { throw "Building lifecycle does not update and reset the material lane." }
if ($runtime -notmatch 'constructionMaterialsV2:\s*replicationConfigBuildingConstructionMaterialsV2') { throw "The building hello capability does not advertise the selected material lane." }
if ($policy -notmatch 'ConstructionMaterialsMismatch' -or $policy -notmatch 'ConstructionMaterialsV2\s*!=\s*remote\.ConstructionMaterialsV2') { throw "Mixed construction-material settings do not fail the handshake." }

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)
$building = $assembly.MainModule.Types | Where-Object FullName -eq 'NSMedieval.BuildingComponents.BaseBuildingInstance' | Select-Object -First 1
$view = $assembly.MainModule.Types | Where-Object FullName -eq 'NSMedieval.BuildingComponents.BaseBuildingViewComponent' | Select-Object -First 1
if ($null -eq $building -or $null -eq $view) { throw "Native building types are missing." }
$storageProperty = $building.Properties | Where-Object Name -eq 'Storage' | Select-Object -First 1
if ($null -eq $storageProperty -or $storageProperty.PropertyType.FullName -ne 'NSMedieval.Components.Storage') { throw "BaseBuildingInstance.Storage native contract changed." }
if (-not ($building.Methods | Where-Object { $_.Name -eq 'OnResourceAdded' -and $_.Parameters.Count -eq 1 })) { throw "BaseBuildingInstance.OnResourceAdded native edge is missing." }
$getResourcesInfo = $view.Methods | Where-Object Name -eq 'GetResourcesInfo' | Select-Object -First 1
if ($null -eq $getResourcesInfo -or -not $getResourcesInfo.HasBody) { throw "Building info-panel resource reader is missing." }
$calls = @($getResourcesInfo.Body.Instructions | ForEach-Object { if ($null -ne $_.Operand) { $_.Operand.ToString() } })
if (-not ($calls | Where-Object { $_ -match 'BaseBuildingInstance::get_Storage' })) { throw "Building UI no longer reads BaseBuildingInstance.Storage." }
if (-not ($calls | Where-Object { $_ -match 'Storage::get_Resources' })) { throw "Building UI no longer reads the construction container contents." }

Write-Host "PASS BuildingConstructionMaterialsV2"

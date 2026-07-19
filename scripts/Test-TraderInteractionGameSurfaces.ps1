[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$interactionSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationTraderInteraction.cs"
$externalSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationExternalEventAgents.cs"
$commandSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandApplication.cs"
$payloadSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs"
foreach ($path in @($cecilPath, $gameAssemblyPath, $interactionSourcePath, $externalSourcePath, $commandSourcePath, $payloadSourcePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Trader-interaction contract input missing: $path" }
}

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)
function Get-AllTypes($types) {
    foreach ($type in $types) {
        $type
        if ($type.HasNestedTypes) { Get-AllTypes $type.NestedTypes }
    }
}
$allTypes = @(Get-AllTypes $assembly.MainModule.Types)
$trader = $allTypes | Where-Object FullName -eq "NSMedieval.State.TraderBehaviour" | Select-Object -First 1
if ($null -eq $trader) { throw "TraderBehaviour native type missing." }
$talk = $trader.Methods | Where-Object {
    $_.Name -eq "OnSettlerTalkTo" -and $_.Parameters.Count -eq 1 -and
    $_.Parameters[0].ParameterType.FullName -eq "NSMedieval.State.WorkerBehaviour"
} | Select-Object -First 1
if ($null -eq $talk) { throw "TraderBehaviour.OnSettlerTalkTo(WorkerBehaviour) drifted." }
$talkIl = ($talk.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
if ($talkIl -notmatch 'TradingManager::OpenTradingMenu' -or $talkIl -notmatch 'ChatGraphManager::StartNew') {
    throw "TraderBehaviour.OnSettlerTalkTo no longer owns both native direct-menu and merchant-dialog paths."
}
$tradeMenu = $allTypes | Where-Object FullName -eq "NSMedieval.AdditionalMenuItems.TradeMenuItem" | Select-Object -First 1
if ($null -eq $tradeMenu) { throw "TradeMenuItem native type missing." }
$tradeMenuClick = $tradeMenu.Methods | Where-Object { $_.Name -eq "OnClickCallback" -and $_.Parameters.Count -eq 0 } | Select-Object -First 1
if ($null -eq $tradeMenuClick) { throw "TradeMenuItem.OnClickCallback() drifted." }
$tradeMenuIl = ($tradeMenuClick.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
if ($tradeMenuIl -notmatch 'GetSelectedWorker' -or $tradeMenuIl -notmatch 'TradeGoal' -or $tradeMenuIl -notmatch 'ForceGoal') {
    throw "TradeMenuItem no longer resolves the selected worker and schedules TradeGoal."
}

$source = (Get-Content -LiteralPath $interactionSourcePath -Raw) +
    (Get-Content -LiteralPath $externalSourcePath -Raw) +
    (Get-Content -LiteralPath $commandSourcePath -Raw) +
    (Get-Content -LiteralPath $payloadSourcePath -Raw)
foreach ($required in @(
    "TraderTradeOpenRequest",
    "TraderTradeOpenResult",
    "TryRequestReplicationTraderInteraction",
    "ReplicationTraderTradeMenuClickPrefix",
    "NSMedieval.AdditionalMenuItems.TradeMenuItem",
    "TryApplyReplicationTraderInteractionOpenRequest",
    "TryGetReplicationTraderPartyNetworkId",
    "TryGetReplicationWorkerBehaviourEntityId",
    "TryResolveReplicationWorkerBehaviour",
    "OnSettlerTalkTo",
    "ReplicationTraderInteractionRequestLeaseSeconds",
    "ResetReplicationTraderInteraction")) {
    if ($source -notmatch [regex]::Escape($required)) { throw "Trader-interaction source contract missing: $required" }
}

Write-Host "PASS TraderInteraction request/identity/native-talk contracts"

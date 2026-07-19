[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$sourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationSynchronizedTrading.cs"
$externalSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationExternalEventAgents.cs"
$runtimeSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$payloadSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs"

foreach ($path in @($cecilPath, $gameAssemblyPath, $sourcePath, $externalSourcePath, $runtimeSourcePath, $payloadSourcePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Synchronized-trading contract input missing: $path" }
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
function Require-Type([string]$name) {
    $type = $allTypes | Where-Object FullName -eq $name | Select-Object -First 1
    if ($null -eq $type) { throw "Native synchronized-trading type missing: $name" }
    $type
}
function Require-Field($type, [string]$name, [string]$fieldType) {
    $field = $type.Fields | Where-Object { $_.Name -eq $name -and $_.FieldType.FullName -eq $fieldType } | Select-Object -First 1
    if ($null -eq $field) { throw "$($type.FullName).$name field contract drifted." }
}
function Require-Method($type, [string]$name, [string[]]$parameters) {
    $method = $type.Methods | Where-Object {
        if ($_.Name -ne $name -or $_.Parameters.Count -ne $parameters.Count) { return $false }
        for ($i = 0; $i -lt $parameters.Count; $i++) {
            if ($_.Parameters[$i].ParameterType.FullName -ne $parameters[$i]) { return $false }
        }
        return $true
    } | Select-Object -First 1
    if ($null -eq $method) { throw "$($type.FullName).$name method contract drifted." }
}

$manager = Require-Type "NSMedieval.UI.TradingManager"
$panel = Require-Type "NSMedieval.UI.TradingPanelView"
$tradeResource = Require-Type "NSMedieval.UI.TradeResource"
$tradeEntry = Require-Type "NSMedieval.UI.TradeEntryLayoutItemView"
$tradingInput = Require-Type "NSMedieval.UI.TradingInputLayoutItemView"
Require-Field $manager "tradeGoods" 'System.Collections.Generic.HashSet`1<System.Tuple`3<NSMedieval.UI.TradeResource,NSMedieval.UI.TradeResource,System.Int32>>'
Require-Field $manager "player" "NSMedieval.UI.ITrader"
Require-Field $manager "trader" "NSMedieval.UI.ITrader"
Require-Field $manager "playerResources" 'System.Collections.Generic.List`1<NSMedieval.UI.TradeResource>'
Require-Field $manager "traderResources" 'System.Collections.Generic.List`1<NSMedieval.UI.TradeResource>'
Require-Field $manager "tradingPanelView" "NSMedieval.UI.TradingPanelView"
Require-Field $panel "isApplyButtonEnabled" "System.Boolean"
Require-Field $panel "tradeEntries" 'System.Collections.Generic.List`1<NSMedieval.UI.TradeEntryLayoutItemView>'
Require-Field $panel "pinnedTradeEntries" 'System.Collections.Generic.List`1<NSMedieval.UI.TradeEntryLayoutItemView>'
Require-Field $tradeEntry "tradeValue" "System.Int32"
Require-Field $tradeEntry "tradingInput" "NSMedieval.UI.TradingInputLayoutItemView"
Require-Method $manager "OpenTradingMenu" @("NSMedieval.UI.ITrader", "NSMedieval.UI.ITrader")
Require-Method $manager "SetBuySellAmount" @("NSMedieval.UI.TradeResource", "NSMedieval.UI.TradeResource", "System.Int32")
Require-Method $manager "ApplyTrade" @("System.Single")
Require-Method $tradeEntry "OnTradeValueChanged" @("System.Int32")
Require-Method $tradingInput "SetTradeValue" @("System.Int32")
foreach ($property in @("Resource", "Health", "Count", "Creature")) {
    if ($null -eq ($tradeResource.Properties | Where-Object Name -eq $property | Select-Object -First 1)) {
        throw "TradeResource.$property property contract drifted."
    }
}

$source = Get-Content -LiteralPath $sourcePath -Raw
$externalSource = Get-Content -LiteralPath $externalSourcePath -Raw
$runtimeSource = Get-Content -LiteralPath $runtimeSourcePath -Raw
$payloadSource = Get-Content -LiteralPath $payloadSourcePath -Raw
foreach ($required in @(
    "synchronizedTrading",
    "TraderTradeSessionOpen",
    "TraderTradeBasketState",
    "TraderTradeBasketUpdateAction",
    "TraderTradeResult",
    "TraderTradeCommitAction",
    "isApplyButtonEnabled",
    "TryFormatReplicationTradingRowPairSignature",
    "ReplicationTradingRowHasCount",
    "kind=creature|creature=",
    "TryGetReplicationTraderPartyNetworkId(creature",
    "TryGetReplicationAgentOwnerEntityId(creature",
    "SendReplicationLocalCommandIntent",
    "PublishReplicationSynchronizedTradingBasketChange",
    "ResetReplicationSynchronizedTrading")) {
    if (($source + $externalSource + $payloadSource) -notmatch [regex]::Escape($required)) { throw "Synchronized-trading source contract missing: $required" }
}
if ($runtimeSource -notmatch 'replicationConfigSynchronizedTrading[\s\S]*?\+\s*":7"') {
    throw "Synchronized-trading handshake capability/version contract missing."
}

Write-Host "PASS SynchronizedTrading native/menu/authority contracts"

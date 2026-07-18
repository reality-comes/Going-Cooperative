[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$externalSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationExternalEventAgents.cs"
$eventSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationEvents.cs"
$transformSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationTransformCollector.cs"
$worldDeltaSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"
$runtimeSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$pluginSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Plugin.cs"
$sessionSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Multiplayer\MultiplayerSessionController.cs"
$saveWorkflowSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Multiplayer\MultiplayerSaveWorkflow.cs"

foreach ($path in @($cecilPath, $gameAssemblyPath, $externalSourcePath, $eventSourcePath,
        $transformSourcePath, $worldDeltaSourcePath, $runtimeSourcePath, $pluginSourcePath,
        $sessionSourcePath, $saveWorkflowSourcePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required trader-party surface input is missing: $path" }
}

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)

function Get-AllTypes {
    param([Parameter(Mandatory)] $Types)
    foreach ($type in $Types) {
        $type
        if ($type.HasNestedTypes) { Get-AllTypes -Types $type.NestedTypes }
    }
}

$allTypes = @(Get-AllTypes -Types $assembly.MainModule.Types)

function Require-Type {
    param([Parameter(Mandatory)][string] $FullName)
    $type = $allTypes | Where-Object { $_.FullName -eq $FullName } | Select-Object -First 1
    if ($null -eq $type) { throw "Trader-party native type missing: $FullName" }
    return $type
}

function Require-Method {
    param(
        [Parameter(Mandatory)] $Type,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Parameters
    )
    $method = $Type.Methods | Where-Object {
        if ($_.Name -ne $Name -or $_.Parameters.Count -ne $Parameters.Count) { return $false }
        for ($i = 0; $i -lt $Parameters.Count; $i++) {
            if ($_.Parameters[$i].ParameterType.FullName -ne $Parameters[$i]) { return $false }
        }
        return $true
    } | Select-Object -First 1
    if ($null -eq $method) { throw "Trader-party native method missing: $($Type.FullName).$Name($($Parameters -join ', '))" }
    return $method
}

function Require-SourcePattern {
    param([Parameter(Mandatory)][string] $Text, [Parameter(Mandatory)][string] $Pattern, [Parameter(Mandatory)][string] $Message)
    if ($Text -notmatch $Pattern) { throw $Message }
}

function Get-SourceMethodBlock {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $Signature
    )
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Trader-party source method missing: $Signature" }
    $openingBrace = $Text.IndexOf('{', $start)
    if ($openingBrace -lt 0) { throw "Trader-party source method has no body: $Signature" }
    $depth = 0
    for ($index = $openingBrace; $index -lt $Text.Length; $index++) {
        if ($Text[$index] -eq '{') { $depth++ }
        elseif ($Text[$index] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $Text.Substring($start, ($index - $start) + 1) }
        }
    }
    throw "Trader-party source method body is unterminated: $Signature"
}

$serializer = Require-Type "NSMedieval.Serialization.FVSerializer"
$deserializer = Require-Type "NSMedieval.Serialization.FVDeserializer"
$creature = Require-Type "NSMedieval.State.CreatureBase"
$humanoid = Require-Type "NSMedieval.State.HumanoidInstance"
$animal = Require-Type "NSMedieval.State.AnimalInstance"
$village = Require-Type "NSMedieval.State.VillageSaveData"
$npcManager = Require-Type "NSMedieval.Manager.NPCManager"
$npcController = Require-Type "NSMedieval.NPCController"
$animalManager = Require-Type "NSMedieval.Manager.AnimalManager"
$trading = Require-Type "NSMedieval.UI.TradingManager"
$traderBehaviour = Require-Type "NSMedieval.State.TraderBehaviour"
$traderEvent = Require-Type "NSMedieval.GameEventSystem.Events.TraderEvent"
$multiTraderEvent = Require-Type "NSMedieval.GameEventSystem.Events.MultiTraderEvent"
$eventSystem = Require-Type "NSMedieval.GameEventSystem.GameEventSystem"

$writeList = $serializer.Methods | Where-Object {
    $_.Name -eq "Write" -and $_.HasGenericParameters -and $_.Parameters.Count -eq 2 -and
    $_.Parameters[0].ParameterType.FullName -eq "System.String" -and
    $_.Parameters[1].ParameterType.FullName -eq 'System.Collections.Generic.IList`1<T>'
} | Select-Object -First 1
if ($null -eq $writeList) { throw "FVSerializer.Write<T>(string, IList<T>) drifted." }
$readList = $deserializer.Methods | Where-Object {
    $_.Name -eq "ReadObjectList" -and $_.HasGenericParameters -and $_.Parameters.Count -eq 2 -and
    $_.Parameters[0].ParameterType.FullName -eq "System.String"
} | Select-Object -First 1
if ($null -eq $readList) { throw "FVDeserializer.ReadObjectList<T> drifted." }

[void](Require-Method $village "AddNPC" @("NSMedieval.State.HumanoidInstance"))
[void](Require-Method $village "AddAnimal" @("NSMedieval.State.AnimalInstance"))
[void](Require-Method $npcManager "LoadSavedNPC" @("NSMedieval.State.HumanoidInstance"))
[void](Require-Method $npcController "RemoveNPC" @("NSMedieval.State.HumanoidInstance"))
[void](Require-Method $animalManager "InstantiateAnimal" @("NSMedieval.State.AnimalInstance", "System.Boolean"))
[void](Require-Method $animalManager "RemoveAnimal" @("NSMedieval.State.AnimalInstance", "System.Boolean"))
[void](Require-Method $animal "AssignPetOwner" @("NSMedieval.State.CreatureBase"))
$animalRopeTo = Require-Method $animal "RopeTo" @("NSMedieval.Goap.IGoapTargetable", "System.Boolean")
$humanoidRopeTo = Require-Method $humanoid "RopeTo" @("NSMedieval.Goap.IGoapTargetable", "System.Boolean")
foreach ($ropeMethod in @($animalRopeTo, $humanoidRopeTo)) {
    $ropeIl = ($ropeMethod.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
    if ($ropeIl -notmatch 'ldarg\.1[\s\S]*stfld .*::ropedTo') { throw "$($ropeMethod.FullName) no longer stores a nullable rope target directly." }
}

[void](Require-Method $trading "OpenTradingMenu" @("NSMedieval.UI.ITrader", "NSMedieval.UI.ITrader"))
[void](Require-Method $trading "ApplyTrade" @("System.Single"))
[void](Require-Method $traderBehaviour "OnSettlerTalkTo" @("NSMedieval.State.WorkerBehaviour"))
[void](Require-Method $traderEvent "Unsubscribe" @())
[void](Require-Method $multiTraderEvent "Unsubscribe" @())
[void](Require-Method $eventSystem "RemoveFromRunningEvents" @("NSMedieval.GameEventSystem.GameEventInstance"))

$uniqueIdGetter = $creature.Methods | Where-Object { $_.Name -eq "get_UniqueId" } | Select-Object -First 1
if ($null -eq $uniqueIdGetter) { throw "CreatureBase.UniqueId getter missing." }
$uniqueIdIl = ($uniqueIdGetter.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
if ($uniqueIdIl -notmatch 'UniqueIdManager::GetUniqueId') { throw "CreatureBase.UniqueId no longer allocates through UniqueIdManager." }

$externalSource = Get-Content -LiteralPath $externalSourcePath -Raw
$eventSource = Get-Content -LiteralPath $eventSourcePath -Raw
$transformSource = Get-Content -LiteralPath $transformSourcePath -Raw
$worldDeltaSource = Get-Content -LiteralPath $worldDeltaSourcePath -Raw
$runtimeSource = Get-Content -LiteralPath $runtimeSourcePath -Raw
$pluginSource = Get-Content -LiteralPath $pluginSourcePath -Raw
$sessionSource = Get-Content -LiteralPath $sessionSourcePath -Raw
$saveWorkflowSource = Get-Content -LiteralPath $saveWorkflowSourcePath -Raw

$abortCleanupSource = Get-SourceMethodBlock $externalSource "private static bool TryCleanupAbortedHostTraderParty("
$applyBundleSource = Get-SourceMethodBlock $externalSource "private static bool ApplyDeserializedTraderParty("
$agentStateSource = Get-SourceMethodBlock $externalSource "private static bool TryApplyReplicationTraderPartyAgentState("
$memberAdoptSource = Get-SourceMethodBlock $externalSource "private static bool TryApplyReplicationTraderPartyMemberAdopt("
$tradeReconcileSource = Get-SourceMethodBlock $externalSource "private static void ReconcileHostReplicationTraderPartyAfterTrade("
$invalidateBootstrapSource = Get-SourceMethodBlock $externalSource "private static void InvalidateHostTraderPartyBootstrap("
$ensureBootstrapSource = Get-SourceMethodBlock $externalSource "private static bool EnsureHostTraderPartyBootstrapReady("
$tryStartBootstrapSource = Get-SourceMethodBlock $externalSource "private static void TryStartHostTraderPartyTransfer("
$supersedeBootstrapSource = Get-SourceMethodBlock $externalSource "private static void SupersedeHostTraderPartyInitialBootstrap("
$hostTombstoneSource = Get-SourceMethodBlock $externalSource "private static void SendHostReplicationTraderPartyTombstone("
$quarantineSource = Get-SourceMethodBlock $externalSource "private static bool TryQuarantineImportedClientTraderEvent("
$clientAbortSource = Get-SourceMethodBlock $externalSource "private static bool TryCleanupClientAbortedNativeTraderParty("
$hostCompleteSource = Get-SourceMethodBlock $externalSource "internal static void CompleteHostReplicationTraderParty("
$clientRemoveSource = Get-SourceMethodBlock $externalSource "internal static void RemoveClientReplicationTraderParty("
$preflightSource = Get-SourceMethodBlock $externalSource "private static bool TryPreflightClientTraderPartyAdoption("
$refreshOwnershipSource = Get-SourceMethodBlock $externalSource "private static void RefreshHostTraderPartyOwnership("
$applyOwnershipSource = Get-SourceMethodBlock $externalSource "private static void ApplyTraderPartyOwnershipState("
$finalizeClientSource = Get-SourceMethodBlock $externalSource "private static void TryFinalizeClientReplicationTraderParty("
$resetSource = Get-SourceMethodBlock $externalSource "private static void ResetReplicationTraderParties("
$unregisterSource = Get-SourceMethodBlock $externalSource "private static void UnregisterTraderPartyBinding("

Require-SourcePattern $externalSource 'ReplicationTraderPartyWireVersion\s*=\s*"trader-party-v3"' "Trader-party transport must advertise the v3 wire contract."
Require-SourcePattern $externalSource 'ReplicationTraderPartyWriterId\s*=\s*"going-cooperative-trader-party-v3"' "Trader-party FV bundles must carry the v3 writer identity."
Require-SourcePattern $externalSource 'ReplicationTraderPartyBundleVersion\s*=\s*3' "Trader-party immutable bundle format must remain v3."
Require-SourcePattern $externalSource 'ReplicationTraderPartyMaxBundleBytes\s*=\s*96\s*\*\s*1024' "Trader-party compressed payload cap must remain 96 KiB."
Require-SourcePattern $externalSource 'ReplicationTraderPartyMaxChunks\s*=\s*512' "Trader-party transfer must remain capped at 512 chunks."
Require-SourcePattern $externalSource 'Write<CreatureBase>\("creatures",\s*creatures\)' "Trader party must serialize one shared CreatureBase root."
Require-SourcePattern $externalSource 'ReadObjectList\("creatures",\s*new List<CreatureBase>\(\)\)' "Trader party must deserialize one shared CreatureBase root."
Require-SourcePattern $externalSource 'ComputeTraderPartySha256\(data\)' "Trader-party data bytes need an independent SHA-256."
Require-SourcePattern $externalSource 'ComputeTraderPartySha256\(references\)' "Trader-party reference bytes need an independent SHA-256."
Require-SourcePattern $externalSource 'IsTraderPartyGameAssemblyMvid\(gameAssemblyMvid\)' "Trader manifest must validate the Assembly-CSharp MVID."
Require-SourcePattern $externalSource '(?s)newHumanoids\.Add\(humanoid\).*?AddNPC\(humanoid\).*?LoadReplicationTraderPartyNpc\(humanoid\)' "Humanoid rollback tracking/AddNPC/LoadSavedNPC ordering drifted."
Require-SourcePattern $externalSource '(?s)field\.SetValue\(creature,\s*0\).*?_\s*=\s*creature\.UniqueId' "Transferred actors must allocate client-local IDs without releasing host IDs."
Require-SourcePattern $externalSource 'TryRequestFullMultiplayerResync' "Terminal trader transfer failure must schedule full-session recovery."
Require-SourcePattern $externalSource 'float\.PositiveInfinity' "Immutable manifests must not be periodically reserialized."
Require-SourcePattern $externalSource 'internal static bool ReplicationTraderPartySurfacesReady\(\)' "Trader readiness must remain an on-demand internal validator."

Require-SourcePattern $externalSource 'ReplicationTraderPartyMemberAdoptDeltaKind\s*=\s*"TraderPartyMemberAdopt"' "Live trader membership adoption is missing from the v3 wire surface."
Require-SourcePattern $externalSource 'ReplicationTraderPartyAbortDeltaKind\s*=\s*"TraderPartyAbort"' "Pre-publication trader abort is missing from the v3 wire surface."
Require-SourcePattern $externalSource 'TryApplyReplicationTraderPartyMemberAdopt\(delta,\s*out detail\)' "TraderPartyMemberAdopt is not dispatched to the client apply path."
Require-SourcePattern $externalSource 'TryApplyReplicationTraderPartyAbort\(delta,\s*out detail\)' "TraderPartyAbort is not dispatched to the client apply path."
Require-SourcePattern $externalSource 'partyFingerprint=' "TraderPartyAbort must carry a bounded exact native-party identity."
Require-SourcePattern $externalSource 'partyMembers=' "TraderPartyAbort must carry its bounded native-party member count."
Require-SourcePattern $tradeReconcileSource 'SendHostReplicationTraderPartyMemberAdopt\(' "A completed trade does not publish newly adopted live members."
Require-SourcePattern $tradeReconcileSource 'SendHostReplicationTraderPartyAgentState\(' "A completed trade does not converge absolute member ownership state."
Require-SourcePattern $externalSource 'ReplicationClientTraderPartyTracker\.RecordBegin\(' "TraderPartyBegin does not use the bounded v3 transfer tracker."
Require-SourcePattern $externalSource 'ReplicationClientTraderPartyTracker\.RecordChunk\(' "TraderPartyChunk does not use the bounded v3 transfer tracker."
Require-SourcePattern $externalSource 'ReplicationClientTraderPartyTracker\.RecordCommit\(' "TraderPartyCommit does not use the bounded v3 transfer tracker."
Require-SourcePattern $externalSource 'ReplicationClientTraderPartyTracker\.TryTakeComplete\(' "Completed v3 trader transfers are not gated by the tracker."
Require-SourcePattern $externalSource 'ReplicationClientTraderPartyTracker\.Expire\(' "Idle v3 trader transfers are not expired by the runtime pump."
Require-SourcePattern $resetSource 'ReplicationClientTraderPartyTracker\.Reset\(\)' "Trader transfer tracker state survives a runtime reset."

Require-SourcePattern $externalSource 'ConditionalWeakTable<object,\s*TraderPartyQuarantineMarker>\s+ReplicationAdoptedTraderEvents' "Imported trader-event quarantine must use weak event keys."
Require-SourcePattern $externalSource 'Dictionary<string,\s*WeakReference>\s+ReplicationAdoptedTraderEventById' "Trader quarantine lookup must not retain native events strongly."
Require-SourcePattern $externalSource 'ReplicationAdoptedTraderEvents\.TryGetValue\(__instance,\s*out _\)' "Trader lifecycle suppression must remain adoption-targeted."
if ($externalSource -match 'HashSet<object>\s+ReplicationAdoptedTraderEvents') { throw "Trader quarantine regressed to a strong native-event set." }
Require-SourcePattern $finalizeClientSource 'ReleaseReplicationTraderEventQuarantine\(eventId\)' "Finalized trader parties do not release their weak quarantine entry."
Require-SourcePattern $resetSource 'ReleaseAllReplicationTraderEventQuarantines\(\)' "Runtime reset does not release trader-event quarantines."

Require-SourcePattern $abortCleanupSource '"Unsubscribe"' "Aborted trader cleanup does not unsubscribe native handlers."
Require-SourcePattern $abortCleanupSource '"RemoveWarningMessage"' "Aborted trader cleanup does not remove the native warning."
Require-SourcePattern $abortCleanupSource 'RemoveFromRunningEvents\(' "Aborted trader cleanup does not remove the native event from the running registry."
Require-SourcePattern $abortCleanupSource 'RemoveAnimal\(animal,\s*false\)' "Aborted trader cleanup must remove animals without dropping loot."
Require-SourcePattern $abortCleanupSource '"Dispose"' "Aborted trader cleanup does not dispose the detached native event."
if ($abortCleanupSource -match '(?:"ForceEnd"|"OnEnd"|\.(?:ForceEnd|OnEnd)\s*\()') { throw "Aborted trader cleanup must not execute native ForceEnd/OnEnd rewards or penalties." }

Require-SourcePattern $agentStateSource 'ClassifyClientTraderPartySemanticDelta\(' "AgentState does not classify stale, duplicate, and conflicting revisions."
Require-SourcePattern $memberAdoptSource 'ClassifyClientTraderPartySemanticDelta\(' "MemberAdopt does not classify stale, duplicate, and conflicting revisions."
foreach ($semanticSource in @($agentStateSource, $memberAdoptSource)) {
    Require-SourcePattern $semanticSource 'ClientTraderPartySemanticDisposition\.AckStale' "Semantic trader delta does not ACK a stale revision."
    Require-SourcePattern $semanticSource 'ClientTraderPartySemanticDisposition\.AckDuplicate' "Semantic trader delta does not ACK an exact duplicate revision."
    Require-SourcePattern $semanticSource 'ClientTraderPartySemanticDisposition\.Conflict' "Semantic trader delta does not reject a conflicting same revision."
    Require-SourcePattern $semanticSource 'CommitClientTraderPartySemanticDelta\(' "Semantic trader delta does not commit its revision high-water after native apply."
    if ($semanticSource -match '(?:FVSerializer|FVDeserializer|DeserializeTraderPartyBundle|TryGetOrCreateImmutableTraderPartyBundle)') {
        throw "Live semantic trader updates must not hot-merge FV inventory graphs."
    }
}
Require-SourcePattern $tradeReconcileSource 'inventoryHotMerge=false' "Live trade convergence must explicitly remain membership/ownership-only."
if ($tradeReconcileSource -match '(?:TryStartHostTraderPartyTransfer|TryGetOrCreateImmutableTraderPartyBundle|FVSerializer|FVDeserializer|DeserializeTraderPartyBundle)') {
    throw "The current peer must not receive a full FV refresh from the live trade path."
}

if ($externalSource -match 'authoritative-manifest-prune') { throw "Manifest omission must not destructively prune trader actors in v3." }
Require-SourcePattern $applyBundleSource 'actor removal is exclusively an explicit tombstone lane' "Initial/late-join apply no longer documents explicit-tombstone-only actor removal."
if ($applyBundleSource -match 'DespawnClientTraderPartyAgent\(') { throw "A full manifest omission must not despawn an existing trader actor." }
Require-SourcePattern $invalidateBootstrapSource 'record\.BootstrapDirty\s*=\s*true' "Live trades must invalidate the bootstrap for a future peer."
Require-SourcePattern $invalidateBootstrapSource 'if\s*\(!replicationRemoteHelloReceived\s*&&\s*!record\.TransferActive\s*&&\s*!record\.EverTransferred\)' "Only an unpublished bootstrap without a live peer may be rebuilt immediately after trade."
Require-SourcePattern $invalidateBootstrapSource 'liveFullFvRevision=false' "Bootstrap invalidation no longer records that the live peer is semantic-only."
if ($invalidateBootstrapSource -match '(?:TryStartHostTraderPartyTransfer|BootstrapAdvertisePending|BootstrapRefreshForNewPeer)') {
    throw "Bootstrap invalidation must not advertise or transmit a full FV refresh to the current peer."
}
Require-SourcePattern $ensureBootstrapSource 'TraderPartyRuntimePolicy\.DecideBootstrapDisposition\(' "Dirty full FV rebuild no longer uses the executable bootstrap policy."
Require-SourcePattern $ensureBootstrapSource 'TraderPartyBootstrapDisposition\.RebuildForNewPeer' "Dirty full FV rebuild is not gated to a newly connected peer."
Require-SourcePattern $externalSource '(?s)!replicationTraderPartyObservedRemoteReady.*?record\.BootstrapAdvertisePending\s*=\s*true;.*?record\.BootstrapRefreshForNewPeer\s*=\s*record\.BootstrapDirty\s*&&\s*record\.EverTransferred' "A new compatible peer does not explicitly advertise/rebuild the dirty bootstrap."
Require-SourcePattern $tryStartBootstrapSource '(?s)EnsureHostTraderPartyBootstrapReady\(record,\s*out var readinessDetail\).*?TryGetOrCreateImmutableTraderPartyBundle\(' "Every transfer start must refresh/validate bootstrap state before stamping a bundle."
Require-SourcePattern $hostTombstoneSource '(?s)SupersedeHostTraderPartyInitialBootstrap\(record,\s*"member-tombstone-before-bootstrap"\).*?SendHostTraderPartyDelta\(.*?ReplicationTraderPartyTombstoneDeltaKind.*?TryStartHostTraderPartyTransfer\(record,\s*"member-tombstone-bootstrap-restart"\)' "Pre-apply membership tombstone must supersede the old bootstrap, publish the tombstone, then advertise the revised roster."
Require-SourcePattern $supersedeBootstrapSource '(?s)CancelHostTraderPartyTransfersForEvent\(.*?record\.ManifestRevision\+\+.*?record\.CachedBundle\s*=\s*Array\.Empty<byte>\(\).*?record\.BootstrapAdvertisePending\s*=\s*replicationRemoteHelloReceived' "Initial bootstrap restart must cancel stale transfer state, advance revision, clear cached FV, and re-advertise."
if ($supersedeBootstrapSource -match 'record\.EverTransferred\)\s*\{?\s*return') { throw "A successful current-peer bootstrap must never enter the membership restart path." }

Require-SourcePattern $refreshOwnershipSource 'TraderPartyOwnerClassification\.GenericWorldOwner' "Ordinary settlement ownership is not classified as detached from the merchant event."
Require-SourcePattern $agentStateSource 'IsClientSameEventTraderOwner\(' "AgentState does not distinguish a same-event trader from a generic world owner."
Require-SourcePattern $applyOwnershipSource 'var effectiveOwner\s*=\s*owner;' "Detached stock must retain and apply its generic world owner."
Require-SourcePattern $preflightSource 'descriptors\.Where\(descriptor\s*=>\s*!descriptor\.Detached\)' "Native trader-event preflight must exact-match only attached event-owned descriptors."
Require-SourcePattern $applyBundleSource 'Detached trader stock must adopt an existing global actor' "Detached purchased stock may be duplicated instead of globally adopted during full bootstrap/resync."
Require-SourcePattern $externalSource '(?s)if\s*\(!descriptor\.Detached\s*&&\s*IsTraderPartyStockRole\(descriptor\.Role\).*?activeTraderIds\.Contains' "Detached stock owned by another world/event actor must not be suppressed as an invalid attached relationship."

Require-SourcePattern $hostCompleteSource '(?s)record\.Agents\.Where\(binding\s*=>\s*binding\.Detached\).*?RememberDetachedTraderPartyAdoption\(detached\[i\]\).*?UnregisterTraderPartyBinding\(detached\[i\]\)' "Host event completion must release detached A identities while preserving actors for later merchant B adoption."
Require-SourcePattern $eventSource 'CompleteHostReplicationTraderParty\(__instance,\s*__state!,\s*"native-end"\)' "Native merchant event completion is not wired to release detached host identities."
Require-SourcePattern $clientRemoveSource '(?s)binding\.Detached.*?RememberDetachedTraderPartyAdoption\(detached\[i\]\).*?UnregisterTraderPartyBinding\(detached\[i\]\)' "Client event completion must release detached A identities while preserving actors for later merchant B adoption."
Require-SourcePattern $tradeReconcileSource '(?s)releasedBinding.*?UnregisterTraderPartyBinding\(existingBinding\).*?RegisterTraderPartyBinding\(binding\).*?catch.*?RegisterTraderPartyBinding\(releasedBinding\)' "A-to-B merchant adoption is not transactionally released/registered/restored."
Require-SourcePattern $externalSource '(?s)ReplicationTraderApplyTradePostfix.*?try\s*\{\s*ReconcileHostReplicationTraderPartyAfterTrade\(owner\);\s*\}\s*catch' "Trade postfix must contain reconciliation exceptions instead of crashing the game loop."

Require-SourcePattern $clientAbortSource '(?s)if\s*\(matches\.Count\s*!=\s*1\).*?return false;.*?TryQuarantineImportedClientTraderEvent' "Client abort cleanup must leave ambiguous/unrelated native merchants untouched before exact-match quarantine."
Require-SourcePattern $clientAbortSource 'RemoveAnimal\(animal,\s*false\)' "Exact client abort cleanup must not drop merchant animal loot."
Require-SourcePattern $quarantineSource '(?s)ReplicationAdoptedTraderEvents\.Add\(nativeEvent.*?RemoveFromRunningEvents\(.*?RunningEvents\.Contains.*?"Unsubscribe".*?"RemoveWarningMessage"' "Imported-event quarantine must install a reversible marker, verify registry removal, then perform best-effort native cleanup."
Require-SourcePattern $quarantineSource '(?s)native-event-remains-running.*?ReleaseReplicationTraderEventQuarantine\(eventId\).*?return false' "Failed registry removal must roll back the quarantine marker without unsubscribing the native event."
Require-SourcePattern $quarantineSource '(?s)Exception\?\s+unsubscribeException\s*=\s*null;.*?catch\s*\(Exception ex\)\s*\{\s*unsubscribeException\s*=\s*ex;.*?if\s*\(unsubscribeException\s*!=\s*null\)\s*\{.*?native-event-unsubscribe=.*?return false;' "Failed native Unsubscribe must fail closed instead of reporting a successful quarantine."
$unsubscribeFailureIndex = $quarantineSource.IndexOf('if (unsubscribeException != null)', [System.StringComparison]::Ordinal)
if ($unsubscribeFailureIndex -lt 0) { throw "Trader quarantine unsubscribe failure branch is missing." }
$unsubscribeFailureTail = $quarantineSource.Substring($unsubscribeFailureIndex)
$unsubscribeSuccessIndex = $unsubscribeFailureTail.IndexOf('detail = "ok"', [System.StringComparison]::Ordinal)
if ($unsubscribeSuccessIndex -lt 0) { throw "Trader quarantine success branch is missing after Unsubscribe handling." }
$unsubscribeFailureBranch = $unsubscribeFailureTail.Substring(0, $unsubscribeSuccessIndex)
if ($unsubscribeFailureBranch -match 'ReleaseReplicationTraderEventQuarantine\(eventId\)') { throw "Failed native Unsubscribe must retain the quarantine marker after registry removal." }
Require-SourcePattern $unsubscribeFailureTail '(?s)native-event-unsubscribe=.*?return false;.*?detail\s*=\s*"ok"' "Native Unsubscribe failure must return before the quarantine success result."

Require-SourcePattern $saveWorkflowSource '(?s)CaptureAndQueueMultiplayerResync\(\).*?FlushHostTraderPartyAbortsBeforeCheckpoint\(out var abortFlushDetail\).*?StopReplicationRuntime\(ReplicationTraderPartyResetContext\.WorldReloadPending\).*?SaveCurrentVillage' "Host full-resync checkpoint capture must flush prepublication trader abort cleanup before runtime reset/save."

Require-SourcePattern $unregisterSource '(?s)ReplicationTraderPartyBindingByObject\.TryGetValue\(binding\.Agent,\s*out var currentBinding\).*?ReferenceEquals\(currentBinding,\s*binding\)' "Object-to-binding unregister must remove only the exact binding instance."
Require-SourcePattern $unregisterSource '(?s)ReplicationTraderPartyObjectByNetworkId\.TryGetValue\(binding\.NetworkId,\s*out var current\).*?ReferenceEquals\(current,\s*binding\.Agent\)' "Network-to-object unregister must remove only the exact actor instance."

Require-SourcePattern $externalSource '(?s)private enum ReplicationTraderPartyResetContext.*?WorldReloadPending.*?StopInPlace.*?ScopeChangedSameWorld' "Trader-party reset contexts are incomplete."
Require-SourcePattern $resetSource 'context\s*!=\s*ReplicationTraderPartyResetContext\.WorldReloadPending' "Same-world reset cleanup is not separated from pending world reload."
Require-SourcePattern $resetSource '(?s)if\s*\(!binding\.Detached\).*?DespawnClientTraderPartyAgent\(' "Same-world reset must despawn every non-detached imported party actor."
Require-SourcePattern $resetSource 'RememberDetachedTraderPartyAdoption\(binding\)' "Same-world reset does not preserve detached purchases for weak re-adoption."
Require-SourcePattern $runtimeSource 'StopReplicationRuntime\(ReplicationTraderPartyResetContext traderPartyResetContext\)' "Runtime shutdown does not require an explicit trader reset context."
Require-SourcePattern $runtimeSource 'ResetReplicationEventRuntimeState\(traderPartyResetContext\)' "Runtime shutdown does not forward its trader reset context."
Require-SourcePattern $sessionSource 'ApplyMultiplayerSessionOptions[\s\S]*?StopReplicationRuntime\(ReplicationTraderPartyResetContext\.ScopeChangedSameWorld\)' "Changing multiplayer scope must use same-world trader cleanup."
Require-SourcePattern $sessionSource 'StopMultiplayerSession[\s\S]*?StopReplicationRuntime\(ReplicationTraderPartyResetContext\.StopInPlace\)' "Disconnect must use in-place trader cleanup."
Require-SourcePattern $saveWorkflowSource 'ReplicationTraderPartyResetContext\.WorldReloadPending' "Native checkpoint loading must identify a pending world reload."
Require-SourcePattern $pluginSource 'StopReplicationRuntime\(ReplicationTraderPartyResetContext\.WorldReloadPending\)' "Plugin destruction must use the world-reload-safe trader reset context."
Require-SourcePattern $eventSource 'ResetReplicationTraderParties\(ReplicationTraderPartyResetContext\.ScopeChangedSameWorld\)' "Event scope/epoch changes must clean trader actors in the current world."

Require-SourcePattern $eventSource 'return false;[\s\S]*ReplicationCombatExternalAgentLifecycleImplemented' "Aggregate external-event authority must remain disabled."
Require-SourcePattern $transformSource 'event-agent:' "Transform identity must recognize event-agent IDs."
Require-SourcePattern $transformSource '"npc"' "Transform collection must include mapped merchant NPC views."
Require-SourcePattern $worldDeltaSource 'TryGetReplicationTraderPartyObject\(entityId' "Agent lookup must resolve remapped event-agent IDs directly."
Require-SourcePattern $worldDeltaSource 'HandleReplicationTraderPartyWorldDeltaAck\(ack\)' "Reliable trader transfer ACK progression is not wired."
Require-SourcePattern $pluginSource 'TryInstallReplicationExternalEventAgentHooks\(harmony\)' "Trader-party Harmony hooks are not installed."
Require-SourcePattern $runtimeSource 'UpdateReplicationTraderPartyTransfers\(\)' "Trader-party transfer/recovery pump is not wired."

Write-Output "PASS TraderPartyGameSurfaces v3 transfer/semantic/abort/reset/quarantine contracts"

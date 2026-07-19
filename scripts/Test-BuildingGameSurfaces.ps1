[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$capturePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.Building.cs"
$commandCapturePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.cs"
$batchPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildBatch.cs"
$runtimePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$worldDeltaPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs"

foreach ($path in @($cecilPath, $gameAssemblyPath, $capturePath, $commandCapturePath, $batchPath, $runtimePath, $worldDeltaPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required building verification input is missing: $path" }
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
    if ($null -eq $type) { throw "Building native surface type missing: $FullName" }
    return $type
}

function Require-Method {
    param(
        [Parameter(Mandatory)] $Type,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ParameterTypes
    )
    $method = $Type.Methods | Where-Object {
        if ($_.Name -ne $Name -or $_.Parameters.Count -ne $ParameterTypes.Count) { return $false }
        for ($i = 0; $i -lt $ParameterTypes.Count; $i++) {
            if ($_.Parameters[$i].ParameterType.FullName -ne $ParameterTypes[$i]) { return $false }
        }
        return $true
    } | Select-Object -First 1
    if ($null -eq $method) {
        throw "Building native method missing: $($Type.FullName).$Name($($ParameterTypes -join ', '))"
    }
    return $method
}

function Require-CallOrder {
    param(
        [Parameter(Mandatory)] $Method,
        [Parameter(Mandatory)][string[]] $Fragments
    )
    $cursor = -1
    foreach ($fragment in $Fragments) {
        $found = -1
        for ($i = $cursor + 1; $i -lt $Method.Body.Instructions.Count; $i++) {
            $operand = $Method.Body.Instructions[$i].Operand
            if ($null -ne $operand -and $operand.ToString().Contains($fragment)) {
                $found = $i
                break
            }
        }
        if ($found -lt 0) { throw "Expected call/order fragment '$fragment' missing in $($Method.FullName) after instruction $cursor" }
        $cursor = $found
    }
}

$placement = Require-Type "NSMedieval.BuildingComponents.BuildingPlacementManager"
$view = Require-Type "NSMedieval.BuildingComponents.BaseBuildingViewComponent"
$roofView = Require-Type "NSMedieval.BuildingComponents.RoofViewComponent"
$buildingInstance = Require-Type "NSMedieval.BuildingComponents.BaseBuildingInstance"
$buildingsManager = Require-Type "NSMedieval.BuildingComponents.BuildingsManagerMain"

$onLeftMouseUp = Require-Method $placement "OnLeftMouseUp" @()
$objectPlaced = Require-Method $placement "ObjectPlacedOnMap" @("NSMedieval.BuildingComponents.BaseBuildingViewComponent")
$normalCommit = Require-Method $placement "MouseUpSpawnInitializeBuildings" @("System.Int32")
$roofCommit = Require-Method $placement "CreateRoofs" @("System.Int32")
$roofReplay = Require-Method $placement "CreateRoofs" @(
    "System.Int32",
    "NSMedieval.Vec3Int",
    'System.Collections.Generic.List`1<NSMedieval.Vec3Int>')
$null = Require-Method $placement "MouseUpRoofs" @()
$null = Require-Method $placement "MouseUpSocketable" @()
$null = Require-Method $placement "SpawnRoofAutoTesting" @(
    "NSMedieval.BuildingComponents.BaseBuildingBlueprint",
    "NSMedieval.Vec3Int",
    "System.Int32",
    "NSMedieval.Vec3Int",
    'System.Collections.Generic.List`1<NSMedieval.Vec3Int>')
$null = Require-Method $buildingInstance "ConstructionStarted" @()
$null = Require-Method $buildingInstance "ConstructionPaused" @()
$null = Require-Method $buildingInstance "EnterFoundationState" @("System.Boolean")
$null = Require-Method $buildingInstance "ConstructionCompleted" @()
$null = Require-Method $buildingInstance "SetConstructionPhase" @(
    "NSMedieval.Construction.ConstructionPhase",
    "System.Boolean")
$null = Require-Method $buildingInstance "SetMarkedForDestruction" @("System.Boolean")
$null = Require-Method $buildingInstance "BuildingCanceled" @("UnityEngine.Vector3")
$null = Require-Method $buildingInstance "BuildingDeconstructed" @("UnityEngine.Vector3")
$null = Require-Method $buildingsManager "DestroyBuilding" @(
    "NSMedieval.BuildingComponents.BaseBuildingInstance",
    "System.Boolean",
    "System.Boolean")
$forbidProperty = $buildingInstance.Properties | Where-Object Name -eq "IsForbidden" | Select-Object -First 1
if ($null -eq $forbidProperty -or $null -eq $forbidProperty.SetMethod) {
    throw "Building native IsForbidden setter is missing."
}

foreach ($propertyName in @("BaseBuildingInstance")) {
    if (-not ($view.Properties | Where-Object Name -eq $propertyName)) { throw "Building view property missing: $propertyName" }
}
foreach ($propertyName in @("Positions", "GetScale", "RoofComponentInstance")) {
    if (-not ($roofView.Properties | Where-Object Name -eq $propertyName)) { throw "Roof view property missing: $propertyName" }
}
foreach ($propertyName in @("Blueprint", "Positions", "MarkedForDestruction")) {
    if (-not ($buildingInstance.Properties | Where-Object Name -eq $propertyName)) { throw "Building instance property missing: $propertyName" }
}

Require-CallOrder $normalCommit @("CreateBuildingInstanceAndBindToView", "ObjectPlacedOnMap")
Require-CallOrder $roofCommit @("CanPlaceRoof", "CreateBuildingInstanceAndBindToView", "CreateAndCacheRoofComponentInstance", "ObjectPlacedOnMap")
Require-CallOrder $roofReplay @("CanPlaceRoof", "CreateBuildingInstanceAndBindToView", "AddPositions", "Scale", "CreateAndCacheRoofComponentInstance", "ObjectPlacedOnMap")

$onLeftCalls = @($onLeftMouseUp.Body.Instructions | ForEach-Object { if ($null -ne $_.Operand) { $_.Operand.ToString() } }) -join "`n"
foreach ($branch in @("MouseUpSpawnInitializeBuildings", "MouseUpRoofs", "MouseUpSocketable")) {
    if (-not $onLeftCalls.Contains($branch)) { throw "OnLeftMouseUp no longer reaches required placement branch: $branch" }
}
$spawnBeamX = $placement.Methods | Where-Object { $_.Name -eq "SpawnBeamAxisX" -and $_.Parameters.Count -eq 4 } | Select-Object -First 1
$spawnBeamZ = $placement.Methods | Where-Object { $_.Name -eq "SpawnBeamAxisZ" -and $_.Parameters.Count -eq 4 } | Select-Object -First 1
$tryPlaceSocketable = $placement.Methods | Where-Object { $_.Name -eq "TryPlaceSocketable" -and $_.Parameters.Count -eq 4 } | Select-Object -First 1
if ($null -eq $spawnBeamX -or $null -eq $spawnBeamZ) { throw "Exact native beam semantic replay surfaces are missing." }
if ($null -eq $tryPlaceSocketable) { throw "Exact native socketable semantic replay surface is missing." }

$captureSource = Get-Content -LiteralPath $capturePath -Raw
$commandCaptureSource = Get-Content -LiteralPath $commandCapturePath -Raw
$batchSource = Get-Content -LiteralPath $batchPath -Raw
$runtimeSource = Get-Content -LiteralPath $runtimePath -Raw
$worldDeltaSource = Get-Content -LiteralPath $worldDeltaPath -Raw
$legacyPlacementSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationBuildingPlacement.cs") -Raw
if (-not $captureSource.Contains("OnLeftMouseUp + ObjectPlacedOnMap")) { throw "Build capture is not transaction-scoped to native commit surfaces." }
if ($captureSource.Contains("ReplicationBuildSpawnFromPoolPostfix")) { throw "Transient SpawnFromPool preview capture must not be restored." }
if (-not $batchSource.Contains("ReplicationPlacementManagerStateScope")) { throw "Batch replay is missing live placement-state isolation." }
if (-not $batchSource.Contains("SpawnRoofAutoTesting")) { throw "Batch replay is missing roof topology replay." }
if ($batchSource.Contains('ReplicationBuildBatchTargetEncodedChars') -or $captureSource.Contains('encodedChars + recordChars')) { throw "A native drag must not be split into independent semantic commands by byte size." }
if (-not $captureSource.Contains('build-transaction-over-cap') -or -not $captureSource.Contains('One native drag is one semantic command')) { throw "Oversized dragged placement is not rejected atomically at the 512-item transaction boundary." }
if (-not $captureSource.Contains('groups.Count != 1') -or -not $captureSource.Contains('build-transaction-mixed-groups')) { throw "A native drag with unexpected mixed semantics can still be split into independently accepted commands." }
if (-not $captureSource.Contains('BuildBatchPayloadMaxUtf8Bytes') -or -not $captureSource.Contains('build-transaction-wire-over-cap') -or -not $captureSource.Contains('Validate every group before sending the first one')) { throw "A BuildBatch can exceed the bidirectional transport ceiling or partially emit before wire preflight." }
if (-not $captureSource.Contains('RollbackReplicationRawBuildViews(') -or -not $captureSource.Contains('reason=native-placement-exception') -or -not $captureSource.Contains('!__state.IsFinalized')) { throw "A native placement exception can strand transaction-owned committed views instead of rolling the exact transaction back." }
if (-not $captureSource.Contains('replicationActiveBuildCaptureTransaction?.TrackRawView(__0)') -or -not $captureSource.Contains('__state.HasCaptureFailure') -or -not $captureSource.Contains('build-capture-rollback-incomplete')) { throw "A committed native view that cannot be classified can escape transaction rollback and become client-only state." }
if (-not $captureSource.Contains('ReplicationBuildObjectPlacedOnMapPrefix') -or -not $captureSource.Contains('ObjectPlacedOnMap mutates the world and can throw before its postfix')) { throw "ObjectPlacedOnMap exception rollback does not claim the bound view before native mutation." }
if (-not $captureSource.Contains('committed-build-unique-id-invalid')) { throw "A committed building without a positive authoritative identity can enter a batch manifest." }
if (-not $captureSource.Contains('reason=build-transport-not-ready') -or -not $captureSource.Contains('ShowReplicationBuildTransportNotReadyMessage()')) { throw "A send-enabled client can retain local-only placement while the multiplayer command channel is not ready." }
if (-not $captureSource.Contains('placement failed and was rolled back to keep both players synchronized')) { throw "Fail-closed native placement rollback is not visible to the player." }
if (-not $batchSource.Contains("build-batch-unsupported-semantic-category")) { throw "Unclassified beam/socket blueprints must still fail closed." }
if (-not $batchSource.Contains('ReplicationBuildPlacementKind.BeamX') -or -not $batchSource.Contains('ReplicationBuildPlacementKind.BeamZ') -or -not $batchSource.Contains('ReplicationBuildPlacementKind.Socketable')) { throw "BuildBatch is missing tagged beam/socketable geometry." }
if (-not $batchSource.Contains('SpawnBeamAxisX') -or -not $batchSource.Contains('SpawnBeamAxisZ') -or -not $batchSource.Contains('TryPlaceSocketable')) { throw "Tagged beam/socketable records are not replayed through native semantic calls." }
if (-not $batchSource.Contains('BEAM_REPLICATION_DIAGNOSTIC') -or -not $batchSource.Contains('preinvoke-endpoint') -or -not $batchSource.Contains('native-null')) { throw "Gated beam replay diagnostics no longer distinguish endpoint resolution from native rejection." }
if (-not $batchSource.Contains('ResolveReplicationBuildingsManagerMain(managerType, out managerDetail)')) { throw "Beam endpoint replay bypasses the active-village-aware BuildingsManagerMain resolver." }
if (-not $captureSource.Contains('Enum.GetName(value.GetType(), value)')) { throw "Beam/socket fail-closed classification regressed to unstable numeric enum values." }
if (-not $captureSource.Contains('StartSocketGridPosition') -or -not $captureSource.Contains('EndSocketGridPosition') -or -not $captureSource.Contains('beam-topology-native')) { throw "Beam capture is not sourced from committed native socket-grid endpoints." }
if (-not $worldDeltaSource.Contains("same-grid mismatch is not a negative result")) { throw "Layered same-grid lookup regression guard is missing." }
if (-not $worldDeltaSource.Contains("ReplicationBuildingBlueprintBatchPlacedDeltaKind")) { throw "Host-origin drag placements are not sent as one batch." }
if (-not $worldDeltaSource.Contains("ReplicationBuildingBlueprintBatchResultDeltaKind")) { throw "Client-origin drag results are not reconciled as one batch." }
if (-not $worldDeltaSource.Contains("exact-apply-commit")) { throw "Host build results are not sourced from exact native commits." }
if ($worldDeltaSource.Contains("TryBuildReplicationBuildingCandidateIndex")) { throw "Post-hoc grid candidate indexing must not classify build success." }
if (-not $worldDeltaSource.Contains('BuildingState|snapshot=')) { throw "Building snapshot coalescing is not scoped by checkpoint and row." }
if (-not $worldDeltaSource.Contains("without discarding already ACKed rows")) { throw "Building snapshot Begin no longer preserves rows that arrived first." }
if (-not $worldDeltaSource.Contains("End can beat Begin and rows under retransmission")) { throw "Building snapshot End no longer creates a retained context when it arrives first." }
if (-not $worldDeltaSource.Contains("context.EndDelta = delta")) { throw "Building snapshot End retry identity is not retained for deferred completion." }
if (-not $worldDeltaSource.Contains("ReplicationOrderingPolicy.IsSnapshotComplete")) { throw "Building snapshot completeness is not shared by End and final-row paths." }
if (-not $worldDeltaSource.Contains("building-state-snapshot-finalized-after-row")) { throw "A final row arriving after End no longer finalizes and ACKs the checkpoint." }
$terminalMembershipCalls = [regex]::Matches($worldDeltaSource, 'RecordReplicationTerminalBuildingStateSnapshotMembership\(').Count
if ($terminalMembershipCalls -lt 5) { throw "Stale/coalesced BuildingState terminal ACK paths do not all preserve checkpoint membership." }
if (-not $worldDeltaSource.Contains('supersededByNewerState: true') -or -not $worldDeltaSource.Contains('building-state-snapshot-superseded') -or -not $worldDeltaSource.Contains('newerAbsoluteState=yes noPrune=yes')) { throw "A BuildingState checkpoint superseded by a newer absolute row can still raise a false resync alarm." }
if (-not $worldDeltaSource.Contains("ReplicationClientAppliedAbsoluteStateSequenceHighWater")) { throw "Applied absolute-state high-water storage is missing." }
if (-not $worldDeltaSource.Contains('return "BuildingState|uid="')) { throw "Building applied ordering is not scoped by physical building identity." }
if (-not $worldDeltaSource.Contains("TryAcceptReplicationResourcePileStateSnapshot")) { throw "Resource-pile snapshots no longer reject stale checkpoint generations." }
if (-not $worldDeltaSource.Contains("pending.SnapshotId >= snapshotId")) { throw "A newer resource-pile snapshot no longer purges older deferred applies." }
if (-not $worldDeltaSource.Contains("building-state-snapshot-not-converged")) { throw "Building snapshot terminal drift diagnostics are missing." }
if (-not $captureSource.Contains("placement blocked unsupported semantics")) { throw "Unsupported multiplayer build commits are not visibly rolled back." }
if (-not $batchSource.Contains('ReplicationBuildRoofMaxPositions = 512') -or -not $captureSource.Contains('roof-topology-over-cap')) { throw "Oversized roof topology is not rejected before exceeding the 16 KiB record bound." }
if (-not $captureSource.Contains('HashSet<object> rawViewSet = new HashSet<object>(ReferenceObjectComparer.Instance)') -or -not $captureSource.Contains('HashSet<object> classifiedViewSet = new HashSet<object>(ReferenceObjectComparer.Instance)')) { throw "Build transaction capture is not deduplicated by exact object reference identity." }
if (-not $captureSource.Contains('roof-topology-unreadable') -or -not $captureSource.Contains('roof-topology-empty')) { throw "Roof capture must fail closed when native committed topology is absent." }
if (-not $batchSource.Contains('build-record-roof-topology-empty')) { throw "Roof replay parser must reject empty topology before native CreateRoofs." }
if ($captureSource.Contains('HashSet<int> viewKeys') -or $captureSource.Contains('RuntimeHelpers.GetHashCode(placement.View)')) { throw "Build transaction capture still risks dropping a view on an identity-hash collision." }
if (-not $batchSource.Contains('isolated = Activator.CreateInstance(value.GetType())') -or -not $batchSource.Contains('field.SetValue(manager, isolated)')) { throw "Placement manager collections are not isolated by a fresh reference swap." }
foreach ($fieldName in @("ray", "hit", "tempSide", "hitSide", "adjustedWorldPosition", "showCantDigTopLayer", "pooledObjectLayer")) {
    if (-not ($placement.Fields | Where-Object Name -eq $fieldName)) { throw "Native placement replay field missing from the audited game build: $fieldName" }
    if (-not $batchSource.Contains('state.Capture("' + $fieldName + '")')) { throw "Placement manager replay no longer restores transitive native field: $fieldName" }
}
$raycastHitsField = $placement.Fields | Where-Object Name -eq "raycastHits" | Select-Object -First 1
if ($null -eq $raycastHitsField -or -not $raycastHitsField.FieldType.IsArray) { throw "Native placement raycast buffer is missing or no longer an array." }
if ((-not $batchSource.Contains('state.IsolateArray("raycastHits", out _)')) -or (-not $batchSource.Contains('Array.CreateInstance(elementType, original.Length)'))) {
    throw "Placement manager replay can overwrite the live placement raycast buffer instead of swapping a fresh same-length array."
}
if ((-not $batchSource.Contains('IsolateCollection("roofPositionView", out _, trackValuesForCleanup: false)')) -or (-not [regex]::IsMatch($batchSource, 'trackValuesForCleanup\s*\?\s*\(\)\s*=>')) -or $batchSource.Contains('TrackUncommittedViews(roofPositionView)')) {
    throw "Roof cleanup still treats RoofViewComponent aliases as uncommitted BaseBuildingViewComponent owners."
}
if (-not $batchSource.Contains('build-batch-placement-state-restore-failed') -or -not $batchSource.Contains('replicationBuildBatchReplayDisabled = true')) { throw "Placement state restoration failures are not surfaced and latched fail-closed." }
if (-not $captureSource.Contains('authoritative-batch-uncommitted-cleanup')) { throw "Partially bound transaction-owned views are not cleaned up after an uncommitted batch item." }
if (-not $captureSource.Contains('Publish immutable commit truth before cleanup') -or -not $captureSource.Contains('CleanupFailureCount') -or -not $captureSource.Contains('authoritative-batch-uncommitted-cleanup-failed')) { throw "Post-commit cleanup failure can erase the canonical result or allow additional replay against an unproven world." }
if (-not $runtimeSource.Contains('ReplicationHostCommandResultRecord') -or -not $runtimeSource.Contains('BuildBatchCommitManifest')) { throw "The host command-result cache does not retain the immutable BuildBatch commit manifest." }
if (-not $worldDeltaSource.Contains('canonicalRecords[i] = FormatReplicationCanonicalBuildPlacementRecord') -or -not $worldDeltaSource.Contains('canonicalPayload')) { throw "BuildBatch results must carry canonical host-committed geometry, not the client request payload." }
if (-not $worldDeltaSource.Contains('provisional-mismatch=committed-topology')) { throw "Client provisional reconciliation must compare angle and complete roof topology." }
if (-not $worldDeltaSource.Contains('ReplicationBuildBatchCommitManifest') -or -not $worldDeltaSource.Contains('ResendReplicationBuildBatchResult')) { throw "Duplicate BuildBatch intents cannot replay an exact durable result manifest." }
if (-not $runtimeSource.Contains('replicationHostProtectedBuildBatchManifestCount') -or -not $runtimeSource.Contains('candidate.IsEvictionProtected') -or -not $runtimeSource.Contains('TrimReplicationHostCommandResultCache()')) { throw "Unacknowledged BuildBatch manifests can be evicted by the generic host command-result retention cap." }
if (-not $runtimeSource.Contains('ReleaseBuildBatchCommitManifest()') -or -not $runtimeSource.Contains('RefreshReplicationHostCommandResultRetentionOrder(commandKey)')) { throw "A positively acknowledged BuildBatch result does not compact to a normally retained duplicate-command tombstone." }
if (-not $worldDeltaSource.Contains('positivelyAcknowledgedBuildBatchResult = acknowledgedPending.Delta') -or -not $worldDeltaSource.Contains('MatchesResultDelta(ReplicationWorldObjectDelta delta)') -or -not $worldDeltaSource.Contains('IsPositiveReplicationBuildBatchResultAcknowledgement')) { throw "BuildBatch manifest release is not tied to a positive acknowledgement of the exact reliable result delta." }
if (-not $runtimeSource.Contains('pendingCommand!.MarkHostResponded(ack.Accepted') -or -not $commandCaptureSource.Contains('ReplicationBuildBatchResultRequestWindowSeconds = 120f')) { throw "BuildBatch ACKs are not retained in the bounded awaiting-result state until the durable manifest arrives." }
if ($runtimeSource.Contains('RollbackReplicationProvisionalBuildViews(' + "`r`n" + '                        pendingCommand!.Command') -or -not $runtimeSource.Contains('Do not discard provisional views on a negative ACK')) { throw "A rejected/partial BuildBatch ACK can discard provisional state before its durable rollback manifest." }
if (-not $commandCaptureSource.Contains('ReplicationBuildBatchResultDormantRetrySeconds') -or -not $commandCaptureSource.Contains('lowRateRequests=yes')) { throw "Accepted BuildBatch receipts can become permanently dormant instead of requesting the cached manifest." }
if (-not $commandCaptureSource.Contains('A throwing transport is still an attempted delivery') -or -not $commandCaptureSource.Contains('Failed transport attempts count toward the same bounded retry')) { throw "Throwing BuildBatch transport attempts can bypass the rollback cap and retain provisional placement forever." }
if (-not $captureSource.Contains('ReplicationBuildBatchReplayMaxFailures = 8') -or -not $worldDeltaSource.Contains('ShouldEscalateBuildBatchReplay') -or -not $worldDeltaSource.Contains('accepted-build-replay-failure-limit')) { throw "An accepted BuildBatch result can invoke native placement forever instead of escalating after bounded replay failures." }
if (-not $captureSource.Contains('ProcessReplicationBuildBatchRecoveryRequest()') -or -not $captureSource.Contains('TryRequestFullMultiplayerResync(out var error)') -or -not $runtimeSource.Contains('ProcessReplicationBuildBatchRecoveryRequest()')) { throw "Terminal BuildBatch divergence does not activate deferred full-save recovery." }
if (-not $captureSource.Contains('Never discard ownership of a live provisional view') -or -not $captureSource.Contains('live-provisional-without-receipt')) { throw "Provisional cleanup can discard the only handle to a live client-only building." }
if (-not $captureSource.Contains('Retain the registration when removal throws') -or -not $captureSource.Contains('provisional build rollback item failed')) { throw "A throwing provisional rollback can erase ownership or abort cleanup of the remaining batch." }
if (-not $worldDeltaSource.Contains('CompleteReplicationBuildBatchPendingIntent(playerId, commandSequence)')) { throw "Successful BuildBatch result reconciliation does not complete the client receipt state." }
if (-not $worldDeltaSource.Contains('ReplicationBuildingBlueprintBatchResultDeltaKind, StringComparison.Ordinal)') -or -not $worldDeltaSource.Contains('ReplicationBuildingBlueprintBatchPlacedDeltaKind, StringComparison.Ordinal)')) { throw "BuildBatch placement/result deltas are not in the priority apply lane." }
if (-not $worldDeltaSource.Contains('buildRecordB64=') -or -not $batchSource.Contains('FormatReplicationCanonicalBuildPlacementRecord')) { throw "Building snapshots do not retain canonical exact placement topology for isolated repair." }
if (-not $worldDeltaSource.Contains('building-state-exact-seed')) { throw "Snapshot-seeded buildings are not registered under the authoritative host identity." }
if (-not $worldDeltaSource.Contains('fullResyncRequired=yes')) { throw "Terminal building snapshot divergence does not identify the required full-resync recovery." }
if (-not $worldDeltaSource.Contains('GetBoundedSnapshotPageCount') -or -not $worldDeltaSource.Contains('HasPendingReplicationBuildingStateSnapshot') -or -not $worldDeltaSource.Contains('pageStart=') -or -not $worldDeltaSource.Contains('totalCount=')) { throw "Building repair checkpoints are not bounded, serialized, and page-addressed." }
if (-not $worldDeltaSource.Contains('GetReplicationBuildingRepairDependencyRank') -or -not $worldDeltaSource.Contains('Normal exact records (floors/walls/supports) seed first')) { throw "Paged building repair can transmit a roof before the missing support page it depends on." }
if (-not $worldDeltaSource.Contains('ReplicationBuildingStateSnapshotRetrySeconds = 3.0f') -or -not $worldDeltaSource.Contains('GetReplicationWorldObjectDeltaRetrySeconds(pending.Delta)')) { throw "Building snapshot retries no longer respect the client apply budget." }
if (-not $worldDeltaSource.Contains('remainingTimeAlreadyApplied') -or -not $worldDeltaSource.Contains('forbiddenAlreadyApplied') -or -not $worldDeltaSource.Contains('destructionAlreadyApplied') -or -not $worldDeltaSource.Contains('building-refresh-skipped-unchanged')) { throw "Building repair rows no longer skip idempotent lifecycle setters and refresh work." }
if (-not $batchSource.Contains('ShouldAcceptBuildBatch') -or -not $batchSource.Contains('TryRollbackAllTransactionViews') -or -not $batchSource.Contains('fullResyncRequired=yes')) { throw "BuildBatch acceptance is not tied to full exact commit count with deterministic rollback/recovery." }
if (-not [regex]::IsMatch($batchSource, 'state\.Dispose\(\);\s*resultCapture\.Dispose\(\);\s*var verified.*?TryRollbackAllTransactionViews', [System.Text.RegularExpressions.RegexOptions]::Singleline)) { throw "BuildBatch rollback can run before isolated manager collections publish and clean all transaction-owned views." }
if (-not $captureSource.Contains('RollbackProven') -or -not $captureSource.Contains('same-view-still-live') -or -not $captureSource.Contains('UniqueIdBuildingDictionary') -or -not $captureSource.Contains('exact-view-absent-building-still-registered') -or -not $captureSource.Contains('authoritative-batch-atomic-rollback')) { throw "Partial BuildBatch rollback is not verified against the exact transaction-owned native view and manager identity." }
if (-not $captureSource.Contains('results[i] = null') -or -not $captureSource.Contains('CommittedCount = Math.Max(0, CommittedCount - 1)') -or -not $worldDeltaSource.Contains("accepted.Append(placed ? '1' : '0')")) { throw "A proven host rollback does not survive as an all-rejected zero-ID canonical manifest." }
if (-not $batchSource.Contains('authoritative-batch-exception-rollback-failed') -or -not $batchSource.Contains('ShouldLatchBuildBatchRecovery')) { throw "A partial native exception with an unproven rollback does not latch BuildBatch replay and full-save recovery." }
if (-not $worldDeltaSource.Contains('host-build-batch-rollback-unproven') -or -not $worldDeltaSource.Contains('provisionalRetained=yes')) { throw "An unproven host rollback can discard client provisional state instead of scheduling full resync." }
if ($worldDeltaSource.Contains('TryApplyReplicationBuildPlacement(')) { throw "Building snapshot repair still uses the live interactive placement path." }
if ($legacyPlacementSource.Contains('hasSelectedItem') -or $legacyPlacementSource.Contains('InitializeBuilding')) { throw "Obsolete interactive placement code can reselect a building tool during replicated apply." }

Write-Host "PASS BuildingGameSurfaces"

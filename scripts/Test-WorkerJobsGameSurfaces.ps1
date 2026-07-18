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

function Assert-SourceMarker {
    param(
        [Parameter(Mandatory)][string] $Source,
        [Parameter(Mandatory)][string] $Marker,
        [Parameter(Mandatory)][string] $Failure
    )
    if (-not $Source.Contains($Marker)) { throw "$Failure Missing source marker: $Marker" }
}

function Assert-AnySourceMarker {
    param(
        [Parameter(Mandatory)][string] $Source,
        [Parameter(Mandatory)][string[]] $Markers,
        [Parameter(Mandatory)][string] $Failure
    )
    foreach ($marker in $Markers) {
        if ($Source.Contains($marker)) { return }
    }
    throw "$Failure Expected one of: $($Markers -join ', ')"
}

# The installed game has two legitimate entry points into the worker-job model:
# cell/row/column clicks converge through OnPriorityAdd, while every paste shape
# converges through JobPanelManager.PasteToWorker. Patching the model boundary is
# therefore both complete and independent of transient UI closure layouts.
$jobType = "NSMedieval.State.WorkerJobs.JobType"
$workerBehaviour = Get-GameType "NSMedieval.State.WorkerBehaviour"
$modifyJobPriority = Assert-Method $workerBehaviour "ModifyJobPriority" @($jobType, "System.Int32", "System.Boolean")
[void](Assert-Method $workerBehaviour "GetJobPriorityTruncated" @($jobType))
[void](Assert-Method $workerBehaviour "IsJobActive" @($jobType))

$workerJobManager = Get-GameType "NSMedieval.UI.WorkerJobManager"
$onPriorityAdd = Assert-Method $workerJobManager "OnPriorityAdd" @($jobType, "System.Int32", "System.Boolean")
$onColumnPriorityClick = Assert-Method $workerJobManager "OnColumnPriorityClick" @($jobType, "System.Int32")
$onRowPriorityClick = Assert-Method $workerJobManager "OnRowPriorityClick" @("System.Int32")
$updateToggles = Assert-Method $workerJobManager "UpdateToggles" @()
$setWorker = Assert-Method $workerJobManager "SetWorker" @("NSMedieval.State.HumanoidInstance", "NSMedieval.UI.WorkerPanelManager")
Assert-Calls $onPriorityAdd "WorkerBehaviour::ModifyJobPriority(NSMedieval.State.WorkerJobs.JobType,System.Int32,System.Boolean)"
Assert-Calls $onColumnPriorityClick "WorkerJobManager::OnPriorityAdd(NSMedieval.State.WorkerJobs.JobType,System.Int32,System.Boolean)"
Assert-Calls $onRowPriorityClick "WorkerJobManager::OnPriorityAdd(NSMedieval.State.WorkerJobs.JobType,System.Int32,System.Boolean)"
Assert-Calls $setWorker "WorkerJobManager::UpdateToggles()"

$jobPanelManager = Get-GameType "NSMedieval.UI.JobPanelManager"
$pasteToWorker = Assert-Method $jobPanelManager "PasteToWorker" @("NSMedieval.State.HumanoidInstance")
Assert-Calls $pasteToWorker "WorkerBehaviour::ModifyJobPriority(NSMedieval.State.WorkerJobs.JobType,System.Int32,System.Boolean)"

$modifyCallers = @($types | ForEach-Object {
    $_.Methods | Where-Object { $_.HasBody } | Where-Object {
        @($_.Body.Instructions | ForEach-Object { [string]$_.Operand }) -match
            [regex]::Escape("WorkerBehaviour::ModifyJobPriority(NSMedieval.State.WorkerJobs.JobType,System.Int32,System.Boolean)")
    }
})
$modifyCallerNames = @($modifyCallers | ForEach-Object FullName)
$expectedModifyCallers = ($modifyCallers.Count -eq 2) `
    -and ($modifyCallerNames -contains $onPriorityAdd.FullName) `
    -and ($modifyCallerNames -contains $pasteToWorker.FullName)
if (-not $expectedModifyCallers) {
    throw "WorkerBehaviour.ModifyJobPriority caller topology changed: $($modifyCallerNames -join '; ')."
}

$workerJobsPath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorkerJobs.cs"
if (-not (Test-Path -LiteralPath $workerJobsPath -PathType Leaf)) {
    throw "Worker Jobs absolute-state implementation is missing: $workerJobsPath"
}

$workerJobsSource = Get-Content -LiteralPath $workerJobsPath -Raw
$managementSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationManagement.cs") -Raw
$runtimeSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs") -Raw
$worldDeltaSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs") -Raw
$commandSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandApplication.cs") -Raw
$captureSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationCommandCapture.cs") -Raw
$payloadSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs") -Raw

# Capture must stay at the exact model boundary. UI prefix capture misses paste
# and mistakes OnPriorityAdd's third argument (silent) for model active state.
foreach ($marker in @(
    'TryInstallReplicationWorkerJobsCapture',
    '"NSMedieval.State.WorkerBehaviour"',
    '"ModifyJobPriority"',
    'nameof(ReplicationWorkerJobsModelPrefix)',
    'nameof(ReplicationWorkerJobsModelPostfix)',
    'replicationWorkerJobsAuthoritativeApplyDepth',
    'GetJobPriorityTruncated',
    'IsJobActive',
    'FlushReplicationWorkerJobsChanges')) {
    Assert-SourceMarker $workerJobsSource $marker "Worker Jobs model capture is incomplete."
}
if ($workerJobsSource.Contains('"OnPriorityAdd"') -or $managementSource.Contains('nameof(ReplicationWorkerJobPrefix)')) {
    throw "Worker Jobs still patches the relative UI entry point instead of only the model boundary."
}

# One frame of model mutations is deliberately collapsed to final absolute rows.
# Flush before inbound transport so local optimistic changes become durable intent
# before an older authoritative state can arrive in the same frame.
$flushCall = 'FlushReplicationWorkerJobsChanges();'
$pumpCall = 'PumpReplicationTransport();'
$flushIndex = $runtimeSource.IndexOf($flushCall, [StringComparison]::Ordinal)
$pumpIndex = $runtimeSource.IndexOf($pumpCall, [StringComparison]::Ordinal)
if ($flushIndex -lt 0 -or $pumpIndex -lt 0 -or $flushIndex -gt $pumpIndex) {
    throw "Worker Jobs dirty changes must flush immediately before PumpReplicationTransport."
}
Assert-SourceMarker $runtimeSource 'ResetReplicationWorkerJobsRuntimeState();' "Worker Jobs runtime state is not reset across sessions."

# The native method accepts a delta against GetJobPriorityTruncated. Its
# current + delta - 5 write is a separate baseline-relative save value, while
# WorkerGoapAgent.ChangeJobPriority applies the delta to observable state.
# Applying absolute state therefore requires desired - current, with no +5.
$absolutePriorityFormula = '(?is)(desired|requested|target)\w*\s*-\s*(\w+\.)?(current|previous|actual)\w*'
if (-not [regex]::IsMatch($workerJobsSource, $absolutePriorityFormula)) {
    throw "Worker Jobs apply is missing the native absolute-priority conversion (desired - current)."
}

# Apply is authoritative and transactional: validate a complete known job row,
# read back the model, roll back partial failure, and refresh the visible row once.
foreach ($marker in @(
    'TryApplyReplicationWorkerJobsUpdate',
    'TryApplyReplicationWorkerJobsState',
    'TryReadReplicationWorkerJob',
    'CompleteReplicationWorkerJobsStateProof',
    'ReplicationHostWorkerJobsIntentSequenceByJob',
    'ReplicationWorkerJobsDirtyRetryAtByTarget',
    'retained for retry',
    'replicationApplyingRemoteManagementCommandSequence',
    'UpdateToggles',
    'refresh-failed:')) {
    Assert-SourceMarker $workerJobsSource $marker "Worker Jobs authoritative apply is incomplete."
}
Assert-AnySourceMarker $workerJobsSource @('GetWorkerJobs', 'AllJobTypes') "Worker Jobs state does not enumerate the complete native worker-job set."
if (-not [regex]::IsMatch($workerJobsSource, '(?i)rollback')) {
    throw "Worker Jobs batch apply has no explicit rollback path for partial failure."
}

# Typed absolute update/state contracts replace the old generic relative policy.
foreach ($marker in @(
    'WorkerJobsUpdateAction',
    'WorkerJobsStateAction',
    'CreateWorkerJobsUpdatePayload',
    'TryReadWorkerJobsUpdatePayload',
    'CreateWorkerJobsStatePayload',
    'TryReadWorkerJobsStatePayload')) {
    Assert-SourceMarker $payloadSource $marker "Worker Jobs payload contract is incomplete."
}
foreach ($marker in @(
    'CreateWorkerJobsUpdatePayload',
    'CreateWorkerJobsStatePayload')) {
    Assert-SourceMarker $workerJobsSource $marker "Worker Jobs runtime does not emit typed absolute payloads."
}
foreach ($marker in @(
    'TryReadWorkerJobsUpdatePayload',
    'TryApplyReplicationWorkerJobsUpdate')) {
    Assert-SourceMarker $commandSource $marker "Worker Jobs command application is incomplete."
}
foreach ($marker in @(
    'TryReadWorkerJobsUpdatePayload',
    'SendReplicationWorkerJobsState',
    'TryReadWorkerJobsStatePayload',
    'TryApplyReplicationWorkerJobsState',
    'rejected-command-correction')) {
    Assert-SourceMarker $managementSource $marker "Worker Jobs accepted/rejected authoritative-state exchange is incomplete."
}
Assert-SourceMarker $managementSource 'worker-job-legacy-payload-unsupported' "The unsafe generic relative WorkerJob payload is not explicitly rejected."

# Client intent and host state are retained until an authoritative full-row proof.
foreach ($marker in @(
    'IsReplicationWorkerJobsUpdateCommand',
    'CoalesceReplicationPendingWorkerJobsIntent',
    'ReplicationManagementIntentDormantRetrySeconds')) {
    Assert-SourceMarker $captureSource $marker "Worker Jobs reliable intent path is incomplete."
}
foreach ($marker in @(
    'IsReplicationWorkerJobsStateDelta',
    'ReplicationWorkerJobsStateDurableRetrySeconds',
    'durableWorkerJobsState',
    '|policy=WorkerJobsState|target=')) {
    Assert-SourceMarker $worldDeltaSource $marker "Worker Jobs durable authoritative-state path is incomplete."
}
foreach ($marker in @(
    'IsReplicationWorkerJobsUpdateCommand',
    'ReplicationManagementWireVersion = "3"')) {
    Assert-SourceMarker $runtimeSource $marker "Worker Jobs runtime compatibility/retry handling is incomplete."
}
if ([regex]::Matches($runtimeSource, 'IsReplicationWorkerJobsUpdateCommand').Count -lt 2) {
    throw "Worker Jobs durable intent must survive ACK until state proof and re-emit state on duplicate commands."
}

Write-Host "PASS WorkerJobsGameSurfaces model-boundary/absolute-row/batch/reliable-intent/guard/state/proof/UI-refresh contracts"

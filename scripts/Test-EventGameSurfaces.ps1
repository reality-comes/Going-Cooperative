[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"
$eventReplicationSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationEvents.cs"
$externalEventAgentsSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationExternalEventAgents.cs"
$replicationConfigSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationConfig.cs"
$eventCheckpointTrackerSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Core\EventCheckpointTracker.cs"
$gameTimeSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationGameTimeSync.cs"
$replicationRuntimeSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationRuntime.cs"
$pluginSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Plugin.cs"
$saveTransferSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Multiplayer\MultiplayerSaveTransfer.cs"
$saveWorkflowSourcePath = Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Multiplayer\MultiplayerSaveWorkflow.cs"

if (-not (Test-Path -LiteralPath $cecilPath -PathType Leaf)) { throw "Mono.Cecil is missing at $cecilPath." }
if (-not (Test-Path -LiteralPath $gameAssemblyPath -PathType Leaf)) { throw "Assembly-CSharp is missing at $gameAssemblyPath." }
if (-not (Test-Path -LiteralPath $eventReplicationSourcePath -PathType Leaf)) { throw "Event replication source is missing at $eventReplicationSourcePath." }
if (-not (Test-Path -LiteralPath $externalEventAgentsSourcePath -PathType Leaf)) { throw "External event-agent source is missing at $externalEventAgentsSourcePath." }
if (-not (Test-Path -LiteralPath $replicationConfigSourcePath -PathType Leaf)) { throw "Replication config source is missing at $replicationConfigSourcePath." }
if (-not (Test-Path -LiteralPath $eventCheckpointTrackerSourcePath -PathType Leaf)) { throw "Event checkpoint tracker source is missing at $eventCheckpointTrackerSourcePath." }
if (-not (Test-Path -LiteralPath $gameTimeSourcePath -PathType Leaf)) { throw "Game-time replication source is missing at $gameTimeSourcePath." }
if (-not (Test-Path -LiteralPath $replicationRuntimeSourcePath -PathType Leaf)) { throw "Replication runtime source is missing at $replicationRuntimeSourcePath." }
if (-not (Test-Path -LiteralPath $pluginSourcePath -PathType Leaf)) { throw "Plugin source is missing at $pluginSourcePath." }
if (-not (Test-Path -LiteralPath $saveTransferSourcePath -PathType Leaf)) { throw "Save transfer source is missing at $saveTransferSourcePath." }
if (-not (Test-Path -LiteralPath $saveWorkflowSourcePath -PathType Leaf)) { throw "Save workflow source is missing at $saveWorkflowSourcePath." }

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
    if ($null -eq $type) { throw "Event native surface type missing: $FullName." }
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
    if ($null -eq $method) {
        throw "Event native surface method missing: $($Type.FullName).$Name($($Parameters -join ', '))."
    }
    return $method
}

function Require-GenericMethod {
    param(
        [Parameter(Mandatory)] $Type,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][int] $GenericParameterCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Parameters,
        [Parameter(Mandatory)][string] $ReturnType
    )
    $method = $Type.Methods | Where-Object {
        if ($_.Name -ne $Name -or
            $_.GenericParameters.Count -ne $GenericParameterCount -or
            $_.Parameters.Count -ne $Parameters.Count -or
            $_.ReturnType.FullName -ne $ReturnType) { return $false }
        for ($i = 0; $i -lt $Parameters.Count; $i++) {
            if ($_.Parameters[$i].ParameterType.FullName -ne $Parameters[$i]) { return $false }
        }
        return $true
    } | Select-Object -First 1
    if ($null -eq $method) {
        throw "Event native generic surface method missing: $($Type.FullName).$Name<$GenericParameterCount>($($Parameters -join ', ')) -> $ReturnType."
    }
    return $method
}

function Require-Field {
    param([Parameter(Mandatory)] $Type, [Parameter(Mandatory)][string] $Name)
    $field = $Type.Fields | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($null -eq $field) { throw "Event native surface field missing: $($Type.FullName).$Name." }
    return $field
}

function Require-Property {
    param([Parameter(Mandatory)] $Type, [Parameter(Mandatory)][string] $Name)
    $property = $Type.Properties | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($null -eq $property) { throw "Event native surface property missing: $($Type.FullName).$Name." }
    return $property
}

function Get-InstructionText {
    param([Parameter(Mandatory)] $Method)
    if (-not $Method.HasBody) { return "" }
    return [string]::Join("`n", @($Method.Body.Instructions | ForEach-Object { $_.OpCode.Name + " " + [string]$_.Operand }))
}

function Require-InstructionText {
    param([Parameter(Mandatory)] $Method, [Parameter(Mandatory)][string] $Text, [Parameter(Mandatory)][string] $Reason)
    if (-not (Get-InstructionText -Method $Method).Contains($Text)) {
        throw "Event native IL contract changed: $Reason ($($Method.DeclaringType.FullName).$($Method.Name))."
    }
}

$scheduler = Require-Type "NSMedieval.GameEventSystem.EventScheduler"
[void](Require-Method $scheduler "OnDateUpdate" @())
[void](Require-Method $scheduler "OnTimeUpdate" @())
[void](Require-Method $scheduler "ScheduleEventGroup" @("NSMedieval.GameEventSystem.EventGroupInstance", "System.Int64"))
[void](Require-Method $scheduler "OnRaidEndedEvent" @("NSMedieval.Manager.ActiveRaidInfo"))

$eventSystem = Require-Type "NSMedieval.GameEventSystem.GameEventSystem"
$startEvent = Require-Method $eventSystem "StartEvent" @("System.String")
if ($startEvent.ReturnType.FullName -ne "System.Boolean") { throw "GameEventSystem.StartEvent return type changed." }
[void](Require-Method $eventSystem "OnGameLoaded" @("System.Boolean"))
[void](Require-Method $eventSystem "IsBlockingEventRunning" @())
[void](Require-Method $eventSystem "IsBlockingObjectiveButton" @())
[void](Require-Method $eventSystem "IsEventRunning" @("System.String"))
[void](Require-Method $eventSystem "RunningEventsWeatherTextKey" @())
[void](Require-Property $eventSystem "RunningEvents")

$worldTimeManager = Require-Type "NSMedieval.WorldTimeManager"
$worldDate = Require-Type "NSMedieval.WorldDate"
[void](Require-Method $worldDate "SetMinutesTotal" @("System.UInt32"))
[void](Require-Method $worldDate "SetMinuteFractionalPart" @("System.Single"))
if (-not ($worldTimeManager.Fields | Where-Object Name -eq "timerCorrector")) { throw "WorldTimeManager.timerCorrector is missing." }

$eventSource = Get-Content -LiteralPath $eventReplicationSourcePath -Raw
$externalEventAgentsSource = Get-Content -LiteralPath $externalEventAgentsSourcePath -Raw
$replicationConfigSource = Get-Content -LiteralPath $replicationConfigSourcePath -Raw
$eventCheckpointTrackerSource = Get-Content -LiteralPath $eventCheckpointTrackerSourcePath -Raw
$gameTimeSource = Get-Content -LiteralPath $gameTimeSourcePath -Raw
$saveWorkflowSource = Get-Content -LiteralPath $saveWorkflowSourcePath -Raw
if (-not $eventSource.Contains("checkpoint-collect-failed")) { throw "Event checkpoints must abort on registry collection failure." }
if (-not $eventSource.Contains("PreparedHostReplicationEvent")) { throw "Event checkpoints must preflight all projected rows before Begin." }
if (-not $eventSource.Contains("preparedEventIds") -or -not $eventSource.Contains("prepared.Sort")) { throw "Event checkpoints must deduplicate and deterministically order prepared registry rows." }
if (-not $eventSource.Contains("EventCheckpointTracker") -or -not $eventCheckpointTrackerSource.Contains("TryTakeNewestComplete")) { throw "Event checkpoint out-of-order buffering is missing." }
if ($eventSource.Contains("event-registry-end-unmatched") -or $eventSource.Contains("event-state-checkpoint-not-started")) { throw "Event checkpoint records must be buffered instead of rejected solely for arriving before Begin." }
if (-not $eventSource.Contains("completion.BeginSequence")) { throw "Event checkpoint pruning must use the Begin boundary so post-Begin states survive delayed End delivery." }
if (-not $eventSource.Contains("ReplicationEventExternalAgentLifecycleImplemented()") -or $eventSource -notmatch '(?s)private static bool ReplicationEventExternalAgentLifecycleImplemented\(\)\s*\{\s*return false;') { throw "External merchant/visitor/raid actor lifecycle must remain explicitly unimplemented and fail closed." }
if ($eventSource -notmatch '(?s)"NSMedieval\.GameEventSystem\.EventScheduler",\s*"OnDateUpdate".*?suppressVoid.*?"NSMedieval\.GameEventSystem\.EventScheduler",\s*"OnTimeUpdate".*?suppressVoid.*?"NSMedieval\.GameEventSystem\.EventScheduler",\s*"ScheduleEventGroup".*?suppressVoid.*?"NSMedieval\.GameEventSystem\.EventScheduler",\s*"OnRaidEndedEvent".*?suppressVoid') { throw "Event scheduler hooks must remain behind full graph authority." }
if ($eventSource.Contains("suppressSchedulingVoid") -or $eventSource.Contains("ShouldSuppressReplicationClientEventStarts")) { throw "Blanket connected-client event scheduler/start suppression must not be present." }
if ($eventSource -notmatch '(?s)private static bool ShouldSuppressReplicationClientTraderEventStart\(bool traderEvent\).*?TraderEventAuthorityPolicy\.ShouldSuppressClientNativeTraderStart.*?replicationRemoteHelloReceived.*?multiplayerLoadingInProgress') { throw "Trader start authority no longer requires an exact family match and compatible post-load runtime." }
if ($eventSource -notmatch '(?s)private static bool ReplicationEventStartEventPrefix\(string __0, ref bool __result\).*?ShouldSuppressReplicationClientTraderEventStart\(IsReplicationTraderEventBlueprintId\(__0\)\)' -or $eventSource -notmatch '(?s)private static bool ReplicationEventInstanceStartPrefix.*?traderEvent\s*=\s*IsReplicationTraderEventInstance\(__instance\).*?ShouldSuppressReplicationClientTraderEventStart\(traderEvent\)') { throw "TraderEvent/MultiTraderEvent native starts are not routed through the targeted authority gate." }
if ($eventSource -notmatch '(?s)replicationTraderEventStartHooksReady\s*=\s*traderStartEventHookCount\s*==\s*1\s*&&\s*traderInstanceStartHookCount\s*==\s*1' -or $eventSource -notmatch '(?s)private static bool TraderEventAuthorityEnabled\(\).*?replicationConfigEventReplication.*?replicationConfigEventLifecycleReplication.*?replicationConfigEventTraderAuthority.*?replicationTraderEventStartHooksReady.*?worldObjectDeltaMode.*?ReplicationTraderPartySurfacesReady') { throw "Trader authority must have an independent exact-hook and party-surface readiness gate." }
if ($eventSource -notmatch '(?s)private static bool EventRegistryReplicationLaneEnabled\(\).*?EventLifecycleLaneEnabled\(\)\s*\|\|\s*TraderEventAuthorityEnabled\(\)' -or
    $eventSource -notmatch '(?s)ScanHostReplicationEventsIfDue\(\).*?traderOnly.*?IsReplicationTraderEventInstance' -or
    $eventSource -notmatch '(?s)SendHostReplicationEventCheckpoint\(string source\).*?traderOnly.*?IsReplicationTraderEventInstance') { throw "Minimal trader authority must poll/checkpoint only trader registry rows when the full event hook set is unavailable." }
if ($eventSource -notmatch 'PrepareHostReplicationTraderParty\(__instance, record\.EventId, "native-start", out var detail\)' -or
    $eventSource -notmatch 'AbortHostReplicationTraderParty\(__instance, record\.EventId, detail\)' -or
    $eventSource -notmatch 'EnsureHostReplicationTraderParty\(nativeEvent, traderRecord\.EventId, "registry-scan"\)' -or
    $eventSource -notmatch 'EnsureHostReplicationTraderParty\(nativeEvent, traderRecord\.EventId, "event-checkpoint"\)' -or
    $eventSource -notmatch 'RemoveClientReplicationTraderParty\(stale\[i\], "event-checkpoint-prune"\)' -or
    $eventSource -notmatch 'RemoveClientReplicationTraderParty\(eventId, "event-tombstone"\)' -or
    $eventSource -notmatch 'ResetReplicationTraderParties\(ReplicationTraderPartyResetContext\.ScopeChangedSameWorld\)') { throw "Trader party lifecycle is not linked to two-phase event start, checkpoint pruning, tombstone, and same-world reset." }
if ($externalEventAgentsSource -notmatch '(?s)internal static bool ReplicationTraderPartySurfacesReady\(\).*?ValidateReplicationTraderPartyNativeSurfaces') { throw "Trader authority readiness must validate native party surfaces on demand before hook installation." }
if ($externalEventAgentsSource -notmatch '(?s)"NSMedieval\.State\.TraderBehaviour",\s*"OnSettlerTalkTo",\s*new\[\]\s*\{\s*AccessTools\.TypeByName\("NSMedieval\.State\.WorkerBehaviour"\)\s*\}.*?tradeTalkPrefix' -or
    $externalEventAgentsSource -notmatch 'replicationTraderPartyHooksReady\s*=\s*count\s*==\s*15') { throw "Trader authority must install all 15 exact party hooks, including the read-only client trader-talk guard." }
if ($replicationConfigSource -notmatch 'case "eventtraderauthority"' -or $replicationConfigSource -notmatch 'replicationConfigEventTraderAuthority') { throw "eventTraderAuthority is not parsed as a distinct config gate." }
if ($eventSource -notmatch '(?s)IsReplicationTraderEventBlueprintId.*?GameEventSettingsRepository.*?GetByID.*?ClassName.*?"TraderEvent".*?"MultiTraderEvent"') { throw "StartEvent blueprint classification must resolve the native event class exactly." }
if ($eventSource -notmatch '(?s)"NSMedieval\.GameEventSystem\.GameEventInstance",\s*"Tick".*?suppressVoid.*?"NSMedieval\.GameEventSystem\.GameEventInstance",\s*"ForceEnd".*?suppressVoid.*?"NSMedieval\.GameEventSystem\.GameEventStateMachine",\s*"SwitchPhase".*?suppressVoid') { throw "Loaded/running event graph hooks must remain behind full authority rather than the scheduler/start gate." }
if ($eventSource -notmatch '(?s)private static bool ReplicationEventOnGameLoadedPrefix.*?ShouldSuppressReplicationClientEvents\(\)') { throw "Native OnGameLoaded event initialization must remain outside the narrow scheduler/start gate." }
if ($saveWorkflowSource -notmatch 'replicationClientEventAuthorityParked\s*=\s*!replicationConfigHostMode\s*&&\s*FullEventGraphAuthorityEnabled\(\)' -or $saveWorkflowSource.Contains("TraderEventAuthorityEnabled()")) { throw "Native checkpoint loading must never park event graphs under the trader authority gate." }
if ($eventSource.Contains("if (!TryGetNativeRunningReplicationEvents(out var runningEvents)) runningEvents = new ArrayList()")) { throw "Event checkpoint false-empty fallback was restored." }
if (-not $gameTimeSource.Contains("TryInvokeReplicationVoidMethod")) { throw "Game-time void setter semantics are not guarded." }
if (-not $gameTimeSource.Contains("timerCorrector")) { throw "Game-time fractional correction must update timerCorrector." }
if ($gameTimeSource.Contains("Math.Abs(unityDrift) <")) { throw "Unity scene time must not drive calendar correction." }
[void](Require-Field $eventSystem "runningEventsID")

$eventInstance = Require-Type "NSMedieval.GameEventSystem.GameEventInstance"
[void](Require-Type "NSMedieval.GameEventSystem.GameEvent")
$eventBaseModel = Require-Type "NSMedieval.EventBase.EventBaseModel"
[void](Require-Property $eventBaseModel "ClassName")
[void](Require-Type "NSMedieval.GameEventSystem.Events.TraderEvent")
[void](Require-Type "NSMedieval.GameEventSystem.Events.MultiTraderEvent")
$traderBehaviour = Require-Type "NSMedieval.State.TraderBehaviour"
[void](Require-Method $traderBehaviour "OnSettlerTalkTo" @("NSMedieval.State.WorkerBehaviour"))

# Trader pilot spawn/serialize/removal seams. These are deliberately exact for
# the locally installed Going Medieval build because FV payloads and native
# manager registration are unsafe to guess across assembly drift.
$tradingManager = Require-Type "NSMedieval.UI.TradingManager"
[void](Require-Method $tradingManager "InitTrader" @(
    "NSMedieval.State.HumanoidInstance",
    "NSMedieval.UI.TraderType",
    'System.Collections.Generic.List`1<NSMedieval.State.CreatureBase>&',
    "NSMedieval.GameEventSystem.GameEventInstance"))
[void](Require-Method $tradingManager "OpenTradingMenu" @("NSMedieval.UI.ITrader", "NSMedieval.UI.ITrader"))
[void](Require-Method $tradingManager "ApplyTrade" @("System.Single"))
$tradingManagerTraderField = Require-Field $tradingManager "trader"
if ($tradingManagerTraderField.FieldType.FullName -ne "NSMedieval.UI.ITrader") { throw "TradingManager.trader type changed." }

$npcController = Require-Type "NSMedieval.NPCController"
[void](Require-Method $npcController "RemoveNPC" @("NSMedieval.State.HumanoidInstance"))
$animalManager = Require-Type "NSMedieval.Manager.AnimalManager"
[void](Require-Method $animalManager "RemoveAnimal" @("NSMedieval.State.AnimalInstance", "System.Boolean"))
[void](Require-Method $animalManager "InstantiateAnimal" @("NSMedieval.State.AnimalInstance", "System.Boolean"))
$animalManagerGetView = Require-Method $animalManager "GetView" @("NSMedieval.State.AnimalInstance")
if ($animalManagerGetView.ReturnType.FullName -ne "NSMedieval.View.Animals.AnimalView") { throw "AnimalManager.GetView return type changed." }

$creatureBase = Require-Type "NSMedieval.State.CreatureBase"
$creatureUniqueIdField = Require-Field $creatureBase "uniqueId"
$creatureUniqueIdProperty = Require-Property $creatureBase "UniqueId"
if ($creatureUniqueIdField.FieldType.FullName -ne "System.Int32" -or
    $creatureUniqueIdProperty.PropertyType.FullName -ne "System.Int32") { throw "CreatureBase unique-ID surface changed." }

$villageSaveData = Require-Type "NSMedieval.State.VillageSaveData"
[void](Require-Method $villageSaveData "AddNPC" @("NSMedieval.State.HumanoidInstance"))
[void](Require-Method $villageSaveData "AddAnimal" @("NSMedieval.State.AnimalInstance"))
$npcManager = Require-Type "NSMedieval.Manager.NPCManager"
[void](Require-Method $npcManager "LoadSavedNPC" @("NSMedieval.State.HumanoidInstance"))
$npcManagerGetView = Require-Method $npcManager "GetView" @("NSMedieval.State.HumanoidInstance")
if ($npcManagerGetView.ReturnType.FullName -ne "NSMedieval.View.NPCView") { throw "NPCManager.GetView return type changed." }

$animalInstance = Require-Type "NSMedieval.State.AnimalInstance"
[void](Require-Method $animalInstance "AssignPetOwner" @("NSMedieval.State.CreatureBase"))
[void](Require-Method $animalInstance "RopeTo" @("NSMedieval.Goap.IGoapTargetable", "System.Boolean"))
[void](Require-Method $animalInstance "SetAnimalType" @("NSMedieval.Types.AnimalType"))
$captiveBehaviour = Require-Type "NSMedieval.State.CaptiveNpcBehaviour"
$captiveOwner = Require-Property $captiveBehaviour "Owner"
if ($captiveOwner.PropertyType.FullName -ne "NSMedieval.State.HumanoidInstance" -or $null -eq $captiveOwner.SetMethod) {
    throw "CaptiveNpcBehaviour.Owner setter contract changed."
}
$humanoidBehaviour = Require-Type "NSMedieval.State.HumanoidBehaviour"
$humanoidBehaviourOwner = Require-Property $humanoidBehaviour "Humanoid"
if ($humanoidBehaviourOwner.PropertyType.FullName -ne "NSMedieval.State.HumanoidInstance") {
    throw "HumanoidBehaviour.Humanoid owner link changed."
}

$fvSerializer = Require-Type "NSMedieval.Serialization.FVSerializer"
[void](Require-Method $fvSerializer ".ctor" @("System.String", "System.String[]"))
[void](Require-GenericMethod $fvSerializer "Write" 1 @("System.String", 'System.Collections.Generic.IList`1<T>') "System.Void")
[void](Require-Method $fvSerializer "WriteReferences" @())
[void](Require-Method $fvSerializer "GetBytes" @("System.String"))
[void](Require-Method $fvSerializer "GetReferenceBytes" @())
$fvDeserializer = Require-Type "NSMedieval.Serialization.FVDeserializer"
[void](Require-Method $fvDeserializer ".ctor" @("System.String", "System.Byte[]"))
[void](Require-GenericMethod $fvDeserializer "ReadObjectList" 1 @("System.String", 'System.Collections.Generic.List`1<T>') 'System.Collections.Generic.List`1<T>')
[void](Require-Method $fvDeserializer "ReadReferences" @("System.Byte[]"))

$eventStart = Require-Method $eventInstance "Start" @()
$eventForceEnd = Require-Method $eventInstance "ForceEnd" @()
[void](Require-Method $eventInstance "Tick" @("System.Single"))
$eventOnEnd = Require-Method $eventInstance "OnEnd" @()
[void](Require-Field $eventInstance "warningMessage")
[void](Require-Property $eventInstance "WarningTooltipPrefixLines")
if ($eventStart.ReturnType.FullName -ne "System.Boolean") { throw "GameEventInstance.Start return type changed." }
if ($eventOnEnd.ReturnType.FullName -ne "System.Void") { throw "GameEventInstance.OnEnd return type changed." }
Require-InstructionText $eventStart "GetStartingPhase" "event Start must still execute its native starting phase"
Require-InstructionText $eventStart "RunEffectors" "event Start must still execute native effectors"
Require-InstructionText $eventStart "GameEventStateMachine::Start" "event Start must still start the native state machine"
Require-InstructionText $eventStart "ShowStartText" "event Start must still emit its native start notice"
Require-InstructionText $eventStart "CreateWarningMessage" "event Start must still create its native warning"
Require-InstructionText $eventForceEnd "GameEventStateMachine::ForceEnd" "event ForceEnd must still tear down the native state machine"
Require-InstructionText $eventOnEnd "ShowEndText" "event OnEnd must still emit its native end notice"
Require-InstructionText $eventOnEnd "RemoveWarningMessage" "event OnEnd must still remove its native warning"
$createEventWarning = Require-Method $eventInstance "CreateWarningMessage" @()
Require-InstructionText $createEventWarning "WarningMessageData::.ctor" "event warning construction path changed"

$stateMachine = Require-Type "NSMedieval.GameEventSystem.GameEventStateMachine"
[void](Require-Field $stateMachine "currentPhase")
[void](Require-Field $stateMachine "parentEventInstance")
[void](Require-Method $stateMachine "SwitchPhase" @("NSMedieval.GameEventSystem.GameEventPhaseBase"))
$eventReplicationSource = Get-Content -LiteralPath $eventReplicationSourcePath -Raw
if ($eventReplicationSource -notmatch '(?s)"NSMedieval\.GameEventSystem\.GameEventStateMachine",\s*"SwitchPhase".*?suppressVoid,\s*phasePostfix') {
    throw "Client GameEventStateMachine.SwitchPhase suppression hook is missing."
}
if ($eventReplicationSource -notmatch '(?s)event state extraction failed event=.*?return false;') {
    throw "Event state extraction no longer fails closed."
}
$replicationRuntimeSource = Get-Content -LiteralPath $replicationRuntimeSourcePath -Raw
$pluginSource = Get-Content -LiteralPath $pluginSourcePath -Raw
if ($replicationRuntimeSource -notmatch '\+ ":6";' -or $replicationRuntimeSource -notmatch "fingerprint\[19\] != '6'") {
    throw "Event capability writer/parser wire versions do not both use v6."
}
if ($replicationRuntimeSource -notmatch '(?s)replicationConfigEventTraderAuthority \? "1" : "0".*?TraderEventAuthorityEnabled\(\) \? "1" : "0"') { throw "Event capability does not distinguish configured from effective trader authority readiness." }
if ($replicationRuntimeSource -notmatch '"\|gameasm="' -or
    $replicationRuntimeSource -notmatch 'ManifestModule\.ModuleVersionId' -or
    $replicationRuntimeSource -notmatch 'moduleVersionId\.ToString\("N"\)' -or
    $replicationRuntimeSource -notmatch '(?s)traderSerializerCompatibilityRequired\s*=\s*replicationConfigEventTraderAuthority.*?remoteEventCapabilities\[2\]\s*==\s*''1''.*?TraderSerializerCompatibilityPolicy\.Evaluate\(\s*traderSerializerCompatibilityRequired.*?RemoteIdentityMissing.*?AssemblyMismatch') {
    throw "Trader FV serializer authority is not hard-bound to an exact Assembly-CSharp MVID during hello."
}
if ($pluginSource -notmatch '(?s)TryInstallReplicationExternalEventAgentHooks\(harmony\);.*?TryStartReplicationRuntime\(\);') {
    throw "The local capability/build hash can be computed before effective trader-hook readiness is known."
}
if ($replicationRuntimeSource -notmatch 'ReplicationHostCommandResultRetention = 8192') {
    throw "Host command result retention is no longer bounded to the reviewed window."
}
$saveTransferSource = Get-Content -LiteralPath $saveTransferSourcePath -Raw
if ($saveTransferSource -notmatch '(?s)else\s*\{\s*lastFingerprint = fingerprint;\s*stableSamples = 1;\s*fingerprintStableSince = DateTime\.UtcNow;' -or
    $saveTransferSource -notmatch 'DateTime\.UtcNow - fingerprintStableSince\.Value >= TimeSpan\.FromMilliseconds\(500\)') {
    throw "Save bundle quiet time is not measured from the last fingerprint change."
}

$showDialog = Require-Type "NSMedieval.GameEventSystem.Events.ShowDialogPhase"
[void](Require-Method $showDialog "OnClose" @("System.Int32"))
[void](Require-Field $showDialog "overrideDialogImage")
$branchingDialog = Require-Type "NSMedieval.GameEventSystem.ShowDialogPhaseBranching"
$branchClose = Require-Method $branchingDialog "OnClose" @("System.Int32")
[void](Require-Field $branchingDialog "switchPhaseIndexNextTick")
Require-InstructionText $branchClose "switchPhaseIndexNextTick" "branch choice must still be retained for the next event tick"

$dialogManager = Require-Type "NSMedieval.Dialogs.DialogViewManager"
[void](Require-Field $dialogManager "OnClose")
[void](Require-Field $dialogManager "view")
[void](Require-Method $dialogManager "Close" @("System.Int32"))

$localizationController = Require-Type "NSMedieval.Controllers.LocalizationController"
[void](Require-Method $localizationController "GetText" @("System.String"))

$gameEventUtil = Require-Type "NSMedieval.GameEventSystem.GameEventUtil"
[void](Require-Method $gameEventUtil "BuildDialogContent" @("NSMedieval.GameEventSystem.GameEventInstance", "NSMedieval.GameEventSystem.GameEvent/DialogContent"))
$dialogContent = Require-Type "NSMedieval.Dialogs.Data.DialogContent"
foreach ($field in @("WindowTitle", "ContentTitle", "ContentBodyText", "ContentBodyImagePath", "Options", "ShowCloseButton")) {
    [void](Require-Field $dialogContent $field)
}
$dialogOption = Require-Type "NSMedieval.Dialogs.Data.DialogOption"
foreach ($field in @("Text", "Tooltips", "Disabled", "DisabledTooltip")) {
    [void](Require-Field $dialogOption $field)
}
$tooltipData = Require-Type "NSMedieval.Dialogs.Data.TooltipData"
foreach ($field in @("Key", "Args", "Humanoid")) {
    [void](Require-Field $tooltipData $field)
}
$warningMessage = Require-Type "NSMedieval.Model.WarningMessageData"
foreach ($property in @("ID", "Text", "Tooltip", "Icon", "Timer", "ShouldShow", "FactionInstance")) {
    [void](Require-Property $warningMessage $property)
}
$delayCountdown = Require-Type "NSMedieval.GameEventSystem.Events.DelayCountdownPhase"
[void](Require-Property $delayCountdown "CountdownMessage")
[void](Require-Field $delayCountdown "additionalTooltipLines")
$delayInitMessage = Require-Method $delayCountdown "InitMessage" @()
Require-InstructionText $delayInitMessage "WarningMessageData::.ctor" "delay countdown warning construction path changed"
$waitCountdown = Require-Type "NSMedieval.GameEventSystem.Events.WaitUntilTimeframePhase"
[void](Require-Property $waitCountdown "CountdownMessage")
[void](Require-Field $waitCountdown "additionalTooltipLines")
$waitInitMessage = Require-Method $waitCountdown "InitMessage" @()
Require-InstructionText $waitInitMessage "WarningMessageData::.ctor" "timeframe warning construction path changed"
$countdownHelper = Require-Type "NSMedieval.GameEventSystem.Events.CountdownWithWarningMessage"
[void](Require-Property $countdownHelper "CountdownMessage")
[void](Require-Field $countdownHelper "additionalTooltipLines")
$countdownInitMessage = Require-Method $countdownHelper "InitMessage" @()
Require-InstructionText $countdownInitMessage "WarningMessageData::.ctor" "negotiation countdown warning construction path changed"
$negotiationPhase = Require-Type "NSMedieval.GameEventSystem.Events.NegotiationPhase"
[void](Require-Field $negotiationPhase "countdown")
$assetUtils = Require-Type "NSMedieval.UI.Utils.AssetUtils"
[void](Require-Method $assetUtils "GetSprite" @("System.String"))

$blackBar = Require-Type "NSMedieval.BlackBarMessageController"
[void](Require-Method $blackBar "ShowBlackBarMessage" @("System.String"))
[void](Require-Method $blackBar "ShowClickableBlackBarMessage" @("System.String", "UnityEngine.Vector3"))
[void](Require-Method $blackBar "ShowClickableBlackBarMessage" @("System.String", "NSMedieval.View.SelectableObject", "System.Boolean"))
if ($eventReplicationSource -notmatch 'replicationEventNoticeHooksReady = count == 6') {
    throw "Event notice hook readiness contract is missing."
}

$weatherManager = Require-Type "NSMedieval.Manager.WeatherManager"
[void](Require-Field $weatherManager "scheduledEvents")
[void](Require-Field $weatherManager "weatherEvents")
[void](Require-Field $weatherManager "soilTemperature")
[void](Require-Field $weatherManager "waterTemperature")
[void](Require-Method $weatherManager "AddEventsForSeason" @("NSMedieval.Season"))
$forceWeather = Require-Method $weatherManager "ForceStartEvent" @("System.String", "System.Int64", "System.Int64", "System.Boolean")
if ($forceWeather.Parameters[2].Name -ne "duration") { throw "WeatherManager.ForceStartEvent third parameter is no longer duration." }
[void](Require-Method $weatherManager "OnStartEvent" @("NSMedieval.Weather.WeatherEventInstance"))
[void](Require-Method $weatherManager "OnEndEvent" @("NSMedieval.Weather.WeatherEventInstance"))
[void](Require-Method $weatherManager "OnGameLoaded" @("System.Boolean"))

$weatherInstance = Require-Type "NSMedieval.Weather.WeatherEventInstance"
[void](Require-Method $weatherInstance "RunEffectors" @("System.Boolean"))
[void](Require-Method $weatherInstance "Destroy" @())
[void](Require-Method $weatherInstance "SetKeywordsOnStart" @())
[void](Require-Field $weatherInstance "firstTick")
[void](Require-Property $weatherInstance "Blueprint")
[void](Require-Property $weatherInstance "StartTime")
[void](Require-Property $weatherInstance "EndTime")
$weatherConstructor = $weatherInstance.Methods | Where-Object {
    $_.IsConstructor -and $_.Parameters.Count -eq 3 -and $_.Parameters[0].ParameterType.FullName -eq "NSMedieval.Weather.WeatherEvent" -and $_.Parameters[1].ParameterType.FullName -eq "System.UInt32" -and $_.Parameters[2].ParameterType.FullName -eq "System.UInt32"
} | Select-Object -First 1
if ($null -eq $weatherConstructor -or -not $weatherConstructor.IsPublic) { throw "WeatherEventInstance public schedule constructor changed." }

$weatherOverrides = Require-Type "NSMedieval.Manager.WeatherOverrides"
$isActive = Require-Property $weatherOverrides "IsActive"
if ($null -eq $isActive.SetMethod -or -not $isActive.SetMethod.IsPublic) { throw "WeatherOverrides.IsActive is no longer publicly writable." }
[void](Require-Method $weatherOverrides "SetTemperature" @("System.Single"))
[void](Require-Method $weatherOverrides "SetSunStrengthMultiplier" @("System.Single"))
[void](Require-Method $weatherOverrides "SetRainEffectWeight" @("System.Single"))
[void](Require-Method $weatherOverrides "GetTemperature" @("System.Single"))
[void](Require-Method $weatherOverrides "GetSunStrengthMultiplier" @())
[void](Require-Method $weatherOverrides "GetRainEffectWeight" @("System.Single"))

$gameSpeedManager = Require-Type "NSMedieval.Manager.GameSpeedManager"
$currentSpeedIndex = Require-Property $gameSpeedManager "CurrentSpeedIndex"
if ($currentSpeedIndex.PropertyType.FullName -ne "NSMedieval.Manager.GameSpeedIndex") { throw "GameSpeedManager.CurrentSpeedIndex type changed." }
if ($null -eq $currentSpeedIndex.SetMethod) { throw "GameSpeedManager.CurrentSpeedIndex setter is missing." }
[void](Require-Method $gameSpeedManager "ProcessSpeedChange" @("NSMedieval.Manager.GameSpeedIndex"))

# Adjacent persistent event system is deliberately gated/deferred, but its
# installed boundary remains part of the researched compatibility contract.
$playerTriggeredManager = Require-Type "NSMedieval.PlayerTriggeredEventSystem.PlayerTriggeredEventManager"
[void](Require-Method $playerTriggeredManager "PrepareEvent" @("System.String", "NSMedieval.BuildingComponents.BaseBuildingInstance"))
[void](Require-Method $playerTriggeredManager "RunEvent" @())
[void](Require-Method $playerTriggeredManager "EndEvent" @())
[void](Require-Type "NSMedieval.PlayerTriggeredEventSystem.PlayerTriggeredEventSaveData")
foreach ($typeName in @(
    "FeastEventInstance",
    "HangingEventInstance",
    "MasterClassEventInstance",
    "RitualEventInstance",
    "SermonEventInstance",
    "TrainingEventInstance")) {
    [void](Require-Type ("NSMedieval.PlayerTriggeredEventSystem." + $typeName))
}

Write-Host "PASS EventGameSurfaces scheduler/events/dialog/warning/notice/weather/player-triggered native contracts"
$assembly.Dispose()

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $repositoryRoot
$cecilPath = Join-Path $gameRoot "BepInEx\core\Mono.Cecil.dll"
$gameAssemblyPath = Join-Path $gameRoot "Going Medieval_Data\Managed\Assembly-CSharp.dll"

if (-not (Test-Path -LiteralPath $cecilPath -PathType Leaf)) {
    throw "Mono.Cecil is missing at $cecilPath."
}
if (-not (Test-Path -LiteralPath $gameAssemblyPath -PathType Leaf)) {
    throw "Assembly-CSharp is missing at $gameAssemblyPath."
}

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecilPath))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAssemblyPath)

function Get-TypeTree {
    param([Parameter(Mandatory)] $Type)

    $Type
    foreach ($nested in $Type.NestedTypes) {
        Get-TypeTree -Type $nested
    }
}

function Get-GoalInstructionText {
    param([Parameter(Mandatory)][string] $GoalName)

    $root = $assembly.MainModule.Types | Where-Object { $_.Name -eq $GoalName } | Select-Object -First 1
    if ($null -eq $root) {
        throw "Semantic work game surface missing goal type $GoalName."
    }

    $parts = [Collections.Generic.List[string]]::new()
    foreach ($type in @(Get-TypeTree -Type $root)) {
        foreach ($method in $type.Methods) {
            if (-not $method.HasBody) {
                continue
            }

            foreach ($instruction in $method.Body.Instructions) {
                $parts.Add($instruction.OpCode.Name + " " + [string]$instruction.Operand)
            }
        }
    }

    return [string]::Join("`n", $parts)
}

$surfaces = @(
    @{ Goal = "ChopTreeGoal"; Action = "StartObtaining"; Trigger = "Mining" },
    @{ Goal = "ConstructBuildingGoal"; Action = "ConstructAction"; Trigger = "Build" },
    @{ Goal = "DigGoal"; Action = "StartObtaining"; Trigger = "Mining" },
    @{ Goal = "HarvestGoal"; Action = "StartObtaining"; Trigger = "Harvest" },
    @{ Goal = "FollowCutPlantOrderGoal"; Action = "EnemyCutPlant"; Trigger = "Mining" },
    @{ Goal = "DeconstructGoal"; Action = "DeconstructAction"; Trigger = "Build" },
    @{ Goal = "RepairBuildingGoal"; Action = "RepairAction"; Trigger = "Build" },
    @{ Goal = "UninstallBuildingGoal"; Action = "UninstallAction"; Trigger = "Build" },
    @{ Goal = "PlantCropsGoal"; Action = "PlantCropsAction"; Trigger = "Planting" }
)

foreach ($surface in $surfaces) {
    $instructions = Get-GoalInstructionText -GoalName $surface.Goal
    if (-not $instructions.Contains($surface.Action)) {
        throw "Semantic work game surface $($surface.Goal) no longer exposes action $($surface.Action)."
    }
    if (-not $instructions.Contains("ldstr " + $surface.Trigger)) {
        throw "Semantic work game surface $($surface.Goal) no longer triggers $($surface.Trigger)."
    }
    if (-not $instructions.Contains("ActionAnimationExtension::TriggerAnimation")) {
        throw "Semantic work game surface $($surface.Goal) no longer uses TriggerAnimation."
    }

    Write-Host "PASS SemanticWorkGameSurface $($surface.Goal)/$($surface.Action)/$($surface.Trigger)"
}

$animalOrderType = $assembly.MainModule.Types | Where-Object { $_.FullName -eq "NSMedieval.Types.AnimalOrderType" } | Select-Object -First 1
$animalController = $assembly.MainModule.Types | Where-Object { $_.FullName -eq "NSMedieval.Controllers.AnimalController" } | Select-Object -First 1
if ($null -eq $animalOrderType -or $null -eq $animalController) {
    throw "Animal-order game surfaces are missing."
}
$animalOrderValues = @{}
foreach ($field in $animalOrderType.Fields) {
    if ($null -ne $field.Constant) { $animalOrderValues[$field.Name] = [int]$field.Constant }
}
foreach ($expected in @(@{ Name = "Tame"; Value = 2 }, @{ Name = "Slaughter"; Value = 4 })) {
    if (-not $animalOrderValues.ContainsKey($expected.Name) -or $animalOrderValues[$expected.Name] -ne $expected.Value) {
        throw "Animal order $($expected.Name) no longer has expected native value $($expected.Value)."
    }
}
if ($null -eq ($animalController.Methods | Where-Object {
    $_.Name -eq "MarkForOrder" -and $_.Parameters.Count -eq 2 -and $_.Parameters[0].ParameterType.FullName -eq "NSMedieval.Types.AnimalOrderType" -and $_.Parameters[1].ParameterType.FullName -eq "NSMedieval.State.AnimalInstance"
} | Select-Object -First 1)) {
    throw "AnimalController.MarkForOrder(AnimalOrderType, AnimalInstance) is missing."
}
$animalInstance = $assembly.MainModule.Types | Where-Object { $_.FullName -eq "NSMedieval.State.AnimalInstance" } | Select-Object -First 1
foreach ($methodName in @("TrainingAttemptCompleted", "SetLastTrainingAttemptInfo", "SetAnimalType", "ResetPetOwner", "SetOrder")) {
    if ($null -eq ($animalInstance.Methods | Where-Object { $_.Name -eq $methodName } | Select-Object -First 1)) {
        throw "AnimalInstance.$methodName is missing."
    }
}
$managementSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationManagement.cs") -Raw
foreach ($requiredSource in @('string.Equals(order, "Tame", StringComparison.Ordinal) && value == 2', 'string.Equals(order, "Slaughter", StringComparison.Ordinal) && value == 4')) {
    if (-not $managementSource.Contains($requiredSource)) {
        throw "Animal-order replication source is missing $requiredSource."
    }
}
foreach ($requiredSource in @('AnimalStateDeltaKind = "AnimalState"', '"TrainingAttemptCompleted"', '"SetLastTrainingAttemptInfo"', '"SetAnimalType"', '"MaxDailyTrainingAttempts"', '"AnimalUntrained"', '"trainingCurrent"', '"FurColor"', '"furTextureB64"', '"SetMaterialBasedOnType"', 'QueueHostReplicationAnimalAppearanceSnapshotIfDue', '"OrderGivenFromAnimalUI"', 'UpdateReplicationAnimalState', 'TryApplyReplicationAnimalStateDelta', 'TryDecodeReplicationOptionalDetailBase64', '"animalType"')) {
    if (-not $managementSource.Contains($requiredSource)) {
        throw "Animal-state replication source is missing $requiredSource."
    }
}
$worldDeltaSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx\Replication\ReplicationWorldObjectDeltas.cs") -Raw
if (-not $worldDeltaSource.Contains('case "ReleaseAnimalGoal":') -or -not $worldDeltaSource.Contains('return "Roping";')) {
    throw "ReleaseAnimalGoal must explicitly drive the client rope presentation."
}
Write-Host "PASS AnimalOrderGameSurface Tame=2 Slaughter=4"

$assembly.Dispose()

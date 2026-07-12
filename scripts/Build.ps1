[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory)]
    [string]$GameRoot,

    [string]$BepInExRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gameRootPath = (Resolve-Path -LiteralPath $GameRoot).Path
if ([string]::IsNullOrWhiteSpace($BepInExRoot)) {
    $BepInExRoot = Join-Path $gameRootPath "BepInEx\core"
}
$bepInExRootPath = (Resolve-Path -LiteralPath $BepInExRoot).Path
$managed = Join-Path $gameRootPath "Going Medieval_Data\Managed"

$references = @(
    (Join-Path $bepInExRootPath "BepInEx.dll"),
    (Join-Path $bepInExRootPath "0Harmony.dll"),
    (Join-Path $managed "mscorlib.dll"),
    (Join-Path $managed "System.dll"),
    (Join-Path $managed "System.Core.dll"),
    (Join-Path $managed "netstandard.dll"),
    (Join-Path $managed "UnityEngine.dll"),
    (Join-Path $managed "UnityEngine.CoreModule.dll"),
    (Join-Path $managed "UnityEngine.AnimationModule.dll"),
    (Join-Path $managed "UnityEngine.PhysicsModule.dll"),
    (Join-Path $managed "UnityEngine.InputLegacyModule.dll"),
    (Join-Path $managed "UnityEngine.IMGUIModule.dll"),
    (Join-Path $managed "UnityEngine.TextRenderingModule.dll"),
    (Join-Path $managed "UnityEngine.UIModule.dll"),
    (Join-Path $managed "UnityEngine.UI.dll"),
    (Join-Path $managed "Unity.TextMeshPro.dll"),
    (Join-Path $managed "Assembly-CSharp.dll")
)

$missing = @($references | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "Required build references are missing:`n$($missing -join "`n")"
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnet
$compiler = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot "sdk") -Recurse -Filter "csc.dll" |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $compiler) {
    throw "Could not locate the Roslyn C# compiler under $dotnetRoot."
}

$sources = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Core") -Recurse -Filter "*.cs" -File
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src\GoingCooperative.Plugin.BepInEx") -Recurse -Filter "*.cs" -File
) | Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" } | Select-Object -ExpandProperty FullName

$outputDirectory = Join-Path $repositoryRoot "artifacts\bin\$Configuration"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory "GoingCooperative.dll"
$arguments = @(
    "-noconfig",
    "-nostdlib+",
    "-target:library",
    "-langversion:10.0",
    "-nullable:enable",
    "-deterministic+",
    "-out:$output"
)
if ($Configuration -eq "Release") {
    $arguments += "-optimize+"
}
foreach ($reference in $references) {
    $arguments += "-r:$reference"
}
$arguments += $sources

& $dotnet $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Going Cooperative compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Built $output"

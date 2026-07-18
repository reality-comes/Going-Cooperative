[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnet
$compiler = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot "sdk") -Recurse -Filter "csc.dll" |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$managed = Join-Path (Split-Path -Parent $repositoryRoot) "Going Medieval_Data\Managed"
$outputDirectory = Join-Path $repositoryRoot "artifacts\tests"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory "EventCheckpointTrackerTests.exe"
$sources = @(
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\EventAuthorityPolicy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\TraderSerializerCompatibilityPolicy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\EventCheckpointTracker.cs"),
    (Join-Path $repositoryRoot "tests\EventCheckpointTrackerTests.cs")
)

& $dotnet $compiler -noconfig -nostdlib+ -target:exe -langversion:10.0 -nullable:enable "-out:$output" `
    "-r:$(Join-Path $managed 'mscorlib.dll')" `
    "-r:$(Join-Path $managed 'System.dll')" `
    "-r:$(Join-Path $managed 'System.Core.dll')" `
    "-r:$(Join-Path $managed 'netstandard.dll')" @sources
if ($LASTEXITCODE -ne 0) { throw "Event checkpoint tracker test compilation failed." }
& $output
if ($LASTEXITCODE -ne 0) { throw "Event checkpoint tracker tests failed." }

$traderTransferOutput = Join-Path $outputDirectory "TraderPartyTransferTrackerTests.exe"
$traderTransferSources = @(
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\TraderPartyTransferTracker.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\TraderPartyRuntimePolicy.cs"),
    (Join-Path $repositoryRoot "tests\TraderPartyTransferTrackerTests.cs")
)

& $dotnet $compiler -noconfig -nostdlib+ -target:exe -langversion:10.0 -nullable:enable "-out:$traderTransferOutput" `
    "-r:$(Join-Path $managed 'mscorlib.dll')" `
    "-r:$(Join-Path $managed 'System.dll')" `
    "-r:$(Join-Path $managed 'System.Core.dll')" `
    "-r:$(Join-Path $managed 'System.Security.dll')" `
    "-r:$(Join-Path $managed 'netstandard.dll')" @traderTransferSources
if ($LASTEXITCODE -ne 0) { throw "Trader party transfer tracker test compilation failed." }
& $traderTransferOutput
if ($LASTEXITCODE -ne 0) { throw "Trader party transfer tracker tests failed." }

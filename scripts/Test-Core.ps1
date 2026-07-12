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
$output = Join-Path $outputDirectory "CorePolicyTests.exe"
$sources = @(
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\CoordinateResolverPolicy.cs"),
    (Join-Path $repositoryRoot "src\GoingCooperative.Core\LockstepCommandPayloads.cs"),
    (Join-Path $repositoryRoot "tests\CorePolicyTests.cs")
)

& $dotnet $compiler -noconfig -nostdlib+ -target:exe -langversion:10.0 "-out:$output" `
    "-r:$(Join-Path $managed 'mscorlib.dll')" `
    "-r:$(Join-Path $managed 'System.dll')" `
    "-r:$(Join-Path $managed 'System.Core.dll')" `
    "-r:$(Join-Path $managed 'netstandard.dll')" @sources
if ($LASTEXITCODE -ne 0) { throw "Core policy test compilation failed." }
& $output
if ($LASTEXITCODE -ne 0) { throw "Core policy tests failed." }

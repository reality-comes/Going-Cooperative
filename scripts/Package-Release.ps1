[CmdletBinding()]
param(
    [string]$Version = "0.2.0",

    [Parameter(Mandatory)]
    [string]$GameRoot,

    [string]$BepInExRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "Build.ps1") -Configuration Release -GameRoot $GameRoot -BepInExRoot $BepInExRoot

$artifactRoot = Join-Path $repositoryRoot "artifacts"
$packageName = "Going-Cooperative-v$Version"
$stage = Join-Path $artifactRoot $packageName
$zip = Join-Path $artifactRoot "$packageName.zip"
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

$pluginDirectory = Join-Path $stage "BepInEx\plugins\GoingCooperative"
$configDirectory = Join-Path $stage "GoingCooperative"
New-Item -ItemType Directory -Force -Path $pluginDirectory, $configDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $artifactRoot "bin\Release\GoingCooperative.dll") -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "config\replication.cfg") -Destination $configDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Destination $stage
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $stage
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $stage

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
Set-Content -LiteralPath "$zip.sha256" -Value "$($hash.Hash.ToLowerInvariant())  $packageName.zip" -Encoding ASCII

Write-Host "Packaged $zip"
Write-Host "SHA-256 $($hash.Hash.ToLowerInvariant())"

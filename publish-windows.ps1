[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'out/windows')
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 8 SDK is required to build from source.' }
& dotnet publish (Join-Path $PSScriptRoot 'discord-indexer.csproj') -c Release -r $Runtime -o $OutputDirectory --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:UseAppHost=true' '-p:PublishTrimmed=false' '-p:RestoreSources=https://api.nuget.org/v3/index.json'
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
foreach ($name in @('discord-indexer-search.ps1', 'discord-indexer-delta.ps1', 'discord-indexer-query.ps1', 'install-windows.ps1', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $OutputDirectory -Force
}
Write-Host "Portable Windows build: $OutputDirectory"

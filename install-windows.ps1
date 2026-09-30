[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'discord-indexer'),
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $source 'discord-indexer.exe'))) {
    # Source checkout: publish locally. Release packages already contain the executable.
    & (Join-Path $source 'publish-windows.ps1') -Runtime $Runtime
    $source = Join-Path $source 'out/windows'
}
$source = [IO.Path]::GetFullPath($source)
$destination = [IO.Path]::GetFullPath($InstallDirectory)
if ($source.TrimEnd('\', '/') -ieq $destination.TrimEnd('\', '/')) { throw 'InstallDirectory must differ from the source package directory.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
# Copy only packaged program files; never import configuration, tokens or databases.
foreach ($name in @('discord-indexer.exe', 'discord-indexer-search.ps1', 'discord-indexer-delta.ps1', 'discord-indexer-query.ps1', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $destination -Force
}
# Preserve any companion native/runtime libraries in a package built without bundling.
Get-ChildItem -LiteralPath $source -Filter '*.dll' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}
Write-Host "Installed portable files to $destination"
Write-Host 'Configure MongoDB and process environment variables before launching discord-indexer.exe manually.'
Write-Host 'No credentials imported, MongoDB provisioned, PATH changed, process started or startup service registered.'

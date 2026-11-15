# Offline launcher smoke test. Never starts Discord or MongoDB.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$launcher = Join-Path $root 'start-discord-indexer-hidden.vbs'
$temp = Join-Path ([IO.Path]::GetTempPath()) ("indexer launcher ' " + [guid]::NewGuid())
$originalExitCode = $global:LASTEXITCODE
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $probe = Join-Path $temp 'probe script.ps1'
    $marker = Join-Path $temp 'completed.txt'
    '[IO.File]::WriteAllText((Join-Path $PSScriptRoot ''completed.txt''), ''completed''); exit 7' | Set-Content -LiteralPath $probe
    & "$env:SystemRoot\System32\cscript.exe" //B //Nologo $launcher $probe
    if ($LASTEXITCODE -ne 7) { throw "Expected child exit 7, received $LASTEXITCODE" }
    if ((Get-Content -LiteralPath $marker -Raw) -ne 'completed') { throw 'Launcher did not wait for the child.' }
    & "$env:SystemRoot\System32\cscript.exe" //B //Nologo $launcher (Join-Path $temp 'missing.ps1')
    if ($LASTEXITCODE -ne 2) { throw 'Missing wrapper must fail.' }
    & "$env:SystemRoot\System32\cscript.exe" //B //Nologo $launcher $marker
    if ($LASTEXITCODE -ne 2) { throw 'Non-PowerShell wrapper must fail.' }
    if ((Get-Content -LiteralPath $launcher -Raw) -notmatch 'shell.Run\(command, 0, True\)') { throw 'Child must start hidden and be awaited.' }
    foreach ($script in @('publish-windows.ps1','install-windows.ps1')) {
        if ((Get-Content (Join-Path $root $script) -Raw) -notmatch 'start-discord-indexer-hidden.vbs') { throw "$script omits launcher." }
    }
    Write-Host 'Hidden launcher: spaced/quoted paths, wait, exit code, invalid inputs and packaging passed.'
} finally {
    Get-ChildItem -LiteralPath $temp -File | Remove-Item
    Remove-Item -LiteralPath $temp
    $global:LASTEXITCODE = $originalExitCode
}

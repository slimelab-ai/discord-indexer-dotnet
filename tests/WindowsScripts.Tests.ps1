# Offline checks: no Discord, MongoDB, credentials, startup tasks or services.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
foreach ($script in Get-ChildItem -LiteralPath $repository -Filter '*.ps1') {
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw "Parse errors in $($script.Name): $parseErrors" }
}
$global:indexerTestQueries = @()
$global:indexerTestTemporaryFiles = @()
function mongosh {
    $index = [Array]::IndexOf($args, '--file')
    if ($index -lt 0) { throw 'Query must use a file, not inline shell evaluation.' }
    $file = $args[$index + 1]
    $global:indexerTestTemporaryFiles += $file
    $source = [IO.File]::ReadAllText($file)
    if ($source -notmatch 'Buffer.from\("([A-Za-z0-9+/=]+)", "base64"\)') { throw 'Missing safely encoded query parameters.' }
    $global:indexerTestQueries += ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1])) | ConvertFrom-Json)
    $global:LASTEXITCODE = 0
}
$originalDatabase = $env:MONGODB_DB
$originalExitCode = $global:LASTEXITCODE
try {
    $env:MONGODB_DB = 'offline-quoted-"-database'
    $hostileText = 'quote"; db.dropDatabase(); //\line' + "`n" + 'next line'
    & (Join-Path $repository 'discord-indexer-search.ps1') $hostileText -Channel 'channel"quoted' -CaseSensitive -Format jsonl -Limit 3
    & (Join-Path $repository 'discord-indexer-delta.ps1') -Since '1775692800000' -Guild 'guild"quoted' -Format jsonl -Limit 4
    if ($global:indexerTestQueries.Count -ne 2) { throw 'Expected two read-only queries.' }
    $search = $global:indexerTestQueries[0]
    if ($search.text -cne $hostileText -or $search.database -cne $env:MONGODB_DB -or $search.channel -cne 'channel"quoted' -or -not $search.caseSensitive -or $search.limit -ne 3) { throw 'Search parameters failed round-trip.' }
    $delta = $global:indexerTestQueries[1]
    if ($delta.since -ne '1775692800000' -or $delta.guild -cne 'guild"quoted' -or $delta.limit -ne 4 -or $delta.format -ne 'jsonl') { throw 'Delta parameters failed round-trip.' }
    foreach ($file in $global:indexerTestTemporaryFiles) {
        if (Test-Path -LiteralPath $file) { throw 'Query temporary file was not removed.' }
    }
    function mongosh {
        $index = [Array]::IndexOf($args, '--file')
        $global:indexerTestTemporaryFiles += $args[$index + 1]
        $global:LASTEXITCODE = 7
    }
    $failed = $false
    try { & (Join-Path $repository 'discord-indexer-search.ps1') 'test' } catch { $failed = $true }
    if (-not $failed) { throw 'Nonzero mongosh exit was ignored.' }
    foreach ($file in $global:indexerTestTemporaryFiles) {
        if (Test-Path -LiteralPath $file) { throw 'Query failure left a temporary file behind.' }
    }
    Write-Host 'Windows script parsing, parameter escaping, query cleanup and failure propagation passed.'
} finally {
    $env:MONGODB_DB = $originalDatabase
    # The mock's deliberate failure must not leak into GitHub Actions' pwsh wrapper.
    $global:LASTEXITCODE = $originalExitCode
    Remove-Item Function:mongosh -ErrorAction SilentlyContinue
    Remove-Variable indexerTestQueries, indexerTestTemporaryFiles -Scope Global -ErrorAction SilentlyContinue
}

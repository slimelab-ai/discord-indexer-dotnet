[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Since,
    [ValidateRange(1, 10000)][int]$Limit = 200,
    [string]$Guild = '',
    [string]$Channel = '',
    [ValidateSet('tsv', 'jsonl')][string]$Format = 'tsv'
)
. (Join-Path $PSScriptRoot 'discord-indexer-query.ps1')
Invoke-IndexerQuery -Parameters @{ mode = 'delta'; since = $Since; limit = $Limit; guild = $Guild; channel = $Channel; format = $Format }

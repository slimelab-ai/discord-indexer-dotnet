[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Text,
    [ValidateRange(1, 10000)][int]$Limit = 20,
    [string]$Guild = '',
    [string]$Channel = '',
    [switch]$CaseSensitive,
    [ValidateSet('tsv', 'jsonl')][string]$Format = 'tsv'
)
. (Join-Path $PSScriptRoot 'discord-indexer-query.ps1')
Invoke-IndexerQuery -Parameters @{ mode = 'search'; text = $Text; limit = $Limit; guild = $Guild; channel = $Channel; caseSensitive = [bool]$CaseSensitive; format = $Format }

# Shared native query adapter. Parameters are JSON data, never interpolated JavaScript.
function Invoke-IndexerQuery {
    param([Parameter(Mandatory)][hashtable]$Parameters)
    $ErrorActionPreference = 'Stop'
    if (-not (Get-Command mongosh -ErrorAction SilentlyContinue)) { throw 'Install MongoDB Shell (mongosh) and add it to PATH to use the Windows query helpers.' }
    $uri = if ($env:MONGODB_URI) { $env:MONGODB_URI } else { 'mongodb://127.0.0.1:27017' }
    $Parameters.database = if ($env:MONGODB_DB) { $env:MONGODB_DB } else { 'discord_index' }
    $json = ConvertTo-Json -InputObject $Parameters -Compress
    # Encode JSON to keep quotes, newlines and shell characters entirely out of source code.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    $javascript = 'const p = JSON.parse(Buffer.from("' + $encoded + '", "base64").toString("utf8"));' + "`n" + @'
const database = db.getSiblingDB(p.database);
const filter = {};
if (p.guild) filter.guild_id = p.guild;
if (p.channel) filter.channel_id = p.channel;
if (p.mode === 'search') {
  filter['raw.content'] = { $regex: p.text, $options: p.caseSensitive ? '' : 'i' };
} else {
  const since = /^\d{11,}$/.test(p.since) ? Number(p.since) : Date.parse(p.since);
  if (!Number.isFinite(since)) throw new Error('Invalid -Since timestamp');
  filter.timestamp_ms = { $gte: since };
}
const channelMap = new Map();
const channelFilter = {};
if (p.guild) channelFilter.guild_id = p.guild;
if (p.channel) channelFilter.channel_id = p.channel;
database.channels.find(channelFilter, {channel_id: 1, name: 1}).forEach(c => channelMap.set(c.channel_id, c.name || ''));
const projection = {message_id: 1, guild_id: 1, channel_id: 1, timestamp: 1, timestamp_ms: 1,
  attachments: 1, attachment_count: 1, has_attachments: 1, 'raw.author': 1, 'raw.content': 1};
const sort = p.mode === 'search' ? {timestamp_ms: -1, _id: -1} : {timestamp_ms: 1, _id: 1};
database.messages.find(filter, projection).sort(sort).limit(p.limit).forEach(d => {
  const author = d.raw && d.raw.author || {};
  const attachments = Array.isArray(d.attachments) ? d.attachments : [];
  const row = {timestamp: d.timestamp || '', timestamp_ms: d.timestamp_ms || 0,
    message_id: d.message_id || '', guild_id: d.guild_id || '', channel_id: d.channel_id || '',
    channel_name: channelMap.get(d.channel_id) || '', author: author.global_name || author.username || '',
    content: d.raw && d.raw.content || '', attachments,
    attachment_count: typeof d.attachment_count === 'number' ? d.attachment_count : attachments.length,
    has_attachments: d.has_attachments === true || attachments.length > 0};
  if (p.format === 'jsonl') print(JSON.stringify(row));
  else print([row.timestamp, row.guild_id, row.channel_id, row.channel_name, row.author,
    row.content, JSON.stringify(row.attachments)].join('\t'));
});
'@
    $temporaryFile = Join-Path ([IO.Path]::GetTempPath()) ('discord-indexer-query-' + [Guid]::NewGuid().ToString('N') + '.js')
    try {
        [IO.File]::WriteAllText($temporaryFile, $javascript, (New-Object Text.UTF8Encoding($false)))
        & mongosh $uri --quiet --file $temporaryFile
        if ($LASTEXITCODE -ne 0) { throw "mongosh query failed ($LASTEXITCODE)." }
    } finally {
        if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
}

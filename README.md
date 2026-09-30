# discord-indexer-dotnet

Discord → MongoDB indexer with backfill + rate limit coordination.

## One-line install (Linux)

Installs the **latest GitHub Release** (`discord-indexer`, `discord-indexer-search`, and `discord-indexer-delta`) to `/usr/local/bin`.

```bash
curl -fsSL https://raw.githubusercontent.com/slimelab-ai/discord-indexer-dotnet/master/install.sh | sudo bash
```

## Native Windows (PowerShell)

Windows does not need WSL, Bash, administrator access or a service. Build from a reviewed checkout with the **.NET 8 SDK**:

```powershell
.\publish-windows.ps1
# Optional: copy the portable files to %LOCALAPPDATA%\discord-indexer
.\install-windows.ps1
```

`publish-windows.ps1` restores dependencies from official NuGet and produces a self-contained, single-file Windows executable in `out/windows`. Use `-Runtime win-arm64` for ARM64; the default is `win-x64`. A release ZIP already contains the executable, so running its `install-windows.ps1` requires no SDK. Extract the ZIP first. `-InstallDirectory` chooses the destination. Neither script reads OpenClaw configurations, imports credentials, installs MongoDB, starts the indexer, changes PATH or creates a startup task/service.

Before launching, provision MongoDB separately and set `MONGODB_URI`, `MONGODB_DB`, and `DISCORD_GUILD_IDS` for the intended database and server(s). Enter the bot token through a hidden prompt in the same PowerShell session:

```powershell
$env:MONGODB_URI = 'mongodb://127.0.0.1:27017'
$env:MONGODB_DB = 'discord_index'
$env:DISCORD_GUILD_IDS = '<guild-id>'
$token = Read-Host 'Discord bot token' -AsSecureString
$env:DISCORD_BOT_TOKEN = [System.Net.NetworkCredential]::new('', $token).Password
try {
    & "$env:LOCALAPPDATA\discord-indexer\discord-indexer.exe"
} finally {
    Remove-Item Env:DISCORD_BOT_TOKEN -ErrorAction SilentlyContinue
    $token.Dispose()
}
```

The token exists in the process environment while the indexer runs; this example does not write it to disk. The default Gateway intents include the bot's DMs. Discord channel permissions determine accessible guild history; enable Message Content for the bot. History has no age cutoff or automatic retention expiration.

Install **MongoDB Shell (`mongosh`)** separately and add it to PATH for native read-only query helpers. They use the same Mongo environment variables, require no Discord token, and do not fall back to Docker:

```powershell
& "$env:LOCALAPPDATA\discord-indexer\discord-indexer-search.ps1" 'error' -Channel '<channel-id>' -Limit 20
& "$env:LOCALAPPDATA\discord-indexer\discord-indexer-delta.ps1" -Since '2026-04-09T00:00:00Z' -Guild '<guild-id>' -Format jsonl
```

Search text is a Mongo regular expression, matching the Linux helper. Windows helpers accept `-Guild`, `-Channel`, `-Limit` (1–10000) and `-Format tsv|jsonl`; search also accepts `-CaseSensitive`. JSONL keeps message content and attachments unambiguous. The existing attachment migration helper remains Bash-only.

## OpenClaw skill install / replace

Safely installs or replaces the `discord-indexer` OpenClaw skill from this repo into `~/.openclaw/skills`:

```bash
bash -c 'set -euo pipefail; repo="patrick-slimelab/discord-indexer-dotnet"; skill="discord-indexer"; dest="$HOME/.openclaw/skills/$skill"; tmp="$(mktemp -d)"; backup=""; cleanup(){ rc=$?; rm -rf "$tmp"; if [[ $rc -eq 0 ]]; then [[ -n "$backup" && -d "$backup" ]] && rm -rf "$backup"; elif [[ -n "$backup" && -d "$backup" && ! -e "$dest" ]]; then mv "$backup" "$dest"; fi; exit $rc; }; trap cleanup EXIT; curl -fsSL "https://github.com/$repo/archive/refs/heads/master.tar.gz" | tar -xz -C "$tmp" --strip-components=2 "discord-indexer-dotnet-master/skill/$skill"; mkdir -p "$(dirname "$dest")"; if [[ -e "$dest" ]]; then backup="$dest.backup.$(date +%s)"; mv "$dest" "$backup"; fi; mv "$tmp/$skill" "$dest"'
```

### OpenClaw auto-token

If the installer detects an OpenClaw state dir at one of:

- `~/.openclaw`
- `~/.moltbot`
- `~/.clawdbot`

…it will parse the JSON config and, if present, use:

- `channels.discord.token`

…and write `/etc/discord-indexer/indexer.env` (mode `0600`).

## Releases

Releases include:
- `discord-indexer-linux-x64.tar.gz`
- `discord-indexer-linux-x64.sha256`
- `discord-indexer-win-x64.zip`
- `discord-indexer-win-x64.sha256`

## Helpers

- `discord-indexer-search <text> [--guild ...] [--channel ...] [--limit N]`
- `discord-indexer-delta --since <timestamp|epoch_ms> [--guild ...] [--channel ...] [--limit N] [--format tsv|jsonl]`
- `discord-indexer-migrate-attachments`

`discord-indexer-delta` is the server-wide delta retrieval helper: by default it returns indexed messages across all readable channels since the requested timestamp, optionally narrowed to one guild or one channel.

Messages store Discord file attachments in a top-level `attachments` array in MongoDB, alongside `attachment_count` and `has_attachments`. Search and delta helpers return the attachment array with message content so callers do not need to parse `raw.attachments`.

For existing databases created before attachment metadata was promoted, run:

```bash
discord-indexer-migrate-attachments
```

## Development Mongo

To avoid touching the live indexer database during development:

```bash
docker compose -f docker-compose.dev.yml up -d
export MONGODB_URI=mongodb://127.0.0.1:27019
export MONGODB_DB=discord_index_dev
```

## Thread history and bot DMs

Backfill discovers active guild threads and paginates public archived threads in
text, announcement, forum and media channels. For text channels it also reads
joined private archives; the wider private-archive endpoint is attempted using
existing bot permissions and skipped on 403/404. No extra `MANAGE_THREADS`
permission, thread joins, unarchiving or Discord writes are required. Inaccessible
threads remain inaccessible.

Thread names, parent IDs and archive metadata are stored in `channels`. Each thread
uses the same resumable message cursor and unique message-ID index as a normal
channel. Discovery runs in the background at startup and every 15 minutes
(`INDEXER_THREAD_DISCOVERY_INTERVAL_MINUTES`, minimum 1). Gateway thread-create,
update and list-sync events also seed threads. An interrupted discovery starts
again from the newest archive page; existing message cursors are preserved, so
replayed pages cannot skip backlog messages. Completed threads with a changed
last-message ID get a catch-up pass that stops at their previous head, including
messages missed while the indexer was offline. Rate limits are shared with message
backfill. An endpoint with a transient failure retries before the next discovery
pass; inaccessible message history is retried on later thread discovery.

Default intents continue to include the bot's own received DMs. They never give
access to another user's personal inbox. Historical DM channels are not enumerated
by this indexer, so old DM backlog is not promised. Discord permissions determine
readable scope. Backfill has no age cutoff and MongoDB has no automatic retention
expiry. Offline edits/deletions of old messages are not comprehensively reconciled;
the existing live-update/delete and bounded startup-reconciliation behavior remains.

API references: [active guild threads](https://docs.discord.com/developers/resources/guild#list-active-guild-threads)
and [archived thread pagination and permissions](https://docs.discord.com/developers/resources/channel#list-public-archived-threads).

## Streaming message updates

Live `MESSAGE_UPDATE` events are coalesced per message and written only after the
message has been quiet for 10 seconds. The settled message is fetched once from
Discord before MongoDB is updated. `MESSAGE_DELETE` removes the indexed message,
including abandoned streaming previews.

At startup, one bounded recovery pass refreshes up to 100 bot messages from the
last 15 minutes. These limits can be tuned with:

- `INDEXER_MESSAGE_UPDATE_DEBOUNCE_MS`
- `INDEXER_STARTUP_RECONCILE_LIMIT` (set to `0` to disable)
- `INDEXER_STARTUP_RECONCILE_WINDOW_MINUTES`

## Notes

- The installer does **not** print tokens.
- MongoDB connection is controlled by env vars (`MONGODB_URI`, `MONGODB_DB`).
- "All channels" means channels the bot was actually able to enumerate/read/index. Private or forbidden channels will not appear.

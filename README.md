# discord-indexer-dotnet

Discord → MongoDB indexer with backfill + rate limit coordination.

## One-line install (Linux)

Installs the **latest GitHub Release** (`discord-indexer`, `discord-indexer-search`, and `discord-indexer-delta`) to `/usr/local/bin`.

```bash
curl -fsSL https://raw.githubusercontent.com/patrick-slimelab/discord-indexer-dotnet/master/install.sh | sudo bash
```

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

## Helpers

- `discord-indexer-search <text> [--guild ...] [--channel ...] [--limit N]`
- `discord-indexer-delta --since <timestamp|epoch_ms> [--guild ...] [--channel ...] [--limit N] [--format tsv|jsonl]`

`discord-indexer-delta` is the server-wide delta retrieval helper: by default it returns indexed messages across all readable channels since the requested timestamp, optionally narrowed to one guild or one channel.

## Notes

- The installer does **not** print tokens.
- MongoDB connection is controlled by env vars (`MONGODB_URI`, `MONGODB_DB`).
- "All channels" means channels the bot was actually able to enumerate/read/index. Private or forbidden channels will not appear.

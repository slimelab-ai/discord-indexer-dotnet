#!/usr/bin/env bash
set -euo pipefail

# install.sh
#
# One-shot installer intended for: curl ... | sudo bash
#
# - Downloads latest GitHub Release asset (linux-x64)
# - Verifies sha256
# - Installs:
#     /usr/local/bin/discord-indexer
#     /usr/local/bin/discord-indexer-search
#     /usr/local/bin/discord-indexer-delta
# - If an OpenClaw config is found, reads channels.discord.token and writes
#   /etc/discord-indexer/indexer.env (0600) without printing the token.

REPO="${REPO:-patrick-slimelab/discord-indexer-dotnet}"
VERSION="${VERSION:-latest}" # latest or vX.Y.Z
PREFIX="${PREFIX:-/usr/local/bin}"

# Optional override when OpenClaw/Clawdbot config is in a non-standard location:
#   OPENCLAW_CONFIG_PATH=/path/to/openclaw.json
OPENCLAW_CONFIG_PATH="${OPENCLAW_CONFIG_PATH:-}"

ASSET_TGZ="discord-indexer-linux-x64.tar.gz"
ASSET_SHA="discord-indexer-linux-x64.sha256"

need() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "ERROR: missing required command: $1" >&2
    exit 1
  }
}

need curl
need tar
need sha256sum


# Ensure we can query the DB locally (used by discord-indexer-search / discord-indexer-delta)
install_mongosh_if_possible() {
  if command -v mongosh >/dev/null 2>&1; then return 0; fi

  # Try apt (without apt-get update; avoid breaking on bad third-party PPAs)
  if command -v apt-get >/dev/null 2>&1; then
    echo "[install] mongosh not found; attempting apt-get install mongosh" >&2
    export DEBIAN_FRONTEND=noninteractive
    apt-get install -y mongosh >/dev/null 2>&1 && command -v mongosh >/dev/null 2>&1 && return 0
    apt-get install -y mongodb-mongosh >/dev/null 2>&1 && command -v mongosh >/dev/null 2>&1 && return 0
  fi

  # Fallback: install from MongoDB tarball (no repo setup needed)
  local ver="2.3.8"
  local arch="linux-x64"
  local url="https://downloads.mongodb.com/compass/mongosh-${ver}-${arch}.tgz"
  echo "[install] Installing mongosh from tarball: $url" >&2
  local tmp
  tmp="$(mktemp -d)"
  (
    cd "$tmp"
    curl -fsSL -o mongosh.tgz "$url"
    tar -xzf mongosh.tgz
    install -m 0755 mongosh-*/bin/mongosh /usr/local/bin/mongosh
  )
  rm -rf "$tmp"

  command -v mongosh >/dev/null 2>&1
}

if [[ "$(id -u)" -ne 0 ]]; then
  echo "ERROR: run as root (use: curl ... | sudo bash)" >&2
  exit 1
fi

# Resolve the invoking user's home dir (important because we're usually root via sudo)
INVOKER="${SUDO_USER:-}"
if [[ -z "$INVOKER" ]]; then
  INVOKER="$(logname 2>/dev/null || true)"
fi
if [[ -z "$INVOKER" ]]; then
  INVOKER="root"
fi

INVOKER_HOME="$(getent passwd "$INVOKER" | cut -d: -f6)"
if [[ -z "$INVOKER_HOME" ]]; then
  INVOKER_HOME="/root"
fi

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

resolve_latest_tag() {
  curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" \
    | sed -n 's/.*"tag_name" *: *"\([^"]*\)".*/\1/p' \
    | head -n1
}

TAG="$VERSION"
if [[ "$VERSION" == "latest" ]]; then
  echo "[install] Resolving latest release for $REPO"
  TAG="$(resolve_latest_tag)"
  if [[ -z "$TAG" ]]; then
    echo "ERROR: could not resolve latest release tag" >&2
    exit 1
  fi
fi

BASE_URL="https://github.com/${REPO}/releases/download/${TAG}"

echo "[install] Downloading ${ASSET_TGZ} (${TAG})"
curl -fsSL -o "$TMP/$ASSET_TGZ" "$BASE_URL/$ASSET_TGZ"
curl -fsSL -o "$TMP/$ASSET_SHA" "$BASE_URL/$ASSET_SHA"

# Install mongosh (best-effort)
install_mongosh_if_possible || echo "[install] NOTE: failed to install mongosh automatically" >&2

# Optional: provision a local MongoDB via Docker (recommended)
# Set INSTALL_MONGO_DOCKER=0 to skip.
INSTALL_MONGO_DOCKER="${INSTALL_MONGO_DOCKER:-1}"

provision_mongo_docker_if_possible() {
  [[ "$INSTALL_MONGO_DOCKER" == "1" ]] || return 0
  command -v docker >/dev/null 2>&1 || return 0
  docker ps >/dev/null 2>&1 || return 0

  # If container already exists, do nothing
  if docker ps -a --format '{{.Names}}' | grep -qx 'discord-indexer-mongo'; then
    docker start discord-indexer-mongo >/dev/null 2>&1 || true
    return 0
  fi

  echo "[install] Provisioning MongoDB via Docker (discord-indexer-mongo on 127.0.0.1:27017)" >&2
  docker volume create discord_indexer_mongo >/dev/null
  docker run -d --name discord-indexer-mongo     -p 127.0.0.1:27017:27017     -v discord_indexer_mongo:/data/db     --restart unless-stopped     mongo:6 >/dev/null
}

provision_mongo_docker_if_possible || true

cd "$TMP"

echo "[install] Extracting"
tar -xzf "$ASSET_TGZ"

echo "[install] Verifying checksums"
sha256sum -c "$ASSET_SHA"

install -d "$PREFIX"
install -m 0755 "$TMP/discord-indexer" "$PREFIX/discord-indexer"
install -m 0755 "$TMP/discord-indexer-search" "$PREFIX/discord-indexer-search"
install -m 0755 "$TMP/discord-indexer-delta" "$PREFIX/discord-indexer-delta"

echo "[install] Installed binaries to $PREFIX"

# --- OpenClaw token detection ---
read_discord_token_from_openclaw() {
  # Try standard OpenClaw state dirs
  local home="$1"
  local candidates=(
    "$home/.openclaw/openclaw.json"
    "$home/.moltbot/openclaw.json"
    "$home/.clawdbot/openclaw.json"
  )

  for cfg in "${candidates[@]}"; do
    if [[ -f "$cfg" ]]; then
      if command -v jq >/dev/null 2>&1; then
        local tok
        tok="$(jq -r '.channels.discord.token // empty' "$cfg" 2>/dev/null || true)"
        if [[ -n "$tok" && "$tok" != "null" ]]; then
          echo "$tok"
          return 0
        fi
      fi
    fi
  done

  return 1
}

TOKEN=""
# --- OpenClaw token detection ---
# Prefer jq; otherwise try python3/python/node; if none are present, attempt to install jq (apt).

install_jq_if_possible() {
  if command -v jq >/dev/null 2>&1; then return 0; fi
  if command -v apt-get >/dev/null 2>&1; then
    echo "[install] jq not found; attempting apt-get install jq" >&2
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -y >/dev/null
    apt-get install -y jq >/dev/null
    command -v jq >/dev/null 2>&1 && return 0
  fi
  return 1
}

extract_token_with_python() {
  local cfg="$1"
  local py=""
  if command -v python3 >/dev/null 2>&1; then py="python3"; fi
  if [[ -z "$py" ]] && command -v python >/dev/null 2>&1; then py="python"; fi
  [[ -n "$py" ]] || return 1
  "$py" - <<PY 2>/dev/null || true
import json
p = r'''$cfg'''
with open(p,'r',encoding='utf-8') as f:
  data=json.load(f)
print((data.get('channels',{}) or {}).get('discord',{}).get('token','') or '')
PY
}

extract_token_with_node() {
  local cfg="$1"
  command -v node >/dev/null 2>&1 || return 1
  node -e 'const fs=require("fs"); const p=process.argv[1]; const d=JSON.parse(fs.readFileSync(p,"utf8")); const t=(((d.channels||{}).discord||{}).token)||""; process.stdout.write(t);' "$cfg" 2>/dev/null || true
}

TOKEN=""

# Ensure jq exists if we can easily install it (Debian/Ubuntu)
install_jq_if_possible || true

# Try candidates (OpenClaw + Clawdbot)
# NOTE: clawdbot installs often store config as clawdbot.json (not openclaw.json)
collect_cfg_candidates() {
  local home="$1"
  echo "$home/.openclaw/openclaw.json"
  echo "$home/.moltbot/openclaw.json"
  echo "$home/.clawdbot/openclaw.json"
  echo "$home/.clawdbot/clawdbot.json"
}

# Explicit override path (if provided)
if [[ -n "${OPENCLAW_CONFIG_PATH:-}" ]]; then
  CANDIDATES=("$OPENCLAW_CONFIG_PATH")
else
  CANDIDATES=()
fi

# Try invoking user's home first
CANDIDATES+=( $(collect_cfg_candidates "$INVOKER_HOME") )

# Also scan other /home/* users for clawdbot installs (common when running from a different shell user)
if [[ -d /home ]]; then
  while IFS= read -r d; do
    CANDIDATES+=("$d/.clawdbot/clawdbot.json")
    CANDIDATES+=("$d/.openclaw/openclaw.json")
  done < <(find /home -mindepth 1 -maxdepth 1 -type d 2>/dev/null || true)
fi

for cfg in "${CANDIDATES[@]}"; do
  if [[ -f "$cfg" ]]; then
    if command -v jq >/dev/null 2>&1; then
      TOKEN="$(jq -r '.channels.discord.token // empty' "$cfg" 2>/dev/null || true)"
    else
      TOKEN="$(extract_token_with_python "$cfg" || true)"
      if [[ -z "$TOKEN" ]]; then
        TOKEN="$(extract_token_with_node "$cfg" || true)"
      fi
    fi
    if [[ -n "$TOKEN" && "$TOKEN" != "null" ]]; then
      echo "[install] Detected Discord token from: $cfg" >&2
      break
    fi
  fi
done

if [[ -z "$TOKEN" ]]; then
  echo "[install] NOTE: could not auto-detect Discord token from OpenClaw/Clawdbot config." >&2

  # Interactive fallback: prompt for path or token
  if [[ -t 0 ]]; then
    echo "[install] If you have a Clawdbot/OpenClaw config elsewhere, enter its full path now." >&2
    echo "[install] Examples:" >&2
    echo "  /home/<user>/.clawdbot/clawdbot.json" >&2
    echo "  /home/<user>/.openclaw/openclaw.json" >&2
    read -r -p "Config path (or leave blank to paste token): " CFG_PATH_INPUT || true

    if [[ -n "${CFG_PATH_INPUT:-}" && -f "$CFG_PATH_INPUT" ]]; then
      if command -v jq >/dev/null 2>&1; then
        TOKEN="$(jq -r '.channels.discord.token // empty' "$CFG_PATH_INPUT" 2>/dev/null || true)"
      else
        TOKEN="$(extract_token_with_python "$CFG_PATH_INPUT" || true)"
        if [[ -z "$TOKEN" ]]; then TOKEN="$(extract_token_with_node "$CFG_PATH_INPUT" || true)"; fi
      fi
    fi

    if [[ -z "$TOKEN" ]]; then
      echo "[install] Paste Discord bot token (input hidden)." >&2
      read -r -s -p "DISCORD_BOT_TOKEN: " TOKEN || true
      echo >&2
    fi
  else
    echo "[install] Non-interactive shell: set DISCORD_BOT_TOKEN manually in /etc/discord-indexer/indexer.env" >&2
    echo "[install] Or rerun with: OPENCLAW_CONFIG_PATH=/path/to/openclaw.json curl ... | sudo bash" >&2
  fi
fi

# --- Write env file ---
ENV_DIR="/etc/discord-indexer"
ENV_FILE="$ENV_DIR/indexer.env"

install -d -m 0755 "$ENV_DIR"

umask 077

if [[ ! -f "$ENV_FILE" ]]; then
  {
    echo "# discord-indexer environment (generated by install.sh)"
    echo "MONGODB_URI=\"mongodb://127.0.0.1:27017\""
    echo "MONGODB_DB=\"discord_index\""
    echo "DISCORD_GUILD_IDS=\"\""
    echo "DISCORD_INTENTS=\"4609\""
    echo "INDEXER_BACKFILL_WORKERS=\"1\""
    echo "INDEXER_BACKFILL_REQUEST_DELAY_MS=\"250\""
    if [[ -n "$TOKEN" ]]; then
      echo "DISCORD_BOT_TOKEN=\"$TOKEN\""
      echo "# token source: OpenClaw/Clawdbot config (channels.discord.token)"
    else
      echo "# DISCORD_BOT_TOKEN not set."
      echo "# Add it here before running the indexer."
    fi
  } > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  echo "[install] Wrote $ENV_FILE (0600)"
else
  if [[ -n "$TOKEN" ]]; then
    if grep -q '^DISCORD_BOT_TOKEN=' "$ENV_FILE"; then
      echo "[install] Updating existing $ENV_FILE with detected DISCORD_BOT_TOKEN (no token printed)"
      # Replace the first matching line safely
      tmpfile="$(mktemp)"
      awk -v repl="DISCORD_BOT_TOKEN=\"$TOKEN\"" 'BEGIN{done=0} {if(!done && $0 ~ /^DISCORD_BOT_TOKEN=/){print repl; done=1} else {print}} END{if(!done){print repl}}' "$ENV_FILE" > "$tmpfile"
      cat "$tmpfile" > "$ENV_FILE"
      rm -f "$tmpfile"
      chmod 600 "$ENV_FILE"
    else
      echo "[install] Adding DISCORD_BOT_TOKEN to existing $ENV_FILE (no token printed)"
      printf '\nDISCORD_BOT_TOKEN="%s"\n# token source: OpenClaw/Clawdbot config (channels.discord.token)\n' "$TOKEN" >> "$ENV_FILE"
      chmod 600 "$ENV_FILE"
    fi
  else
    echo "[install] $ENV_FILE already exists; leaving it unchanged"
  fi
fi


# --- systemd service install ---
if command -v systemctl >/dev/null 2>&1; then
  echo "[install] Installing systemd unit (discord-indexer.service)"

  # Big warning for missing Docker (common on WSL2)
  if ! command -v docker >/dev/null 2>&1; then
    cat >&2 <<'WARN'

====================  DOCKER REQUIRED  ====================
This installer is configured to run MongoDB as a Docker container
via systemd (discord-indexer-mongo.service).

Docker was NOT detected on this machine.

- If you're on WSL2: you likely need Docker Desktop + WSL integration.
- Otherwise: install Docker Engine for your distro.

Without Docker (or an alternative MongoDB), discord-indexer will fail
with Mongo connection errors (default MONGODB_URI points to localhost).
===========================================================

WARN
  fi

  if ! id -u discord-indexer >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin discord-indexer
  fi

  mkdir -p /var/log/discord-indexer
  # Allow non-root users to read logs without sudo
  touch /var/log/discord-indexer/discord-indexer.log
  chown discord-indexer:discord-indexer /var/log/discord-indexer/discord-indexer.log
  chmod 0644 /var/log/discord-indexer/discord-indexer.log

  # If docker is available, manage MongoDB as a docker container via systemd.
# (This keeps Mongo alive across reboots and avoids host-level mongod installs.)
if command -v docker >/dev/null 2>&1; then
  cat > /etc/systemd/system/discord-indexer-mongo.service <<'MONGO_UNIT'
[Unit]
Description=discord-indexer MongoDB (docker container)
Requires=docker.service
After=docker.service

[Service]
Type=simple

# Create volume (idempotent)
ExecStartPre=-/usr/bin/docker volume create discord_indexer_mongo

# Run mongod in the foreground so systemd tracks the real container lifetime.
# Do not use docker --restart here; systemd is the supervisor.
ExecStartPre=-/usr/bin/docker stop discord-indexer-mongo
ExecStartPre=-/usr/bin/docker rm discord-indexer-mongo
ExecStart=/usr/bin/docker run --rm --name discord-indexer-mongo -p 127.0.0.1:27017:27017 -v discord_indexer_mongo:/data/db mongo:6
ExecStop=-/usr/bin/docker stop discord-indexer-mongo
Restart=always
RestartSec=3
TimeoutStartSec=60
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
MONGO_UNIT
fi

cat > /etc/systemd/system/discord-indexer.service <<'UNIT'
[Unit]
Description=discord-indexer (Discord -> MongoDB)
After=network-online.target
Wants=network-online.target

# If present, require the docker-managed MongoDB and start only after it is up.
Requires=discord-indexer-mongo.service
After=discord-indexer-mongo.service

[Service]
Type=simple
User=discord-indexer
Group=discord-indexer
EnvironmentFile=/etc/discord-indexer/indexer.env

# Ensure log file exists and is world-readable, then wait for Mongo to accept TCP connections.
ExecStartPre=/bin/sh -lc 'mkdir -p /var/log/discord-indexer && touch /var/log/discord-indexer/discord-indexer.log && chown discord-indexer:discord-indexer /var/log/discord-indexer/discord-indexer.log && chmod 0644 /var/log/discord-indexer/discord-indexer.log'
ExecStartPre=/bin/bash -lc 'for i in {1..90}; do (echo >/dev/tcp/127.0.0.1/27017) >/dev/null 2>&1 && exit 0; sleep 0.5; done; echo "Mongo not ready" >&2; exit 1'

ExecStart=/usr/local/bin/discord-indexer
Restart=always
RestartSec=2

# Logging
StandardOutput=append:/var/log/discord-indexer/discord-indexer.log
StandardError=append:/var/log/discord-indexer/discord-indexer.log

# Light hardening (don't break .NET JIT)
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/log/discord-indexer

[Install]
WantedBy=multi-user.target
UNIT

  systemctl daemon-reload
  # Enable mongo unit if it exists
  if systemctl list-unit-files | grep -q '^discord-indexer-mongo\.service'; then
    systemctl enable --now discord-indexer-mongo.service || true
  fi

  systemctl enable --now discord-indexer.service || systemctl restart discord-indexer.service || true
  echo "[install] systemd: enabled+started discord-indexer.service"
else
  echo "[install] NOTE: systemctl not found; skipping service installation" >&2
fi

cat <<EOF

[install] Done.
- Env file: $ENV_FILE
- Logs: /var/log/discord-indexer/discord-indexer.log
- Service: discord-indexer.service (systemd)

Check status:
  systemctl status discord-indexer.service --no-pager
  tail -n 200 /var/log/discord-indexer/discord-indexer.log

EOF

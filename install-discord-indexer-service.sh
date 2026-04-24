#!/usr/bin/env bash
set -euo pipefail

# One-step installer for discord-indexer (systemd service on Ubuntu).
#
# What it does:
# - Builds the binary automatically:
#     - prefers local dotnet SDK if present
#     - otherwise uses Docker (build stage) if available
# - Installs binary to /usr/local/bin/discord-indexer
# - Writes secrets to /etc/discord-indexer/indexer.env (0600 root:root)
# - Installs + starts systemd units for MongoDB (optional) and discord-indexer.service

# ====== CONFIG (override via env) ======
REPO_DIR="${REPO_DIR:-$(pwd)}"
PROJECT_FILE="${PROJECT_FILE:-$REPO_DIR/discord-indexer.csproj}"
RUNTIME="${RUNTIME:-linux-x64}"
CONFIGURATION="${CONFIGURATION:-Release}"
PUBLISH_DIR="${PUBLISH_DIR:-$REPO_DIR/.publish}"

BIN_DST="${BIN_DST:-/usr/local/bin/discord-indexer}"

SVC_USER="${SVC_USER:-discord-indexer}"
SVC_GROUP="${SVC_GROUP:-discord-indexer}"

STATE_DIR="${STATE_DIR:-/var/lib/discord-indexer}"
LOG_DIR="${LOG_DIR:-/var/log/discord-indexer}"

ENV_DIR="${ENV_DIR:-/etc/discord-indexer}"
ENV_FILE="${ENV_FILE:-$ENV_DIR/indexer.env}"

UNIT_NAME="${UNIT_NAME:-discord-indexer.service}"

# If you want to skip building entirely, set BIN_SRC and SKIP_BUILD=1.
BIN_SRC="${BIN_SRC:-}"

# ====== REQUIRED SETTINGS (export before running) ======
DISCORD_BOT_TOKEN="${DISCORD_BOT_TOKEN:-}"
DISCORD_GUILD_IDS="${DISCORD_GUILD_IDS:-}"     # optional; if empty, indexer will attempt auto-discovery
# Gateway intents. Default includes DMs (DIRECT_MESSAGES=4096). For message text, enable MESSAGE_CONTENT (32768) in the Discord Developer Portal.
DISCORD_INTENTS="${DISCORD_INTENTS:-4609}"

# By default the installer will provision a dedicated mongo:6 container and bind it to 127.0.0.1:$MONGO_PORT.
# You can disable with INSTALL_MONGO=0 or point at an external Mongo by setting MONGODB_URI explicitly.
INSTALL_MONGO="${INSTALL_MONGO:-1}"
MONGO_CONTAINER_NAME="${MONGO_CONTAINER_NAME:-discord-indexer-mongo}"
MONGO_VOLUME_NAME="${MONGO_VOLUME_NAME:-discord_indexer_mongo}"
MONGO_PORT="${MONGO_PORT:-27017}"

MONGODB_URI="${MONGODB_URI:-mongodb://127.0.0.1:${MONGO_PORT}}"
MONGODB_DB="${MONGODB_DB:-discord_index}"

# Optional CLI flags (if/when your indexer supports them)
INDEXER_OPTS="${INDEXER_OPTS:-}"

need_cmd() {
  command -v "$1" >/dev/null 2>&1
}

die() {
  echo "ERROR: $*" >&2
  exit 1
}

# ====== checks ======
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  [[ -f "$PROJECT_FILE" ]] || die "Could not find project file: $PROJECT_FILE (run from repo root)"
fi
[[ -n "$DISCORD_BOT_TOKEN" ]] || die "DISCORD_BOT_TOKEN is required (export it before running)."

# ====== ensure mongo (optional) ======
if [[ "$INSTALL_MONGO" == "1" ]]; then
  if need_cmd docker; then
    # If MONGODB_URI was overridden to a non-local URI, don't try to manage mongo.
    if [[ "$MONGODB_URI" == mongodb://127.0.0.1:* || "$MONGODB_URI" == mongodb://localhost:* ]]; then
      # Check for port conflicts
      if ss -ltn 2>/dev/null | awk '{print $4}' | grep -Eq "(^|:)${MONGO_PORT}$"; then
        # If the mongo container is already running on that port, this is fine; otherwise it's a conflict.
        if ! docker ps --format '{{.Names}}' | grep -Eq "^${MONGO_CONTAINER_NAME}$"; then
          die "Port ${MONGO_PORT} is already in use. Set MONGO_PORT=27018 (and rerun), or set MONGODB_URI to an external Mongo."
        fi
      fi

      # Create volume if missing
      docker volume inspect "$MONGO_VOLUME_NAME" >/dev/null 2>&1 || docker volume create "$MONGO_VOLUME_NAME" >/dev/null

      # Create/start container if missing
      if ! docker ps -a --format '{{.Names}}' | grep -Eq "^${MONGO_CONTAINER_NAME}$"; then
        echo "[install] Starting dedicated MongoDB container: ${MONGO_CONTAINER_NAME} (127.0.0.1:${MONGO_PORT})"
        docker run -d \
          --name "$MONGO_CONTAINER_NAME" \
          --restart unless-stopped \
          -p "127.0.0.1:${MONGO_PORT}:27017" \
          -v "${MONGO_VOLUME_NAME}:/data/db" \
          mongo:6 >/dev/null
      else
        if ! docker ps --format '{{.Names}}' | grep -Eq "^${MONGO_CONTAINER_NAME}$"; then
          echo "[install] Starting existing MongoDB container: ${MONGO_CONTAINER_NAME}"
          docker start "$MONGO_CONTAINER_NAME" >/dev/null
        fi
      fi
    fi
  else
    echo "WARN: INSTALL_MONGO=1 but docker is not installed; skipping Mongo container provisioning." >&2
  fi
fi

# ====== build ======
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  if [[ -z "$BIN_SRC" ]]; then
    BIN_SRC="$PUBLISH_DIR/discord-indexer"
  fi

  rm -rf "$PUBLISH_DIR" && mkdir -p "$PUBLISH_DIR"

  if need_cmd dotnet; then
    echo "[install] Building with local dotnet SDK -> $PUBLISH_DIR"
    dotnet publish "$PROJECT_FILE" \
      -c "$CONFIGURATION" \
      -r "$RUNTIME" \
      -o "$PUBLISH_DIR" \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:PublishTrimmed=false
  elif need_cmd docker; then
    # Docker build fallback: uses repo Dockerfile build stage.
    echo "[install] dotnet not found; building via Docker"
    echo "[install] Building docker image (target=export) ..."
    docker build -t discord-indexer-build:local --target export "$REPO_DIR" >/dev/null

    # Image target=export is FROM scratch and has no default CMD; provide one so docker can create the container.
    cid="$(docker create discord-indexer-build:local /discord-indexer)"
    trap 'docker rm -f "$cid" >/dev/null 2>&1 || true' EXIT

    echo "[install] Extracting binary from image -> $BIN_SRC"
    docker cp "$cid:/discord-indexer" "$BIN_SRC"
    chmod +x "$BIN_SRC"
  else
    die "Neither dotnet SDK nor docker is installed, so I can't build the binary. Install dotnet-sdk-8.0 or docker, or set BIN_SRC=/path/to/prebuilt/discord-indexer and SKIP_BUILD=1."
  fi
fi

[[ -n "$BIN_SRC" ]] || die "BIN_SRC is empty (set BIN_SRC or allow the script to build)."
[[ -f "$BIN_SRC" ]] || die "Binary not found at BIN_SRC=$BIN_SRC"

# ====== create user/group ======
if ! getent group "$SVC_GROUP" >/dev/null; then
  sudo groupadd --system "$SVC_GROUP"
fi

if ! id -u "$SVC_USER" >/dev/null 2>&1; then
  sudo useradd --system --gid "$SVC_GROUP" \
    --home-dir "$STATE_DIR" --create-home \
    --shell /usr/sbin/nologin \
    "$SVC_USER"
fi

# ====== dirs ======
sudo install -d -o "$SVC_USER" -g "$SVC_GROUP" -m 0750 "$STATE_DIR"
sudo install -d -o "$SVC_USER" -g "$SVC_GROUP" -m 0750 "$LOG_DIR"
sudo install -d -o root -g root -m 0755 "$ENV_DIR"

# ====== install binary ======
echo "[install] Installing binary -> $BIN_DST"
sudo install -o root -g root -m 0755 "$BIN_SRC" "$BIN_DST"

# ====== install helper CLI ======
HELPER_SRC="$REPO_DIR/discord-indexer-search"
if [[ -f "$HELPER_SRC" ]]; then
  echo "[install] Installing helper -> /usr/local/bin/discord-indexer-search"
  sudo install -o root -g root -m 0755 "$HELPER_SRC" /usr/local/bin/discord-indexer-search
fi

DELTA_HELPER_SRC="$REPO_DIR/discord-indexer-delta"
if [[ -f "$DELTA_HELPER_SRC" ]]; then
  echo "[install] Installing helper -> /usr/local/bin/discord-indexer-delta"
  sudo install -o root -g root -m 0755 "$DELTA_HELPER_SRC" /usr/local/bin/discord-indexer-delta
fi

# ====== write env file (secrets live here) ======
echo "[install] Writing env file -> $ENV_FILE"
tmp_env="$(mktemp)"
cat >"$tmp_env" <<EOF
# discord-indexer env (loaded by systemd)
DISCORD_BOT_TOKEN=${DISCORD_BOT_TOKEN}
DISCORD_GUILD_IDS=${DISCORD_GUILD_IDS}
DISCORD_INTENTS=${DISCORD_INTENTS}
MONGODB_URI=${MONGODB_URI}
MONGODB_DB=${MONGODB_DB}

# Optional extra flags consumed by ExecStart via \$INDEXER_OPTS
INDEXER_OPTS=${INDEXER_OPTS}
EOF

sudo install -o root -g root -m 0600 "$tmp_env" "$ENV_FILE"
rm -f "$tmp_env"

# ====== optional systemd-managed MongoDB ======
MANAGED_MONGO=0
MONGO_UNIT_NAME="discord-indexer-mongo.service"
MONGO_WAIT_CMD="/bin/bash -lc 'for i in {1..120}; do (echo >/dev/tcp/127.0.0.1/${MONGO_PORT}) >/dev/null 2>&1 && exit 0; sleep 0.5; done; echo \"Mongo not ready\" >&2; exit 1'"

if [[ "$INSTALL_MONGO" == "1" && ( "$MONGODB_URI" == mongodb://127.0.0.1:* || "$MONGODB_URI" == mongodb://localhost:* ) ]]; then
  if need_cmd docker; then
    MANAGED_MONGO=1
    echo "[install] Installing systemd Mongo unit -> /etc/systemd/system/${MONGO_UNIT_NAME}"
    tmp_mongo_unit="$(mktemp)"
    cat >"$tmp_mongo_unit" <<EOF
[Unit]
Description=discord-indexer MongoDB (docker container)
Requires=docker.service
After=docker.service

[Service]
Type=simple
ExecStartPre=-/usr/bin/docker volume create ${MONGO_VOLUME_NAME}
ExecStartPre=-/usr/bin/docker stop ${MONGO_CONTAINER_NAME}
ExecStartPre=-/usr/bin/docker rm ${MONGO_CONTAINER_NAME}
ExecStart=/usr/bin/docker run --rm --name ${MONGO_CONTAINER_NAME} -p 127.0.0.1:${MONGO_PORT}:27017 -v ${MONGO_VOLUME_NAME}:/data/db mongo:6
ExecStop=-/usr/bin/docker stop ${MONGO_CONTAINER_NAME}
Restart=always
RestartSec=3
TimeoutStartSec=60
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF
    sudo install -o root -g root -m 0644 "$tmp_mongo_unit" "/etc/systemd/system/${MONGO_UNIT_NAME}"
    rm -f "$tmp_mongo_unit"
  else
    echo "WARN: INSTALL_MONGO=1 but docker is not installed; no MongoDB systemd unit installed." >&2
  fi
fi

# ====== systemd unit ======
echo "[install] Installing systemd unit -> /etc/systemd/system/${UNIT_NAME}"
tmp_unit="$(mktemp)"
cat >"$tmp_unit" <<EOF
[Unit]
Description=Discord Indexer (.NET)
After=network-online.target
Wants=network-online.target
EOF

if [[ "$MANAGED_MONGO" == "1" ]]; then
  cat >>"$tmp_unit" <<EOF
Requires=${MONGO_UNIT_NAME}
After=${MONGO_UNIT_NAME}
EOF
fi

cat >>"$tmp_unit" <<EOF

[Service]
Type=simple
User=${SVC_USER}
Group=${SVC_GROUP}
WorkingDirectory=${STATE_DIR}
EnvironmentFile=${ENV_FILE}
Environment=MONGO_PORT=${MONGO_PORT}

# Wait for Mongo to accept TCP connections (avoid crash loops at boot)
ExecStartPre=${MONGO_WAIT_CMD}

StandardOutput=append:${LOG_DIR}/discord-indexer.log
StandardError=append:${LOG_DIR}/discord-indexer.err

ExecStart=${BIN_DST} \$INDEXER_OPTS
Restart=always
RestartSec=2

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=${STATE_DIR} ${LOG_DIR}
LockPersonality=true
# .NET JIT needs W+X memory; enabling this can crash (SIGSEGV).
# If you publish NativeAOT / fully AOT, you can re-enable.
# MemoryDenyWriteExecute=true

[Install]
WantedBy=multi-user.target
EOF

sudo install -o root -g root -m 0644 "$tmp_unit" "/etc/systemd/system/${UNIT_NAME}"
rm -f "$tmp_unit"

# ====== enable + start ======
echo "[install] Enabling + starting systemd units"
sudo systemctl daemon-reload
if [[ "$MANAGED_MONGO" == "1" ]]; then
  sudo systemctl enable --now "${MONGO_UNIT_NAME}"
fi
sudo systemctl enable --now "${UNIT_NAME}"

echo
echo "OK: Installed and started: ${UNIT_NAME}"
echo "Status: sudo systemctl status ${UNIT_NAME} --no-pager"
echo "Logs:   sudo journalctl -u ${UNIT_NAME} -f"

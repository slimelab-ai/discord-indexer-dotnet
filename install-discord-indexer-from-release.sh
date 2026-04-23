#!/usr/bin/env bash
set -euo pipefail

# install-discord-indexer-from-release.sh
#
# Installs discord-indexer + helper CLIs from the latest GitHub Release.
# Designed for Ubuntu/Linux hosts. Does NOT require dotnet or docker.
#
# What it does:
# - Downloads discord-indexer-linux-x64.tar.gz + .sha256 from a GitHub Release
# - Verifies checksums
# - Installs binaries to /usr/local/bin
# - Optionally (default) installs/updates systemd unit + env file (compatible with existing service installer)
#
# Env vars:
#   REPO=patrick-slimelab/discord-indexer-dotnet   (default)
#   VERSION=latest|vX.Y.Z                          (default: latest)
#   INSTALL_SYSTEMD=1|0                            (default: 1)
#
# After install:
#   sudo systemctl daemon-reload
#   sudo systemctl restart discord-indexer.service

REPO="${REPO:-marvin-mira/discord-indexer-dotnet}"
VERSION="${VERSION:-latest}"
INSTALL_SYSTEMD="${INSTALL_SYSTEMD:-1}"

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

if [[ "$(id -u)" -ne 0 ]]; then
  echo "ERROR: run as root (use sudo)" >&2
  exit 1
fi

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# Resolve tag
TAG="$VERSION"
if [[ "$VERSION" == "latest" ]]; then
  echo "[install] Resolving latest release for $REPO"
  TAG="$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" | sed -n 's/.*"tag_name" *: *"\([^"]*\)".*/\1/p' | head -n1)"
  if [[ -z "$TAG" ]]; then
    echo "ERROR: could not resolve latest release tag via GitHub API" >&2
    exit 1
  fi
fi

BASE_URL="https://github.com/${REPO}/releases/download/${TAG}"

echo "[install] Downloading $ASSET_TGZ and $ASSET_SHA from $BASE_URL"

curl -fsSL -o "$TMP/$ASSET_TGZ" "$BASE_URL/$ASSET_TGZ"
curl -fsSL -o "$TMP/$ASSET_SHA" "$BASE_URL/$ASSET_SHA"

cd "$TMP"

echo "[install] Extracting"
tar -xzf "$ASSET_TGZ"

echo "[install] Verifying checksums"
sha256sum -c "$ASSET_SHA"

install -m 0755 "$TMP/discord-indexer" /usr/local/bin/discord-indexer
install -m 0755 "$TMP/discord-indexer-search" /usr/local/bin/discord-indexer-search
install -m 0755 "$TMP/discord-indexer-delta" /usr/local/bin/discord-indexer-delta

echo "[install] Installed:" \
  "/usr/local/bin/discord-indexer" \
  "/usr/local/bin/discord-indexer-search" \
  "/usr/local/bin/discord-indexer-delta"

if [[ "$INSTALL_SYSTEMD" == "1" ]]; then
  echo "[install] Installing/updating systemd service + env file"
  if [[ -f "/etc/discord-indexer/indexer.env" ]]; then
    echo "[install] Found existing /etc/discord-indexer/indexer.env; reusing values"
    set -a
    # shellcheck disable=SC1091
    source /etc/discord-indexer/indexer.env
    set +a
  fi

  if [[ -x "./install-discord-indexer-service.sh" ]]; then
    SKIP_BUILD=1 BIN_SRC=/usr/local/bin/discord-indexer REPO_DIR="$TMP" ./install-discord-indexer-service.sh
  else
    echo "ERROR: bundled install-discord-indexer-service.sh missing from release asset" >&2
    exit 1
  fi
fi

echo "[install] Done. If using systemd:" \
  "sudo systemctl daemon-reload && sudo systemctl restart discord-indexer.service"

#!/usr/bin/env bash
# Mount Cities Skylines game files and AppData from a remote Windows machine.
#
# Configuration is read from .env in the repo root (copy .env.example to .env).
# Any variable can also be overridden by exporting it before running this script.
#
# Usage:
#   ./mount-cities.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Load .env if present ────────────────────────────────────────────────────────
if [[ -f "$SCRIPT_DIR/.env" ]]; then
    while IFS='=' read -r _key _value; do
        [[ "$_key" =~ ^[[:space:]]*# ]] && continue
        [[ -z "$_key" ]]               && continue
        _value="${_value%\"}"  ; _value="${_value#\"}"
        _value="${_value%\'}"  ; _value="${_value#\'}"
        export "$_key=$_value"
    done < "$SCRIPT_DIR/.env"
    unset _key _value
fi

# ── Validate required variables ─────────────────────────────────────────────────
missing=()
[[ -z "${CITIES_HOST:-}"       ]] && missing+=("CITIES_HOST")
[[ -z "${CITIES_GAME_SHARE:-}" ]] && missing+=("CITIES_GAME_SHARE")
[[ -z "${CITIES_DATA_SHARE:-}" ]] && missing+=("CITIES_DATA_SHARE")
[[ -z "${CITIES_SMB_USER:-}"   ]] && missing+=("CITIES_SMB_USER")

if [[ ${#missing[@]} -gt 0 ]]; then
    echo "Error: missing required variables: ${missing[*]}"
    echo "Copy .env.example to .env and fill in your values."
    exit 1
fi

# ── Password ─────────────────────────────────────────────────────────────────────
if [[ -z "${SMB_PASSWORD:-}" ]]; then
    read -rsp "SMB password for ${CITIES_SMB_USER}@${CITIES_HOST}: " SMB_PASSWORD
    echo
fi

# ── Mount points (can be overridden via env) ─────────────────────────────────────
GAME_MOUNT="${CITIES_GAME_MOUNT:-/mnt/cities_skylines}"
DATA_MOUNT="${CITIES_DATA_MOUNT:-/mnt/cities_skylines_data}"

COMMON_OPTS="username=${CITIES_SMB_USER},password=${SMB_PASSWORD},uid=$(id -u),gid=$(id -g),vers=3.0"

mount_share() {
    local remote="$1"
    local local_path="$2"

    if mountpoint -q "$local_path"; then
        echo "Already mounted: $local_path"
        return
    fi

    sudo mkdir -p "$local_path"
    sudo mount -t cifs "//${CITIES_HOST}/${remote}" "$local_path" -o "$COMMON_OPTS"
    echo "Mounted: //${CITIES_HOST}/${remote} -> $local_path"
}

WORKSHOP_MOUNT="${CITIES_WORKSHOP_MOUNT:-/mnt/cities_workshop}"

mount_share "$CITIES_GAME_SHARE" "$GAME_MOUNT"
mount_share "$CITIES_DATA_SHARE" "$DATA_MOUNT"
mount_share "workshop" "$WORKSHOP_MOUNT"

echo ""
echo "Done. Game DLLs:  ${GAME_MOUNT}/Cities_Data/Managed/"
echo "      Mod output: ${DATA_MOUNT}/Addons/Mods/"
echo "      Game log:   ${GAME_MOUNT}/Cities_Data/output_log.txt"
echo "      Workshop:   ${WORKSHOP_MOUNT}/"

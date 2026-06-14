#!/usr/bin/env bash
# publish.sh — First-time upload of Take a Walk to Steam Workshop via steamcmd.
#
# Prerequisites:
#   - steamcmd installed
#   - STEAM_USERNAME set in .env
#   - dist/TakeAWalk/ already staged by ./deploy.sh --release
#
# Usage:
#   ./deploy.sh --release
#   ./publish.sh                        # creates a new Workshop item
#   ./publish.sh "Initial release"      # with a custom change note

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

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

APP_ID="255710"
ITEM_ID="${WORKSHOP_ITEM_ID:-0}"
MOD_NAME="TakeAWalk"
DIST="$SCRIPT_DIR/dist/$MOD_NAME"
WORKSHOP_DIR="$SCRIPT_DIR/Workshop"
VDF="$WORKSHOP_DIR/item.vdf"
CHANGE_NOTE="${1:-Initial release}"
STEAM_USERNAME="${STEAM_USERNAME:-}"

if ! command -v steamcmd &>/dev/null; then
    echo "ERROR: steamcmd not found. Install with: sudo apt-get install steamcmd"
    exit 1
fi

if [[ -z "$STEAM_USERNAME" ]]; then
    echo "ERROR: STEAM_USERNAME not set. Add it to .env."
    exit 1
fi

if [[ "$ITEM_ID" == "0" ]]; then
    echo "NOTE: WORKSHOP_ITEM_ID not set — this will create a NEW Workshop item."
    echo "After publish, add the ID to .env: WORKSHOP_ITEM_ID=<id>"
fi

if [[ ! -f "$DIST/$MOD_NAME.dll" ]]; then
    echo "ERROR: $DIST/$MOD_NAME.dll not found. Run ./deploy.sh --release first."
    exit 1
fi

mkdir -p "$WORKSHOP_DIR"

# previewfile is optional: steamcmd fails with "File Not Found" if the path is set but the file is
# missing, so only emit the line when PreviewImage.png actually exists.
PREVIEW="$WORKSHOP_DIR/PreviewImage.png"
PREVIEW_LINE=""
if [[ -f "$PREVIEW" ]]; then
    PREVIEW_LINE="    \"previewfile\"     \"$PREVIEW\""
else
    echo "NOTE: $PREVIEW not found, publishing without a preview image (set one later in the Workshop)."
fi

# visibility: only force it when CREATING a new item (id 0), and create it hidden so a half-finished
# first publish never goes live by accident. On updates (id set) the line is omitted, preserving
# whatever visibility you chose in the Workshop. 0=public, 1=friends, 2=hidden, 3=unlisted.
VISIBILITY_LINE=""
if [[ "$ITEM_ID" == "0" ]]; then
    VISIBILITY_LINE="    \"visibility\"      \"2\""
fi

cat > "$VDF" <<EOF
"workshopitem"
{
    "appid"           "$APP_ID"
    "publishedfileid" "$ITEM_ID"
    "contentfolder"   "$DIST"
$PREVIEW_LINE
$VISIBILITY_LINE
    "title"           "Take a Walk"
    "changenote"      "$CHANGE_NOTE"
}
EOF

echo "Uploading $MOD_NAME (item $ITEM_ID) to Workshop..."
steamcmd +login "$STEAM_USERNAME" +workshop_build_item "$VDF" +quit

if [[ "$ITEM_ID" != "0" ]]; then
    echo "Done: https://steamcommunity.com/sharedfiles/filedetails/?id=$ITEM_ID"
else
    echo "Check steamcmd output above for the new item ID, then set WORKSHOP_ITEM_ID in .env."
fi

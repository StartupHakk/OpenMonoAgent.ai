#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# deploy-ftp/scripts/diff.sh
#
# Compares the local build directory against the remote FTP server.
# Outputs a categorised list of NEW, MODIFIED, and UNCHANGED files.
#
# Required env vars:
#   FTP_HOST, FTP_USER, FTP_PASSWORD, REMOTE_PATH, LOCAL_PATH
# Optional:
#   PASSIVE  (default: true)
#
# Exit 0 = diff complete (even if all files unchanged).
# Exit 1 = could not connect to FTP server.
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

FTP_HOST="${FTP_HOST:?FTP_HOST is required}"
FTP_USER="${FTP_USER:?FTP_USER is required}"
FTP_PASSWORD="${FTP_PASSWORD:?FTP_PASSWORD is required}"
REMOTE_PATH="${REMOTE_PATH:-/public_html}"
LOCAL_PATH="${LOCAL_PATH:-./dist}"
PASSIVE="${PASSIVE:-true}"

TMPDIR_REMOTE=$(mktemp -d)
trap 'rm -rf "$TMPDIR_REMOTE"' EXIT

PASSIVE_FLAG=""
if [[ "$PASSIVE" == "true" ]]; then
    PASSIVE_FLAG="set ftp:passive-mode true;"
fi

# Fetch remote file listing (name, size, mtime) into a temp file
REMOTE_LIST="$TMPDIR_REMOTE/remote.txt"

lftp -c "
  $PASSIVE_FLAG
  set ftp:ssl-allow true;
  set ssl:verify-certificate false;
  open -u '$FTP_USER','$FTP_PASSWORD' '$FTP_HOST';
  find '$REMOTE_PATH' -type f;
  bye
" 2>/dev/null | sed "s|^$REMOTE_PATH/||" | sort > "$REMOTE_LIST" || {
    echo "ERROR: Could not connect to FTP server at $FTP_HOST" >&2
    echo "Check your FTP_HOST, FTP_USER, and FTP_PASSWORD environment variables." >&2
    exit 1
}

# Build local file list
LOCAL_LIST="$TMPDIR_REMOTE/local.txt"
find "$LOCAL_PATH" -type f | sed "s|^$LOCAL_PATH/||" | sort > "$LOCAL_LIST"

# Compare
NEW_FILES=()
MODIFIED_FILES=()
UNCHANGED_FILES=()

while IFS= read -r file; do
    if ! grep -qxF "$file" "$REMOTE_LIST"; then
        NEW_FILES+=("$file")
    else
        # Compare sizes to detect modifications
        LOCAL_SIZE=$(stat -f%z "$LOCAL_PATH/$file" 2>/dev/null || stat -c%s "$LOCAL_PATH/$file" 2>/dev/null || echo 0)
        REMOTE_SIZE=$(lftp -c "
          $PASSIVE_FLAG
          open -u '$FTP_USER','$FTP_PASSWORD' '$FTP_HOST';
          cls -l '$REMOTE_PATH/$file';
          bye
        " 2>/dev/null | awk '{print $5}' || echo 0)

        if [[ "$LOCAL_SIZE" != "$REMOTE_SIZE" ]]; then
            MODIFIED_FILES+=("$file")
        else
            UNCHANGED_FILES+=("$file")
        fi
    fi
done < "$LOCAL_LIST"

# Output results
echo "NEW:${#NEW_FILES[@]}"
echo "MODIFIED:${#MODIFIED_FILES[@]}"
echo "UNCHANGED:${#UNCHANGED_FILES[@]}"
echo "---"

for f in "${NEW_FILES[@]:-}"; do
    [[ -n "$f" ]] && echo "NEW:$f"
done

for f in "${MODIFIED_FILES[@]:-}"; do
    [[ -n "$f" ]] && echo "MODIFIED:$f"
done

for f in "${UNCHANGED_FILES[@]:-}"; do
    [[ -n "$f" ]] && echo "UNCHANGED:$f"
done

exit 0

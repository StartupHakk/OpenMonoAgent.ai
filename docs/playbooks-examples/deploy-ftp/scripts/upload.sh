#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# deploy-ftp/scripts/upload.sh
#
# Uploads new and modified files from LOCAL_PATH to REMOTE_PATH on the FTP server.
# Uses lftp mirror for efficient, resumable transfer.
#
# Required env vars:
#   FTP_HOST, FTP_USER, FTP_PASSWORD, REMOTE_PATH, LOCAL_PATH
# Optional:
#   DRY_RUN  (default: false) — prints what would be uploaded, transfers nothing
#   PASSIVE  (default: true)
#
# Exit 0 = upload complete (or dry-run complete).
# Exit 1 = upload failed.
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

FTP_HOST="${FTP_HOST:?FTP_HOST is required}"
FTP_USER="${FTP_USER:?FTP_USER is required}"
FTP_PASSWORD="${FTP_PASSWORD:?FTP_PASSWORD is required}"
REMOTE_PATH="${REMOTE_PATH:-/public_html}"
LOCAL_PATH="${LOCAL_PATH:-./dist}"
DRY_RUN="${DRY_RUN:-false}"
PASSIVE="${PASSIVE:-true}"

PASSIVE_FLAG=""
if [[ "$PASSIVE" == "true" ]]; then
    PASSIVE_FLAG="set ftp:passive-mode true;"
fi

# Never upload secret files
EXCLUDE_PATTERN='\.env$|\.env\.|\.pem$|\.key$|\.p12$|credentials\.'

START_TIME=$(date +%s)

if [[ "$DRY_RUN" == "true" ]]; then
    echo ""
    echo "DRY RUN — no files will be transferred"
    echo ""

    lftp -c "
      $PASSIVE_FLAG
      set ftp:ssl-allow true;
      set ssl:verify-certificate false;
      open -u '$FTP_USER','$FTP_PASSWORD' '$FTP_HOST';
      mirror --reverse --dry-run --verbose \
             --exclude-glob .env \
             --exclude-glob '*.pem' \
             --exclude-glob '*.key' \
             --exclude-glob 'credentials.*' \
             '$LOCAL_PATH/' '$REMOTE_PATH/';
      bye
    "

    echo ""
    echo "Dry run complete. Run without --dry-run to transfer files."
    exit 0
fi

echo ""
echo "Uploading to ftp://$FTP_HOST$REMOTE_PATH ..."
echo ""

lftp -c "
  $PASSIVE_FLAG
  set ftp:ssl-allow true;
  set ssl:verify-certificate false;
  set net:timeout 30;
  set net:max-retries 3;
  set net:reconnect-interval-base 5;
  open -u '$FTP_USER','$FTP_PASSWORD' '$FTP_HOST';
  mirror --reverse --verbose --only-newer \
         --exclude-glob .env \
         --exclude-glob '*.env.*' \
         --exclude-glob '*.pem' \
         --exclude-glob '*.key' \
         --exclude-glob '*.p12' \
         --exclude-glob 'credentials.*' \
         --parallel=4 \
         '$LOCAL_PATH/' '$REMOTE_PATH/';
  bye
"

END_TIME=$(date +%s)
ELAPSED=$((END_TIME - START_TIME))

echo ""
echo "──────────────────────────────────────────"
echo "Upload complete in ${ELAPSED}s"
echo "Remote: ftp://$FTP_HOST$REMOTE_PATH"
echo "──────────────────────────────────────────"

exit 0

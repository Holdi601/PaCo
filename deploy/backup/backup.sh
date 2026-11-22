#!/bin/bash
# Nightly encrypted backup of the whole PaCo installation to an SMB share (e.g. a second NAS).
#
#   Backed up: the PaCo checkout incl. deploy/.env (secrets, master key), the data directory (database copied
#   consistently while the server runs, key ring, screenshots), the ntfy data.
#   Encryption: AES-256 (GPG, symmetric) with the backup key in $KEY_FILE – that key is NEVER copied to the share.
#   Keep a copy of it in a password manager: without it a backup cannot be restored.
#
# Settings (environment or ~/.paco-backup.conf): SMB_SHARE (//host/share), SMB_DIR, KEEP_DAYS, PACO_DIR, DATA_DIR, NTFY_DIR.
set -euo pipefail
CONF="${HOME}/.paco-backup.conf"
[ -f "$CONF" ] && . "$CONF"
SMB_SHARE="${SMB_SHARE:?SMB_SHARE fehlt (z. B. //nas2/backup)}"
SMB_DIR="${SMB_DIR:-PaCo-Backup}"
KEEP_DAYS="${KEEP_DAYS:-14}"
PACO_DIR="${PACO_DIR:-$HOME/paco}"
DATA_DIR="${DATA_DIR:-$HOME/paco-data}"
NTFY_DIR="${NTFY_DIR:-$HOME/paco-ntfy}"
KEY_FILE="${KEY_FILE:-$HOME/.paco-backup-key}"
IMAGE=paco-backup-smb

umask 077
if [ ! -s "$KEY_FILE" ]; then
  openssl rand -base64 48 > "$KEY_FILE"
  chmod 600 "$KEY_FILE"
  echo "Neuer Backup-Schlüssel erzeugt: $KEY_FILE – bitte im Passwort-Manager sichern!"
fi
docker image inspect "$IMAGE" >/dev/null 2>&1 || docker build -q -t "$IMAGE" "$PACO_DIR/deploy/backup" >/dev/null

STAMP=$(date +%Y-%m-%d_%H%M)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/paco-data"
# Consistent copy of the SQLite database while the server keeps running.
sqlite3 "$DATA_DIR/paco.db" ".backup '$WORK/paco-data/paco.db'"
tar -C "$DATA_DIR" --exclude='./paco.db' --exclude='./paco.db-*' -cf - . | tar -C "$WORK/paco-data" -xf -

ARCHIVE="$WORK/paco-$STAMP.tar.gz.gpg"
tar -C "$WORK" -czf - paco-data \
    -C "$(dirname "$PACO_DIR")" "$(basename "$PACO_DIR")" \
    -C "$(dirname "$NTFY_DIR")" "$(basename "$NTFY_DIR")" \
  | gpg --batch --yes --quiet --symmetric --cipher-algo AES256 --pinentry-mode loopback --passphrase-file "$KEY_FILE" -o "$ARCHIVE"

smb() { docker run --rm --network host -v "$WORK:/work:ro" "$IMAGE" "$SMB_SHARE" -N -m SMB3 -c "$1"; }
smb "mkdir $SMB_DIR" >/dev/null 2>&1 || true
smb "cd $SMB_DIR; put /work/$(basename "$ARCHIVE") $(basename "$ARCHIVE")"

# Retention: delete backups older than KEEP_DAYS days.
CUTOFF=$(date -d "-$KEEP_DAYS days" +%Y-%m-%d)
smb "cd $SMB_DIR; ls paco-*.tar.gz.gpg" | awk '{print $1}' | grep -E '^paco-[0-9]{4}-[0-9]{2}-[0-9]{2}' | while read -r f; do
  d=${f:5:10}
  if [[ "$d" < "$CUTOFF" ]]; then smb "cd $SMB_DIR; del $f" >/dev/null && echo "alt gelöscht: $f"; fi
done
echo "Backup ok: $SMB_SHARE/$SMB_DIR/$(basename "$ARCHIVE") ($(du -h "$ARCHIVE" | cut -f1))"

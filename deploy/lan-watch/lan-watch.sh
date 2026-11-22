#!/bin/sh
# PaCo LAN watcher (optional): every minute, which hardware (MAC) addresses answer in the server's own network.
# Only that list goes to PaCo; PaCo compares it with the addresses its PCs reported and keeps nothing else.
# A PC that is visibly on in this network while PaCo on it is silent → push message to the parents.
set -u
: "${PACO_PUBLIC_URL:?PACO_PUBLIC_URL fehlt}"
if [ -z "${PACO_LAN_TOKEN:-}" ]; then echo "PACO_LAN_TOKEN fehlt in deploy/.env (openssl rand -hex 32)"; exit 1; fi

INTERVAL="${LAN_INTERVAL:-60}"
LOCAL_PORT="${PACO_PORT:-8790}"
# https://host:port/... → host and port of the public address; the request goes to the local port with the same
# host name, so the normal certificate check applies.
HOSTPORT=$(echo "$PACO_PUBLIC_URL" | sed -E 's#^https://([^/]+).*#\1#')
HOST=${HOSTPORT%%:*}
PUBLIC_PORT=${HOSTPORT##*:}
[ "$PUBLIC_PORT" = "$HOSTPORT" ] && PUBLIC_PORT=443
IFACE="${LAN_INTERFACE:-}"
[ -n "$IFACE" ] || IFACE=$(ip route show default 2>/dev/null | awk '/^default/ {print $5; exit}')
echo "PaCo-LAN-Wächter gestartet: Netzwerk an $IFACE, alle ${INTERVAL}s → https://$HOST:$PUBLIC_PORT (lokal Port $LOCAL_PORT)"

failures=0
while true; do
  macs=$(arp-scan --interface="$IFACE" --localnet --plain --quiet --retry=2 --ignoredups 2>/dev/null | awk '{print $2}' | sort -u)
  list=$(printf '%s\n' "$macs" | awk 'NF { printf "%s\"%s\"", (n++ ? "," : ""), $1 }')
  if out=$(printf '{"macs":[%s]}' "$list" | curl -sS --max-time 20 --fail-with-body \
        --connect-to "$HOST:$PUBLIC_PORT:127.0.0.1:$LOCAL_PORT" \
        -H "Authorization: Bearer $PACO_LAN_TOKEN" -H 'Content-Type: application/json' \
        --data-binary @- "https://$HOST:$PUBLIC_PORT/api/lan/seen" 2>&1); then
    [ "$failures" -gt 0 ] && echo "$(date -Iseconds) wieder ok: $out"
    failures=0
  else
    failures=$((failures + 1))
    # Log the first failure and then once an hour, not every minute.
    [ $((failures % 60)) -eq 1 ] && echo "$(date -Iseconds) Senden fehlgeschlagen ($failures×): $out"
  fi
  sleep "$INTERVAL"
done

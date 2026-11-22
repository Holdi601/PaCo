# Optional LAN watcher for PaCo (see docs/server-setup.md): arp-scan + curl, nothing else.
FROM alpine:3.21
RUN apk add --no-cache arp-scan curl ca-certificates
COPY lan-watch.sh /lan-watch.sh
ENTRYPOINT ["/bin/sh", "/lan-watch.sh"]

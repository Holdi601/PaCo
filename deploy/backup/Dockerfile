# Tiny helper image for the nightly backup: only the SMB client (nothing is installed on the host).
FROM alpine:3.21
RUN apk add --no-cache samba-client
ENTRYPOINT ["smbclient"]

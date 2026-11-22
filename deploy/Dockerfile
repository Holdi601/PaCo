# syntax=docker/dockerfile:1
# Builds the PaCo server (Linux) and the Windows client installer in one go.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props PaCo.slnx ./
COPY src/ src/
RUN dotnet publish src/PaCo.Server -c Release -o /out/server \
 && dotnet publish src/PaCo.Client -c Release -o /out/client -p:EnableWindowsTargeting=true \
 && mkdir -p /out/dist \
 && mv /out/client/PaCo.exe /out/dist/PaCo-Setup.exe \
 && sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props > /out/dist/version.txt

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/server ./
COPY --from=build /out/dist/ ./client/
ENV PaCo__DataDir=/data \
    PaCo__ClientPath=/app/client/PaCo-Setup.exe \
    PaCo__HttpsPort=8790 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Logging__Console__FormatterName=simple \
    Logging__Console__FormatterOptions__TimestampFormat="yyyy-MM-dd HH:mm:ss "
VOLUME /data
EXPOSE 8790
# Runs as the non-root user given in docker-compose (owner of the data directory).
# umask 077: database, keys and screenshots are readable only by the server's own user.
ENTRYPOINT ["/bin/sh", "-c", "umask 077 && exec dotnet PaCo.Server.dll \"$@\"", "paco"]

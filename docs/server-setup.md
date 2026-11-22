# Server einrichten

Der PaCo-Server läuft als Docker-Container auf einem Heimserver/NAS mit Linux. Er spricht selbst HTTPS, ein
Reverse-Proxy ist nicht nötig.

## Voraussetzungen

- Linux-Server mit Docker und Docker Compose
- Eine Adresse, unter der der Server aus dem Internet erreichbar ist, z. B. DynDNS wie DuckDNS
- Ein gültiges TLS-Zertifikat für diese Adresse als PEM-Dateien (Zertifikatskette und privater Schlüssel), z. B. von
  Let's Encrypt über `acme.sh` mit DNS-Challenge
- Ein freier TCP-Port, im Router an den Server weitergeleitet (Standard: `8790`)

> Ein Zertifikat gilt für den **Namen**, nicht für den Port. Hast du schon ein Zertifikat für deine Domain, z. B. für
> einen anderen Dienst, kann PaCo dieselben Dateien **nur lesend** mitbenutzen.

## 1. Code holen und konfigurieren

```bash
git clone <repo-url> paco && cd paco
cp deploy/.env.example deploy/.env
nano deploy/.env
```

| Variable | Bedeutung |
|---|---|
| `PACO_PUBLIC_URL` | Adresse, wie die Kinder-PCs den Server erreichen, z. B. `https://meinname.duckdns.org:8790` |
| `PACO_PORT` | Port auf dem Host (Router-Weiterleitung auf diesen Port) |
| `PACO_TLS_DIR` | Ordner mit den PEM-Dateien (wird read-only eingebunden) |
| `PACO_TLS_CERT` / `PACO_TLS_KEY` | Dateinamen von Zertifikatskette und Schlüssel in diesem Ordner |
| `PACO_DATA_DIR` | Datenordner (Datenbank, Signaturschlüssel). **Sichern!** |
| `PACO_UID` / `PACO_GID` | Benutzer, dem der Datenordner gehört und der die TLS-Dateien lesen darf (`id -u`, `id -g`) |
| `PACO_MASTER_KEY` | **Master-Schlüssel** (`openssl rand -base64 32`). Verschlüsselt alle gespeicherten Geheimnisse (2FA, Notfall-Codes, Familien-Schlüssel, Signaturschlüssel). Liegt bewusst **nicht** im Datenordner – eine Kopie des Datenordners allein ist dadurch wertlos. Zusätzlich im Passwort-Manager aufbewahren. |

```bash
mkdir -p /pfad/zu/paco-data        # muss PACO_UID gehören
```

## 2. Starten

```bash
docker compose -f deploy/docker-compose.yml up -d --build
docker logs -f paco
```

Der Build dauert beim ersten Mal einige Minuten. Er baut den Server **und** das Windows-Installationsprogramm.

Beim ersten Start erzeugt der Server den **Signaturschlüssel** `policy-signing-key.pem` im Datenordner. Alle Kinder-PCs
vertrauen genau diesem Schlüssel. Geht er verloren, müssen alle Geräte neu gekoppelt werden, deshalb gehört er ins
Backup (siehe [Betrieb](operations.md)).

## Push-Benachrichtigungen (ntfy)

Der Compose-Stack startet zusätzlich einen eigenen [ntfy](https://ntfy.sh)-Server (Container `paco-ntfy`). Einmalig:

```bash
# in deploy/.env: PACO_NTFY_PUBLIC_URL=https://<adresse>:8792, PACO_NTFY_DIR=/pfad/zu/paco-ntfy
docker compose -f deploy/docker-compose.yml up -d ntfy
docker exec -it paco-ntfy ntfy user add --role=admin paco     # Passwort vergeben (wird nicht weiter gebraucht)
docker exec paco-ntfy ntfy token add paco                     # Token → PACO_NTFY_TOKEN in deploy/.env
docker compose -f deploy/docker-compose.yml up -d
```

Anonyme Nutzer dürfen nur **lesen**, gesendet wird nur mit PaCos Token. Jede Familie bekommt ein geheimes, zufälliges
Thema. Die Eltern richten die App über **Handy-Benachrichtigungen** auf der Website ein (Anleitung mit QR-Code).

## 3. Router

Leite die TCP-Ports `PACO_PORT` (Website, Standard 8790) und `PACO_NTFY_PORT` (Push, Standard 8792) auf den Server weiter. Test von außen, z. B. vom Handy ohne WLAN:
`https://<adresse>:8790` muss die Anmeldeseite zeigen, mit gültigem Schloss-Symbol.

## 4. Super-Admin anlegen

```bash
docker exec -it paco dotnet PaCo.Server.dll admin-create du@example.org
```

Die Ausgabe enthält ein **vorläufiges Passwort**. Beim ersten Login musst du:

1. ein eigenes Passwort setzen (mindestens 12 Zeichen) und
2. eine Authenticator-App einrichten (Google Authenticator, Microsoft Authenticator, Aegis …) und die Notfall-Codes notieren.

Zugang verloren? `admin-reset` setzt Passwort und 2FA zurück:

```bash
docker exec -it paco dotnet PaCo.Server.dll admin-reset du@example.org
```

## 5. Eltern einladen

**Verwaltung → Eltern einladen**: Name, E-Mail und Familie (neu oder bestehend) eintragen und den erzeugten Link
z. B. per Messenger schicken. Der Link ist 7 Tage gültig und funktioniert nur einmal. Damit legen die Eltern ihr Passwort
fest und richten 2FA ein.

## Updates

```bash
git pull
docker compose -f deploy/docker-compose.yml up -d --build
```

Datenbank-Änderungen werden beim Start automatisch übernommen. Die Kinder-PCs aktualisierst du, indem du das neue
Installationsprogramm auf dem PC startet und **Aktualisieren** wählst (siehe [Kinder-PC](client-install.md)).

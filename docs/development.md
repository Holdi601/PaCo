# Entwicklung

## Voraussetzungen

- .NET SDK 10 (siehe `global.json`)
- Windows für den Client. Der Server läuft auch unter Linux und macOS.

## Bauen und testen

```bash
dotnet build PaCo.slnx
dotnet test
```

Die Tests umfassen:

- **Kern**: Zeitregeln (Kontingent, Zeitfenster, Zusatzzeit, Codes), Freischaltcodes (Manipulation, falscher Tag/Kind),
  Signaturprüfung, Brute-Force-Sperre
- **Server/API**: Kopplung (Einmal-Code), Kontenzuordnung, Sync mit Signaturprüfung, Ablehnung fremder Kinder und
  falscher Tokens, **Mandantentrennung**
- **Ende-zu-Ende Weboberfläche**: Admin-Login mit TOTP → Einladung → Passwort + 2FA → Kind anlegen → Regeln →
  Zusatzzeit, Freischaltcode, Sperre → alle Seiten. Dazu eine Kontosperre nach Fehlversuchen.

Für den Windows-Client gibt es zusätzlich einen VM-Test (Installation, Zeitablauf, Sperre, Abmeldung), siehe
`tests/e2e-vm/` (folgt).

## Server lokal starten

```bash
dotnet run --project src/PaCo.Server --launch-profile https
dotnet run --project src/PaCo.Server -- admin-create dev@example.org
```

Die Daten landen in `src/PaCo.Server/paco-data/` (git-ignoriert).

## Client

Eine einzige `PaCo.exe` mit mehreren Rollen:

| Aufruf | Rolle |
|---|---|
| `PaCo.exe` | Installationsassistent (Doppelklick, fordert Adminrechte an) |
| `PaCo.exe --service` | Windows-Dienst (LocalSystem) |
| `PaCo.exe --agent <pipe>` | Sitzungs-Agent, vom Dienst als SYSTEM in der Kinder-Sitzung gestartet |
| `PaCo.exe --uninstall` | Deinstallation |
| `PaCo.exe --install --server URL --code CODE --child kind1=Lena [--make-standard kind1]` | unbeaufsichtigte Installation |

Veröffentlichen als eigenständige Einzeldatei (enthält die .NET-Laufzeit):

```bash
dotnet publish src/PaCo.Client -c Release -o out/client
```

Der Docker-Build macht das automatisch und legt die Datei als `PaCo-Setup.exe` in das Server-Image. Beim Download
hängt der Server Serveradresse und Kopplungscode an die Datei an (`SetupPayload`).

## Datenbank-Migrationen

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/PaCo.Server --output-dir Data/Migrations
```

## Geheimnisse und Git

Nach dem Klonen einmal den Secret-Wächter aktivieren:

```bash
git config core.hooksPath .githooks
```

Der Hook `.githooks/pre-commit` blockiert Commits mit:

- Datenbanken, Schlüsseln, Zertifikaten, `.env`-Dateien, Exe-Dateien
- typischen Geheimnis-Mustern (Private Keys, GitHub-/AWS-Tokens, DuckDNS-Token, Passwort-Zuweisungen)
- den **Werten** von Umgebungsvariablen, deren Namen in `.git/secret-envvars.txt` stehen (z. B. Server-Passwörter)
- persönlichen Mustern aus `.git/secret-patterns.txt` (eigene Domain, Name, Heim-IP …)

Beide `.git/…`-Dateien liegen nur lokal und werden nie committet.

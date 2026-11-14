# PaCo – Kinderschutz für Windows, selbst gehostet

PaCo begrenzt die **Bildschirmzeit** von Kindern an Windows-PCs, filtert **Webseiten und Programme** und zeigt Eltern
**live**, wer gerade wie lange am PC ist. Der Server läuft auf dem eigenen Heimserver (Docker). Es gibt keine Cloud und
kein Abo, und die Kinderdaten verlassen das eigene Netz nicht.

> Entstanden als Ersatz für Microsoft Family Safety, das bei uns nach einigen Wochen immer wieder aufgehört hat,
> Zeitlimits durchzusetzen.

## Funktionen

**Für Eltern (Weboberfläche, auch am Handy)**
- Übersicht pro Kind: heute genutzt, Restzeit, *ist gerade am PC*, aktuelles Programm, Status je Gerät (online/offline)
- Ein Tipp für **+15 / +30 / +60 Minuten** oder „heute unbegrenzt“. Ist der PC online, wirkt das nach wenigen Sekunden.
- **Jetzt sperren** (einzeln oder alle Kinder)
- **Freischaltcodes** (z. B. „30 Minuten“). Sie funktionieren auch, wenn der Kinder-PC **offline** ist, und gelten nur
  für dieses Kind, nur heute und nur einmal.
- Wochenplan: Minuten pro Tag und erlaubter Zeitraum (z. B. 14:00–19:00), mit Kopieren auf andere Tage
- Bei Zeitablauf wahlweise **Sperrbildschirm** oder **sofortiges Abmelden**, jeweils vorher Ton aus
- Gesperrte Programme, gesperrte Webseiten (mit Vorlagen), SafeSearch, YouTube-Jugendschutz, kein privates Surfen,
  andere Browser sperren
- Statistik: 14-Tage-Verlauf, Programme pro Tag, Ereignisse (gesperrt, abgemeldet, falscher Code …)
- Warnungen: Kinderkonto mit Admin-Rechten, Gerät lange offline, Uhr verstellt, Deinstallation

**Für den Server-Betreiber (Super-Admin)**
- Familien anlegen, Eltern per **Einladungslink** einladen (7 Tage gültig, einmalig). Es gibt keine offene Registrierung.
- Konten sperren, Zugang zurücksetzen (Passwort vergessen / Handy verloren), Familien verwalten
- Der Admin sieht **keine** Kinderdaten. Jede Familie sieht nur ihre eigenen Kinder und Geräte.

**Auf dem Kinder-PC**
- Ein einziges Installationsprogramm mit eingebauter .NET-Laufzeit. Es braucht keine Vorinstallation (Windows 10/11, x64).
- Das Programm von der Website laden, im Admin-Konto starten und die Kinderkonten ankreuzen. Fertig.
- Warnungen 10 / 5 / 1 Minute vor Ablauf, Restzeit im Infobereich

## Aufbau

```
 Eltern (Browser) ──HTTPS + Login + 2FA──▶  PaCo-Server (ASP.NET Core, SQLite, Docker)
                                              │  signiert Regeln (ECDSA P-256)
 Kinder-PC                                    │
 ┌──────────────────────────────┐   HTTPS     │
 │ PaCo-Dienst (LocalSystem)    │◀───────────▶┘  Geräte-Token, alle 15–30 s
 │  • Zeit zählen & durchsetzen │
 │  • Regeln signiert gecacht   │  → funktioniert offline weiter
 │  • Browser-/App-Sperren      │
 ├──────────────────────────────┤
 │ Sitzungs-Agent (SYSTEM, in   │  Sperrbildschirm auf eigenem Desktop,
 │ der Kinder-Sitzung)          │  Ton aus, Warnungen, Programm-Statistik
 └──────────────────────────────┘
```

| Ordner | Inhalt |
|---|---|
| `src/PaCo.Shared` | Gemeinsamer Kern: Regeln, Zeitlogik, Signaturen, Freischaltcodes |
| `src/PaCo.Server` | Webserver, Eltern-Oberfläche, Geräte-API |
| `src/PaCo.Client` | Windows: Installer, Dienst, Sitzungs-Agent (eine einzige `PaCo.exe`) |
| `tests/PaCo.Tests` | Unit-, API- und Ende-zu-Ende-Tests der Weboberfläche |
| `deploy/` | Dockerfile, docker-compose, Beispiel-Konfiguration |
| `docs/` | Anleitungen und Sicherheitskonzept |

## Dokumentation

- [Server einrichten](docs/server-setup.md): Docker, HTTPS-Zertifikat, Router, erster Admin
- [Kinder-PC einrichten](docs/client-install.md): Anleitung für Eltern
- [Sicherheit](docs/security.md): Schutz gegen Manipulation, Grenzen, Bedrohungsmodell
- [Betrieb](docs/operations.md): Logs, Fehlersuche, Backup, Updates
- [Entwicklung](docs/development.md): Build, Tests, Architekturdetails

## Schnellstart (Server)

```bash
cp deploy/.env.example deploy/.env      # Adresse, Zertifikat, Datenordner eintragen
docker compose -f deploy/docker-compose.yml up -d --build
docker exec -it paco dotnet PaCo.Server.dll admin-create du@example.org
```

Danach `https://<deine-adresse>:8790` öffnen, mit dem angezeigten vorläufigen Passwort anmelden, ein eigenes Passwort
setzen und die Authenticator-App einrichten.

## Datenschutz

- Alle Daten liegen auf dem eigenen Server (SQLite-Datei im Datenordner).
- Gespeichert werden Nutzungszeiten pro Programm, Ereignisse und Regeln, **keine** Bildschirmfotos und **keine**
  Tastatureingaben.
- Das Repository enthält **keine** Zugangsdaten, Schlüssel oder Nutzerdaten. Ein Pre-Commit-Hook verhindert das
  (siehe [docs/development.md](docs/development.md)).

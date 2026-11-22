# Betrieb, Fehlersuche, Backup

## Logs

**Server**

```bash
docker logs --tail 200 -f paco
```

Protokolliert werden Anmeldungen (auch fehlgeschlagene, mit IP), Kontosperren, Einladungen, Kopplungen und Fehler.
Die Logs rotieren automatisch (5 × 10 MB).

**Kinder-PC**

`C:\ProgramData\PaCo\paco.log` (nur als Administrator lesbar, rotiert bei 2 MB). Darin steht unter anderem:

- Dienststart und Version, erkannte Kinder-Sitzungen, Agent-Start und -Verbindung
- jede Sperre, Freigabe, Abmeldung und Warnung mit Grund
- Sync-Fehler (Server nicht erreichbar …)
- übernommene oder abgelehnte Regeln

Wichtige Ereignisse (gesperrt, abgemeldet, falscher Code, Uhr verstellt, Agent-Fehler) werden zusätzlich an den Server
gemeldet. Sie erscheinen auf der Website unter **Statistik** des Kindes und als Warnung in der **Übersicht**.

**Auf der Website:**

- **Geräte → 📋 Protokoll** (Eltern): alle Ereignisse des PCs, filterbar nach *möglichen Umgehungsversuchen*
  (Uhr verstellt, falscher Code, gesperrtes Programm gestartet, abgesicherter Modus, Nutzung ohne Internet,
  Sitzungsprogramm beendet …) und *technischen Fehlern*. Mit **„Diagnose-Log abrufen“** lädt der PC die letzten
  Einträge seiner `paco.log` hoch (verschlüsselt gespeichert, die letzten 3 bleiben erhalten).
- **Verwaltung → 🩺 Geräte-Zustand** (Super-Admin): Version, letzter Kontakt und technische Fehler aller Geräte, ohne
  Kinderdaten.

Client-Fehler werden automatisch an den Server gemeldet, höchstens 20 pro Stunde und Gerät.

Dienststatus am PC (Admin-PowerShell):

```powershell
Get-Service PaCo
Get-Content C:\ProgramData\PaCo\paco.log -Tail 50
```

## Häufige Probleme

| Problem | Ursache / Lösung |
|---|---|
| Gerät „offline“ auf der Website | PC aus oder kein Internet. Im Log nach „Sync fehlgeschlagen“ suchen. Regeln gelten trotzdem weiter. |
| „+15 Min“ wirkt nicht | Der PC ist offline. Gib dem Kind stattdessen einen **Freischaltcode**. |
| Kind wird nicht überwacht | Website → Geräte: Ist das Windows-Konto dem Kind zugeordnet? |
| Warnung „Administratorrechte“ | Das Kinderkonto in ein Standardkonto umwandeln (Einstellungen → Konten → Familie & andere Benutzer). |
| Kopplungscode ungültig | Er ist 24 h gültig und nur einmal verwendbar. Auf der Website ein neues Installationsprogramm laden. |
| SmartScreen-Warnung beim Setup | Das Programm ist nicht signiert: *Weitere Informationen → Trotzdem ausführen*. |

## Client-Updates

1. **Neue Version veröffentlichen** (Entwickler): `<Version>` in `Directory.Build.props` erhöhen, committen,
   Tag setzen und pushen (`git tag v1.2.0 && git push --tags`). GitHub Actions testet, baut `PaCo-Setup.exe` und
   legt sie zusammen mit der SHA-256-Prüfsumme als Release an.
   Danach auf dem Entwickler-PC **signieren**: `dotnet run tools/release/sign.cs -- release 1.2.0`. Der private
   Schlüssel liegt nur dort (verschlüsselt für den Windows-Benutzer); der Server übernimmt nur signierte Releases –
   ein gekapertes GitHub-Konto allein kann keine Software auf die Kinder-PCs bringen.
2. **Auf dem Server holen** (Super-Admin): *Verwaltung → Auf GitHub nach Updates suchen*. Der Server lädt das
   Release, prüft die Prüfsumme und bietet die Version ab sofort an, auch für neue Installationen.
3. **Verteilen**: *Verwaltung → Alle Geräte aktualisieren* oder pro Gerät durch die Eltern unter
   *Geräte → Update installieren*.

Sicherheit: Die Kinder-PCs laden Updates **nur von deinem Server**. Jedes Update kommt mit einem Manifest
(Version, SHA-256, Größe), das der Server mit seinem Signaturschlüssel signiert. Der PC prüft Signatur und Prüfsumme,
ersetzt das Programm atomar und startet den Dienst neu. Startet die neue Version nicht, wird automatisch die
vorherige wiederhergestellt.

## Backup

`deploy/backup/backup.sh` sichert jede Nacht die **komplette Installation** verschlüsselt auf eine SMB-Freigabe
(z. B. ein zweites NAS): das PaCo-Verzeichnis inkl. `deploy/.env` (alle Zugangsdaten und der Master-Schlüssel), den
Datenordner (Datenbank – im laufenden Betrieb konsistent kopiert –, Schlüsselbund, verschlüsselter Signaturschlüssel,
Bildschirmfotos) und die ntfy-Daten.

- Verschlüsselung: AES-256 (GPG, symmetrisch) mit dem **Backup-Schlüssel** `~/.paco-backup-key`. Dieser Schlüssel
  wird **nie** mit auf die Freigabe kopiert. **Im Passwort-Manager aufbewahren** – ohne ihn lässt sich kein Backup
  wiederherstellen. Auch eine offene Gast-Freigabe liest damit nur unlesbare Daten.
- Einstellungen in `~/.paco-backup.conf`, z. B. `SMB_SHARE=//nas2/backup`, `SMB_DIR=PaCo-Backup`, `KEEP_DAYS=14`.
- Die SMB-Übertragung läuft in einem kleinen Docker-Image (`deploy/backup/Dockerfile`), auf dem Host wird nichts installiert.
- Zeitplan per `crontab -e`: `30 3 * * * /home/<user>/paco/deploy/backup/backup.sh >> /home/<user>/paco-backup.log 2>&1`

## Wiederherstellen

```bash
# Backup-Datei von der Freigabe holen, dann:
gpg --batch --pinentry-mode loopback --passphrase-file ~/.paco-backup-key -d paco-2026-11-22_0330.tar.gz.gpg | tar -xzf -
# Ergebnis: paco/ (Code + deploy/.env), paco-data/, paco-ntfy/ – an ihren Platz legen, dann:
cd paco && docker compose -f deploy/docker-compose.yml up -d --build
```

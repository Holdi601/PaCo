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

## Backup

Der Datenordner (`PACO_DATA_DIR`) enthält alles:

| Datei | Inhalt | Verlust bedeutet |
|---|---|---|
| `paco.db` | Konten, Familien, Kinder, Regeln, Statistik | alles neu anlegen |
| `policy-signing-key.pem` | Signaturschlüssel | **alle PCs neu koppeln** |
| `dp-keys/` | Schlüssel für Cookies und Einladungslinks | alle müssen sich neu anmelden |

Konsistentes Backup im laufenden Betrieb:

```bash
sqlite3 /pfad/zu/paco-data/paco.db ".backup '/backup/paco-$(date +%F).db'"
cp -a /pfad/zu/paco-data/policy-signing-key.pem /pfad/zu/paco-data/dp-keys /backup/
```

> Backups enthalten personenbezogene Daten und den Signaturschlüssel. **Niemals in Git** oder in öffentliche Clouds
> legen, nur verschlüsselt oder auf eigenen Datenträgern.

## Wiederherstellen

Container stoppen, die Dateien zurück in den Datenordner kopieren und den Container starten.

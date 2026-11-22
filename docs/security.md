# Sicherheitskonzept

PaCo geht von zwei Gegnern aus: **Angreifern aus dem Internet** (der Server ist öffentlich erreichbar) und
**dem Kind am PC** (Standard-Windows-Konto, technisch neugierig, physischer Zugriff auf den PC).

## Server

| Maßnahme | Umsetzung |
|---|---|
| Transport | Nur HTTPS (eigenes Kestrel-TLS mit Let's-Encrypt-Zertifikat, automatisches Nachladen bei Erneuerung), HSTS |
| Anmeldung | ASP.NET Core Identity, PBKDF2-Passworthashes, Passwort ≥ 12 Zeichen |
| 2FA | TOTP (RFC 6238, z. B. Google Authenticator), **Pflicht** für alle Konten, 8 Notfall-Codes |
| Brute-Force | Kontosperre nach 5 Fehlversuchen (15 min), IP-Rate-Limit auf Anmeldeseiten und Geräte-API |
| Konten | Keine Selbstregistrierung. Konten entstehen nur per CLI (Super-Admin) oder Einladungslink (7 Tage, einmalig). |
| Sitzungen | Cookies `__Host-`, `Secure`, `HttpOnly`, `SameSite=Strict`; Anti-Forgery-Token für alle Formulare; „überall abmelden“ |
| Mandantentrennung | Globaler EF-Core-Abfragefilter auf `FamilyId` für **jede** Familien-Tabelle (Standard: nichts sichtbar), zusätzlich Schreibschutz im `SaveChanges`, mit Tests |
| Browser-Schutz | Strenge Content-Security-Policy (keine Inline-Skripte), `X-Frame-Options: DENY`, `nosniff`, `no-referrer` |
| Geräte | Eigenes zufälliges Token pro Gerät (256 Bit), nur der SHA-256-Hash wird gespeichert. Kopplungscode einmalig, 24 h gültig. |
| Container | Läuft als Nicht-Root, read-only Dateisystem, `no-new-privileges`, alle Capabilities entfernt |

## Alarme, Bildschirmfotos, Push

| Thema | Umsetzung |
|---|---|
| Datensparsamkeit | Stichwörter werden **auf dem PC** geprüft. Zum Server gehen nur Treffer mit max. 300 Zeichen Ausschnitt, nie ganze Chats. Es gibt keinen Keylogger. |
| Bildschirmfotos | Nur auf ausdrücklichen Klick der Eltern. Sie werden mit ASP.NET Data Protection **verschlüsselt** außerhalb von `wwwroot` gespeichert und nach 7 Tagen gelöscht. Der Abruf läuft nur über eine angemeldete Seite mit Familienfilter: Eine fremde oder erratene ID ergibt „nicht gefunden“. |
| IDs | Zufällige GUIDs (122 Bit). Selbst eine bekannte ID gibt keinen Zugriff (Familienfilter, getestet). |
| Upload | Geräte können nur zu offenen Anfragen der eigenen Familie für ein eigenes Kind hochladen, nur JPEG, max. 6 MB. |
| Push | Eigener ntfy-Server: anonym nur Lesen, Senden nur mit PaCos Token. Das Thema pro Familie ist geheim (128 Bit) und kann rotiert werden. iPhone-Weckrufe über ntfy.sh enthalten keinen Inhalt. |

## Kinder-PC

| Angriff durch das Kind | Gegenmaßnahme |
|---|---|
| Programm beenden (Task-Manager) | Dienst und Sitzungs-Agent laufen als **SYSTEM**; ein Standardkonto kann sie nicht beenden. Der Dienst startet nach Absturz automatisch neu. |
| Dienst stoppen / deinstallieren | Erfordert Administratorrechte. Bei einer Deinstallation werden die Eltern informiert. Meldet sich ein PC gar nicht mehr (z. B. PaCo abgeschaltet), bekommen die Eltern nach einer einstellbaren Zahl von Tagen eine Push-Nachricht (Standard 7, pro Gerät), und eine weitere, sobald er wieder da ist. |
| Regeldatei bearbeiten | Ordner `C:\ProgramData\PaCo` ist nur für SYSTEM/Administratoren lesbar. Regeln sind zusätzlich **vom Server signiert** (ECDSA P-256); manipulierte oder fremde Regeln werden abgelehnt. |
| Gerätedaten kopieren | `device.bin` ist mit DPAPI an diesen PC gebunden verschlüsselt. |
| Internet trennen | Regeln gelten **offline** weiter. Die Zeit wird lokal gezählt und später nachgemeldet. Offline gibt es **keine** zusätzliche Zeit, außer über einen Freischaltcode der Eltern. |
| Uhr / Zeitzone verstellen | Standardkonten dürfen die Uhr nicht stellen. Die Zeitzone wird ignoriert (es gilt die Zeitzone der Familie). Zurückgestellte Uhren werden gemeldet; online wird die Serverzeit verwendet. |
| Falscher Server (DNS-/hosts-Umleitung, eigener Proxy, „Platzhalter-Server“ mit mehr Zeit) | Die Verbindung ist TLS mit normaler Zertifikatsprüfung; die Serveradresse liegt geschützt in `C:\ProgramData\PaCo`, die hosts-Datei ist nur für Admins änderbar. Zusätzlich gilt: Regeln, Zusatzzeit und Updates sind mit dem beim Koppeln gespeicherten **Server-Schlüssel signiert**, und auch die **Serverzeit** zählt nur signiert, gebunden an Gerät und eine Zufallszahl (Nonce) der jeweiligen Anfrage – ein falscher Server kann weder Regeln noch Zeit fälschen oder alte Antworten wiederholen. Unsignierte Zeit wird verworfen und den Eltern gemeldet. |
| Freischaltcode raten | 100.000 Möglichkeiten, nach 5 Fehlversuchen exponentielle Sperre bis 60 min. Codes gelten nur für ein Kind, einen Tag und einmal. |
| Sperrbildschirm umgehen (Alt+Tab, Win-Taste, Task-Manager) | Der Sperrbildschirm läuft auf einem **eigenen Windows-Desktop**. Die Programme des Kindes sind dort nicht erreichbar, und ein Wächter schaltet sofort zurück. |
| Sitzungs-Agent sabotieren | Lässt sich der Sperrbildschirm nicht anzeigen, wird das Kind **abgemeldet** (fail closed). |
| Abgesicherter Modus (z. B. Umschalt + Neustart → Problembehandlung → Starteinstellungen, geht ohne Admin-Passwort) | Der Dienst ist für den abgesicherten Modus (mit und ohne Netzwerk) registriert und sperrt dort genauso – in der Test-VM geprüft. Den Eltern wird gemeldet, dass der PC im abgesicherten Modus gestartet wurde. |
| Systemwerkzeuge (Registry-Editor, Eingabeaufforderung, PowerShell, Skript-Hosts, winget) | Windows-Benutzerrichtlinien im Konto des Kindes (wie im Gruppenrichtlinien-Editor): `DisableRegistryTools`, `DisableCMD` (Batchdateien laufen weiter), PowerShell-Skripte aus, Windows Script Host aus, dazu `DisallowRun` mit allen gesperrten Programmen – Windows verweigert den Start mit einer Meldung. Was trotzdem startet, beendet PaCo. Jedes Werkzeug ist auf der Website einzeln sperr- und wieder freigebbar; die passende Richtlinie folgt automatisch. Die Schlüssel sind für das Kind nur lesbar; PaCo entfernt genau seine Werte wieder, wenn das Konto kein Kinderkonto mehr ist oder PaCo deinstalliert wird. |
| Apps selbst installieren (Microsoft Store, winget) | Der Store und winget sind standardmäßig gesperrt (auf der Website pro Kind abschaltbar). Die Windows-Richtlinie „Store deaktivieren“ wirkt nur in Enterprise/Education, deshalb sperrt PaCo die Store-App selbst. |
| Gesperrtes Programm umbenennen | Zusätzlich wird der Original-Dateiname aus den Versionsinformationen der Exe geprüft. |
| Anderer Browser / Inkognito | Browser-Richtlinien für Edge, Chrome und Firefox (Sperrlisten, SafeSearch, kein InPrivate). Andere Browser können gesperrt werden. Die Registry-Schlüssel sind für das Kind nur lesbar. |
| Mehrere Konten / Benutzerwechsel | Jede Sitzung wird einzeln überwacht. Neue Windows-Konten erscheinen auf der Website. |

## Sicherheitsprüfung (November 2026)

Ein Pentest des Servers (Website, Konten, Familien-Trennung, Geräte-Schnittstelle) und eine Härtungs-Prüfung des
Windows-Clients ergaben keine kritischen Lücken; Familien-Trennung, CSRF-Schutz, XSS-Schutz und Sicherheits-Header hielten
allen Tests stand. Behoben wurden u. a.:

- Native Bibliotheken des Clients werden im geschützten `C:\ProgramData\PaCo\bundle` entpackt statt im für alle
  beschreibbaren `C:\Windows\Temp` (vorher wäre DLL-Unterschieben in einen SYSTEM-Prozess denkbar gewesen).
- Sperrbildschirm auf einem Desktop mit zufälligem Namen, nur für SYSTEM zugänglich; der Dienst verlangt eine Bestätigung,
  sonst wird das Kind abgemeldet. Bricht die Verbindung während der Sperre ab, wird ebenfalls abgemeldet.
- Datenordner: wird bei der Erstinstallation neu angelegt und bei jedem Start bereinigt (fremde Besitzer, eigene
  Rechte, Verknüpfungen). Updates: kein Folgen von Verknüpfungen, begrenzte Größe, Datei bis zum Start gesperrt.
- Signierte Dokumente haben einen Typ (Regeln, Update, Uhrzeit) und können nicht gegeneinander getauscht werden.
- Freischaltcodes: Schlüssel pro Kind statt pro Familie; ein PC erhält nur die Schlüssel seiner Kinder.
- Server: Vorlagen von Geräten werden nur als bekannte Texte mit einfachen Platzhaltern angenommen (vorher konnten sie
  Seiten lahmlegen), Mengenbegrenzung und Aufbewahrungsfristen für Gerätedaten, Authenticator-Codes nur einmal gültig,
  Sitzungen serverseitig (Abmelden beendet sie wirklich), keine Rückschlüsse auf existierende Konten, Push an die Familie,
  wenn der Server-Betreiber jemanden einlädt oder einen Zugangslink erstellt, Geräte-Authentifizierung vor dem Lesen von Daten.
- Regelmäßig: CodeQL-Analyse und Prüfung auf verwundbare Pakete (GitHub Actions, wöchentlich), Dependabot.

Ebenfalls umgesetzt (November 2026):

- **Release-Signatur** mit einem Offline-Schlüssel (nur auf dem Entwickler-PC); der Server übernimmt nur signierte Releases.
- **Verschlüsselte Geheimnisse**: 2FA-Schlüssel, Notfall-Codes, Familien-Schlüssel und Signaturschlüssel liegen nur
  verschlüsselt vor; der Schlüsselbund ist mit dem Master-Schlüssel aus `.env` verschlüsselt (nicht im Datenordner).
  Passwörter sind ohnehin nur als nicht umkehrbarer Hash gespeichert – auch der Server-Betreiber kann sie nicht lesen.
- **Erneute Passwort-Abfrage** vor neuen Notfall-Codes oder dem Neu-Einrichten der Authenticator-App.
- Der Super-Admin kann **nur das Passwort**, **nur die 2FA** oder **beides** zurücksetzen. Ein Passwort-Link öffnet
  das Konto nie allein (die 2FA bleibt nötig); die Familie bekommt jeweils eine Push-Nachricht.
- **Verschlüsselte nächtliche Sicherung** auf ein zweites NAS (siehe docs/operations.md).

## Bekannte Grenzen

- **Physischer Zugriff mit Admin- oder BIOS-Kenntnissen** (Booten von USB, BIOS-Uhr vorstellen) kann jede
  Softwarelösung aushebeln. Deshalb gehören ein BIOS-Passwort und ein geheimes Admin-Passwort dazu.
- **Lernzeit** erkennt Lern-Apps am Programmnamen und an der Programmbeschreibung, Lern-Webseiten an der Adresszeile des
  Browsers. Wer die Programmbeschreibung eines Spiels gezielt umschreibt, könnte es als Lern-App tarnen.
- **Mehrere PCs offline gleichzeitig**: Jeder PC kennt die Nutzung der anderen nur bis zum letzten Kontakt.
- **Webfilter** wirken in Edge, Chrome und Firefox. Für einen netzwerkweiten Filter (alle Geräte, auch Handys) kann man
  zusätzlich einen DNS-Filter wie AdGuard Home betreiben.
- **Bewusst nicht gesperrt**, damit der PC nutzbar bleibt: Task-Manager, Einstellungen/Systemsteuerung, Ausführen-Dialog und Programme im Benutzerordner (AppLocker/Softwareeinschränkung würde Spiele wie Roblox, Minecraft oder Discord blockieren). Ein Kind kann mit dem Task-Manager nur eigene Programme beenden, nicht PaCo.
- Das Installationsprogramm ist nicht code-signiert, deshalb zeigt Windows SmartScreen eine Warnung.

## Sicherheitslücke melden

Bitte nicht öffentlich als Issue, sondern direkt an den Betreiber des Repositories.

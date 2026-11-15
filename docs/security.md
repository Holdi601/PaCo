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
| Dienst stoppen / deinstallieren | Erfordert Administratorrechte. Bei einer Deinstallation werden die Eltern informiert. |
| Regeldatei bearbeiten | Ordner `C:\ProgramData\PaCo` ist nur für SYSTEM/Administratoren lesbar. Regeln sind zusätzlich **vom Server signiert** (ECDSA P-256); manipulierte oder fremde Regeln werden abgelehnt. |
| Gerätedaten kopieren | `device.bin` ist mit DPAPI an diesen PC gebunden verschlüsselt. |
| Internet trennen | Regeln gelten **offline** weiter. Die Zeit wird lokal gezählt und später nachgemeldet. Offline gibt es **keine** zusätzliche Zeit, außer über einen Freischaltcode der Eltern. |
| Uhr / Zeitzone verstellen | Standardkonten dürfen die Uhr nicht stellen. Die Zeitzone wird ignoriert (es gilt die Zeitzone der Familie). Zurückgestellte Uhren werden gemeldet; online wird die Serverzeit verwendet. |
| Falscher Server (DNS-/hosts-Umleitung, eigener Proxy, „Platzhalter-Server“ mit mehr Zeit) | Die Verbindung ist TLS mit normaler Zertifikatsprüfung; die Serveradresse liegt geschützt in `C:\ProgramData\PaCo`, die hosts-Datei ist nur für Admins änderbar. Zusätzlich gilt: Regeln, Zusatzzeit und Updates sind mit dem beim Koppeln gespeicherten **Server-Schlüssel signiert**, und auch die **Serverzeit** zählt nur signiert, gebunden an Gerät und eine Zufallszahl (Nonce) der jeweiligen Anfrage – ein falscher Server kann weder Regeln noch Zeit fälschen oder alte Antworten wiederholen. Unsignierte Zeit wird verworfen und den Eltern gemeldet. |
| Freischaltcode raten | 100.000 Möglichkeiten, nach 5 Fehlversuchen exponentielle Sperre bis 60 min. Codes gelten nur für ein Kind, einen Tag und einmal. |
| Sperrbildschirm umgehen (Alt+Tab, Win-Taste, Task-Manager) | Der Sperrbildschirm läuft auf einem **eigenen Windows-Desktop**. Die Programme des Kindes sind dort nicht erreichbar, und ein Wächter schaltet sofort zurück. |
| Sitzungs-Agent sabotieren | Lässt sich der Sperrbildschirm nicht anzeigen, wird das Kind **abgemeldet** (fail closed). |
| Abgesicherter Modus | Der Dienst ist für den abgesicherten Modus registriert. |
| Gesperrtes Programm umbenennen | Zusätzlich wird der Original-Dateiname aus den Versionsinformationen der Exe geprüft. |
| Anderer Browser / Inkognito | Browser-Richtlinien für Edge, Chrome und Firefox (Sperrlisten, SafeSearch, kein InPrivate). Andere Browser können gesperrt werden. Die Registry-Schlüssel sind für das Kind nur lesbar. |
| Mehrere Konten / Benutzerwechsel | Jede Sitzung wird einzeln überwacht. Neue Windows-Konten erscheinen auf der Website. |

## Bekannte Grenzen

- **Physischer Zugriff mit Admin- oder BIOS-Kenntnissen** (Booten von USB, BIOS-Uhr vorstellen) kann jede
  Softwarelösung aushebeln. Deshalb gehören ein BIOS-Passwort und ein geheimes Admin-Passwort dazu.
- **Mehrere PCs offline gleichzeitig**: Jeder PC kennt die Nutzung der anderen nur bis zum letzten Kontakt.
- **Webfilter** wirken in Edge, Chrome und Firefox. Für einen netzwerkweiten Filter (alle Geräte, auch Handys) kann man
  zusätzlich einen DNS-Filter wie AdGuard Home betreiben.
- Das Installationsprogramm ist nicht code-signiert, deshalb zeigt Windows SmartScreen eine Warnung.

## Sicherheitslücke melden

Bitte nicht öffentlich als Issue, sondern direkt an den Betreiber des Repositories.

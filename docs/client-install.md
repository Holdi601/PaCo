# Kinder-PC einrichten (Anleitung für Eltern)

Dauer: ca. 5 Minuten. Du brauchst den PC des Kindes und das **Administrator-Konto** auf diesem PC.

## Vorher prüfen

- **Das Kind hat ein eigenes Windows-Konto**, das **kein Administrator** ist. Im Installationsprogramm kannst du ein
  Admin-Kinderkonto mit einem Haken in ein Standardkonto umwandeln.
- Du kennst das Passwort eines **Administrator-Kontos** auf dem PC (z. B. dein eigenes Konto). Das Kind darf es nicht kennen.
- Empfohlen: In den Windows-Einstellungen die Microsoft-Family-Einschränkungen für das Kind entfernen, damit sich die
  beiden Systeme nicht in die Quere kommen.

## Installation

1. Auf dem Kinder-PC mit dem **Administrator-Konto** anmelden, nicht mit dem Konto des Kindes.
2. Die PaCo-Website öffnen → **Geräte** → **Installationsprogramm herunterladen**.
   Die Datei enthält schon die Serveradresse und einen einmaligen Kopplungscode (24 Stunden gültig).
3. `PaCo-Setup.exe` starten und die Windows-Frage „Änderungen zulassen?“ mit **Ja** beantworten.
   Falls „Der Computer wurde durch Windows geschützt“ erscheint: **Weitere Informationen → Trotzdem ausführen**.
4. **Verbinden** klicken.
5. Für jedes Windows-Konto auswählen, zu welchem Kind es gehört, oder „kein Kind“ für Erwachsene.
6. **Sicherheits-Check**: PaCo prüft, ob das Kinderkonto ein Standardkonto ist, ob Admin-Konten ein Passwort haben,
   ob Virenschutz, Firewall und SmartScreen laufen usw. Mit dem Haken bei „Empfohlene Korrekturen automatisch
   anwenden“ behebt PaCo, was es selbst beheben kann. **Rote Punkte ohne Automatik bitte selbst erledigen**, vor
   allem ein Passwort für jedes Admin-Konto.
7. **Installieren**. Fertig.

Danach auf der Website unter **Handy-Benachrichtigungen** die ntfy-App einrichten, damit Alarme sofort aufs Handy kommen.

Die Regeln gelten sofort für alle angemeldeten Kinder und ab der nächsten Anmeldung für alle anderen.

## Was das Kind sieht

- Ein Schild-Symbol im Infobereich unten rechts. Mit der Maus darüber zeigt es die Restzeit.
- Hinweise 10, 5 und 1 Minute vor Ablauf.
- Bei Zeitablauf, je nach Einstellung:
  - **Sperrbildschirm**: Der Ton geht aus und ein schwarzer Bildschirm erscheint. Weiter geht es nur mit einem
    Freischaltcode der Eltern oder mit Zusatzzeit über die Website. Das Kind kann sich dort auch abmelden.
  - **Abmelden**: Der Ton geht aus und das Kind wird abgemeldet. Meldet es sich wieder an, hat es 60 Sekunden Zeit,
    einen Freischaltcode einzugeben.

## Freischaltcode

Auf der Website bei dem Kind: **Freischaltcode → Dauer wählen → Erzeugen**. Den 10-stelligen Code sagt man dem Kind,
z. B. am Telefon. Er gilt nur heute, nur für dieses Kind und nur einmal, und er funktioniert **auch ohne Internet** auf
dem PC. Nach 5 falschen Eingaben wird die Eingabe für einige Minuten gesperrt.

## Aktualisieren / Entfernen

- **Aktualisieren**: Das neue Installationsprogramm im Admin-Konto starten und **Aktualisieren** wählen. Alle
  Einstellungen bleiben erhalten.
- **Entfernen**: Zuerst auf der Website **Geräte → Gerät entfernen** wählen. Danach am PC im Admin-Konto unter
  *Einstellungen → Apps → PaCo Kinderschutz → Deinstallieren* entfernen.

## Tipps für mehr Sicherheit

- Ein **BIOS/UEFI-Passwort** setzen und das Booten von USB deaktivieren. Sonst kann ein technisch versiertes Kind die
  Uhr im BIOS verstellen oder ein anderes System starten.
- Kein Kind kennt das Passwort des Administrator-Kontos.
- Auf der Website unter **Regeln → Internet** die Option „Andere Browser sperren“ einschalten.

# ETS2 Verkehrsradar (Prototyp)

*English version: [README.md](README.md)*

Zeigt die KI-Fahrzeuge rund um den eigenen LKW als Pfeile auf einer Web-Seite – gedacht als
Entscheidungshilfe vor dem Einfahren in Kreuzungen. Anzeige im Browser auf dem 2. Monitor oder
auf einem Tablet/Handy im selben Netz. Der Server startet und endet automatisch mit dem Spiel.

```
ETS2 ─┬─ ets2la_plugin.dll  ──(Shared Memory: Verkehr, Ampeln, LKW-Position)──┐
      └─ radar_launcher.dll ──(Shared Memory: exakte Geschwindigkeit)─────────┤
                │ startet/beendet                                             ▼
                └──────────────────────────> traffic_radar\TrafficRadar.exe (Port 8350) ──HTTP──> Browser
```

| Teil | Ordner | Aufgabe |
|---|---|---|
| ETS2LA-Plugin | `vendor/ets2la-plugin` | liest Verkehr und Ampeln aus dem Spielspeicher (fremdes Projekt, MIT, https://github.com/ETS2LA/plugin) |
| Starter-DLL | `launcher/` | startet den Server versteckt mit dem Spiel, beendet ihn mit dem Spiel (auch bei Absturz, per Job-Objekt), liefert die exakte Geschwindigkeit aus dem offiziellen Telemetry SDK |
| Server | `server/` | liest das Shared Memory, streamt 20×/s an alle Browser; Webseite ist in die EXE eingebettet |

Die Starter-DLL meldet außerdem, ob die Simulation läuft (SDK-Ereignisse `started`/`paused`). Im Menü,
beim Laden und in der Pause zeigt die Seite „Radar wartet“; die ETS2LA-Daten werden erst bei laufender
Fahrt geöffnet (früher geöffnet blieben sie leer). Als Sicherung öffnet der Server sie alle 3 s neu,
solange keine gültigen Live-Daten kommen.

Fehlt die Starter-DLL, schätzt der Server die Geschwindigkeit aus der Positionsänderung.

**Browser:** Hat sich 30 s nach dem Laden der Plugins keine Radar-Seite verbunden (offener Tab, Tablet),
öffnet die Starter-DLL `http://localhost:8350/` im Standardbrowser. Ein schon offener Tab verbindet sich
von selbst neu (in Tests nach 10–15 s), dann kommt kein zweiter dazu. Abschalten: `open_browser=0` in
`plugins\radar_launcher.ini`.

## Anzeige

| Farbe | Bedeutung |
|---|---|
| Blau | eigener LKW |
| Rot | fährt und kommt näher (näher als 70 m) |
| Gelb | fährt |
| Grau | steht |
| Punkte | Ampeln (rot / gelb / grün) |

Die gestrichelte Linie zeigt, wo das Fahrzeug in 3 Sekunden ist. Anhänger werden mitgezeichnet.
Oben rechts zeigt ein Kompass die Fahrtrichtung (z. B. „NO 47°“): Die Rose steht fest (N oben),
die Nadel zeigt die Fahrtrichtung – in beiden Ausrichtungen des Radars.

Kompass und „Norden oben“ nutzen den **geografischen** Norden wie das Navi im Spiel. Die Spielwelt ist
eine flache Lambert-Kegelprojektion (Parameter aus `def/climate.sii`, Umrechnung nach TruckSim Maps,
siehe `server/Ets2Projection.cs`); ihre „Oben“-Achse weicht abseits von 15° Ost vom echten Norden ab –
in Island um gut 20°. Island fällt dabei unter die UK-Maßstabsregel von TruckSim Maps; das passte im
Test besser zum Navi (273° statt 267° bei „ziemlich genau West“), ist aber nicht sicher belegt.

Knöpfe: Sichtbereich 50/100/120/150 m · Fahrtrichtung oben / Norden oben · Aktivierungsgrenze
30/40/50/60/70/80 km/h · „Immer“ (unabhängig vom Tempo) · Vollbild. Einstellungen merkt sich jeder Browser
selbst. Über der Grenze blendet sich das Radar aus („Radar pausiert“).

Adresse: am PC `http://localhost:8350/`, am Tablet `http://<PC-IP>:8350/`.
Am PC zeigt die Seite neben den Knöpfen einen QR-Code mit dieser Adresse (Antippen vergrößert) – mit der
Handy-/Tablet-Kamera scannen. Als Adresse nimmt der Server die Netzwerkkarte mit Standard-Gateway
(VirtualBox-/Hyper-V-Adapter fallen so heraus). Auf Touch-Geräten wird der QR-Code nicht angezeigt.
QR-Erzeugung: Bibliothek QRCoder (MIT).
Ohne laufendes Spiel ist die Seite nicht erreichbar; sie verbindet sich von selbst, sobald ETS2 läuft.

## Installation im Spiel

Ziel: `E:\SteamLibrary\steamapps\common\Euro Truck Simulator 2\bin\win_x64\plugins\`

```
plugins\
  ets2la_plugin.dll
  radar_launcher.dll
  radar_launcher.ini     (aus launcher\, optional)
  traffic_radar\
    TrafficRadar.exe     (aus dist\traffic_radar)
    server.log           (wird beim Start geschrieben)
```

Benötigt die .NET-10-Laufzeit (ist installiert).

### Firewall (einmalig, als Administrator)

Der Server läuft ohne Fenster – eine Windows-Firewall-Abfrage würde unsichtbar bleiben und Windows
legt dann eine **Blockier**-Regel an. Deshalb vorab eine Erlaubnis-Regel anlegen:

```powershell
netsh advfirewall firewall add rule name="ETS2 Verkehrsradar" dir=in action=allow profile=private program="E:\SteamLibrary\steamapps\common\Euro Truck Simulator 2\bin\win_x64\plugins\traffic_radar\TrafficRadar.exe"
```

Blockier-Regeln haben Vorrang vor Erlaubnis-Regeln. Wenn das Tablet nicht verbinden kann:
„Windows Defender Firewall mit erweiterter Sicherheit“ → Eingehende Regeln → Regeln namens
„TrafficRadar“ mit Aktion *Blockieren* löschen.

## Bauen

Benötigt Visual Studio mit „Desktopentwicklung mit C++“ und das .NET-10-SDK.

**ETS2LA-Plugin** (nach Spiel-Updates neu bauen – es lädt nur bei passender Spielversion, aktuell 1.62.x):

```powershell
cd C:\work\Claude\ets2-traffic-radar\vendor\ets2la-plugin
git pull; git submodule update --init --depth 1
cmake -S . -B build -A x64
cmake --build build --config Release      # -> build\Release\ets2la_plugin.dll
```

**Starter-DLL** (nutzt nur die offizielle SDK-Schnittstelle, übersteht normalerweise Spiel-Updates):

```powershell
cd C:\work\Claude\ets2-traffic-radar\launcher
cmake -S . -B build -A x64
cmake --build build --config Release      # -> build\Release\radar_launcher.dll
```

**Server:**

```powershell
cd C:\work\Claude\ets2-traffic-radar
dotnet publish server -c Release -o dist\traffic_radar   # -> dist\traffic_radar\TrafficRadar.exe
```

Die cmake-Befehle im *Developer PowerShell for VS* ausführen. Spiel vor dem Kopieren beenden.
Läuft das Spiel noch, lässt sich die gesperrte `TrafficRadar.exe` umbenennen (z. B. in `TrafficRadar.old.exe`)
und die neue daneben kopieren – sie gilt dann ab dem nächsten Spielstart; die alte danach löschen.

Ohne Spiel testen: `dist\traffic_radar\TrafficRadar.exe --demo` (simulierte Kreuzung).
Weitere Optionen: `--port 9000`, `--log datei.log`.
`launcher\test\test_host.exe <dll> <sekunden> [crash]` simuliert das Spiel für die Starter-DLL.

Logs: `Dokumente\Euro Truck Simulator 2\game.log.txt` (Zeilen `[ets2la_plugin]`, `[radar_launcher]`)
und `plugins\traffic_radar\server.log`.

## Grenzen des Prototyps

- Gedacht für Singleplayer/Konvoi. TruckersMP-Fahrzeuge liefert das Plugin ohne Tempo – sie würden grau („steht“) erscheinen; in TruckersMP ungetestet.
- Keine Straßen auf dem Radar – nur Fahrzeuge und Ampeln.
- Bildschirm-Wachhalten am Tablet funktioniert über `http://` nicht (Browser-Beschränkung) – ggf. die
  Bildschirm-Zeitsperre am Tablet hochsetzen.

## Lizenz

MIT, siehe [LICENSE](LICENSE). Das ETS2LA-Plugin in `vendor/ets2la-plugin` und die Bibliothek QRCoder stehen unter ihrer eigenen MIT-Lizenz.

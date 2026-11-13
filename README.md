# ETS2 Traffic Radar (prototype)

*Deutsche Version: [README.de.md](README.de.md)*

Shows the AI vehicles around your own truck as arrows on a web page – meant as a decision aid before
pulling into junctions. View it in a browser on a second monitor or on a tablet/phone on the same
network. The server starts and stops automatically with the game.

> The user interface of the page is in German.
> This is a personal hobby project and is **not maintained**. The ETS2LA plugin only loads on the game
> version it was built for, so after a game update you have to rebuild it yourself (see *Building*).

```
ETS2 ─┬─ ets2la_plugin.dll  ──(shared memory: traffic, traffic lights, truck position)──┐
      └─ radar_launcher.dll ──(shared memory: exact speed)──────────────────────────────┤
                │ starts/stops                                                          ▼
                └──────────────────────────> traffic_radar\TrafficRadar.exe (port 8350) ──HTTP──> browser
```

| Part | Folder | Purpose |
|---|---|---|
| ETS2LA plugin | `vendor/ets2la-plugin` | reads traffic and traffic lights from game memory (third-party project, MIT, https://github.com/ETS2LA/plugin, included as a git submodule) |
| Launcher DLL | `launcher/` | starts the server hidden together with the game and ends it with the game (also on a crash, via a job object); provides the exact speed from the official Telemetry SDK |
| Server | `server/` | reads the shared memory and streams it 20×/s to all browsers; the web page is embedded in the EXE |

The launcher DLL also reports whether the simulation is running (SDK events `started`/`paused`). In the
menu, while loading and while paused the page shows "Radar wartet" (radar waiting); the ETS2LA data is
only opened once you are driving (opened earlier, it stayed empty). As a fallback the server reopens it
every 3 s as long as no valid live data arrives.

Without the launcher DLL the server estimates the speed from the change in position.

**Browser:** If no radar page has connected 30 s after the plugins were loaded (open tab, tablet), the
launcher DLL opens `http://localhost:8350/` in the default browser. A tab that is already open reconnects
by itself (10–15 s in tests), so no second one is opened. To disable: `open_browser=0` in
`plugins\radar_launcher.ini`.

## Display

| Colour | Meaning |
|---|---|
| Blue | your truck |
| Red | moving and approaching (closer than 70 m) |
| Yellow | moving |
| Grey | stationary |
| Dots | traffic lights (red / yellow / green) |

The dashed line shows where the vehicle will be in 3 seconds. Trailers are drawn too.
A compass at the top right shows the heading (e.g. "NO 47°"): the rose is fixed (N at the top) and the
needle shows the heading – in both radar orientations.

The compass and "north up" use **geographic** north, like the in-game navigation. The game world is a
flat Lambert conformal conic projection (parameters from `def/climate.sii`, conversion following TruckSim
Maps, see `server/Ets2Projection.cs`); its "up" axis deviates from true north away from 15° E – by
more than 20° in Iceland. Iceland is treated with TruckSim Maps' UK scale rule; in testing this matched
the navigation better (273° instead of 267° for "almost exactly west"), but it is not confirmed.

Buttons: view range 50/100/120/150 m · heading up / north up · activation limit 30/40/50/60/70/80 km/h ·
"Immer" (always, regardless of speed) · full screen. Each browser remembers its own settings. Above the
limit the radar is hidden ("Radar pausiert").

Address: on the PC `http://localhost:8350/`, on a tablet `http://<PC-IP>:8350/`.
On the PC the page shows a QR code with this address next to the buttons (tap to enlarge) – scan it with
the phone/tablet camera. The server uses the network adapter with a default gateway (so VirtualBox/Hyper-V
adapters are skipped). The QR code is not shown on touch devices. QR generation: QRCoder library (MIT).
Without the game running the page is not reachable; it reconnects by itself as soon as ETS2 is running.

## Installing into the game

Target: `<Steam library>\steamapps\common\Euro Truck Simulator 2\bin\win_x64\plugins\`

```
plugins\
  ets2la_plugin.dll
  radar_launcher.dll
  radar_launcher.ini     (from launcher\, optional)
  traffic_radar\
    TrafficRadar.exe     (from dist\traffic_radar)
    server.log           (written on start)
```

Requires the .NET 10 runtime. On game start ETS2 asks once whether to allow the advanced SDK features –
confirm with OK.

### Firewall (once, as administrator)

The server runs without a window – a Windows Firewall prompt would stay invisible and Windows then creates
a **block** rule. So create an allow rule beforehand (adjust the path):

```powershell
netsh advfirewall firewall add rule name="ETS2 Traffic Radar" dir=in action=allow profile=private program="<Steam library>\steamapps\common\Euro Truck Simulator 2\bin\win_x64\plugins\traffic_radar\TrafficRadar.exe"
```

Block rules take precedence over allow rules. If the tablet cannot connect: "Windows Defender Firewall
with Advanced Security" → Inbound Rules → delete rules named "TrafficRadar" with action *Block*.

## Building

Requires Visual Studio with "Desktop development with C++" and the .NET 10 SDK.

```powershell
git clone --recursive https://github.com/ds2216/ets2-traffic-radar.git
cd ets2-traffic-radar
```

**ETS2LA plugin** (rebuild after game updates – it only loads on a matching game version, currently 1.62.x):

```powershell
cd vendor\ets2la-plugin
git pull origin main; git submodule update --init --depth 1
cmake -S . -B build -A x64
cmake --build build --config Release      # -> build\Release\ets2la_plugin.dll
```

**Launcher DLL** (uses only the official SDK interface, usually survives game updates):

```powershell
cd launcher
cmake -S . -B build -A x64
cmake --build build --config Release      # -> build\Release\radar_launcher.dll
```

**Server:**

```powershell
dotnet publish server -c Release -o dist\traffic_radar   # -> dist\traffic_radar\TrafficRadar.exe
```

Run the cmake commands in the *Developer PowerShell for VS*. Quit the game before copying.
If the game is still running, the locked `TrafficRadar.exe` can be renamed (e.g. to `TrafficRadar.old.exe`)
and the new one copied next to it – it takes effect from the next game start; delete the old one afterwards.

Testing without the game: `dist\traffic_radar\TrafficRadar.exe --demo` (simulated junction).
More options: `--port 9000`, `--log file.log`.
`launcher\test\test_host.exe <dll> <seconds> [crash]` simulates the game for the launcher DLL.

Logs: `Documents\Euro Truck Simulator 2\game.log.txt` (lines `[ets2la_plugin]`, `[radar_launcher]`)
and `plugins\traffic_radar\server.log`.

## Limitations

- Meant for single player/convoy. The plugin delivers TruckersMP vehicles without speed – they would
  appear grey ("stationary"); untested in TruckersMP.
- No roads on the radar – only vehicles and traffic lights.
- Keeping the tablet screen awake does not work over `http://` (browser restriction) – raise the screen
  timeout on the tablet if needed.

# Mock EVE client for macOS

A stand-in client for exercising the macOS host without the game. The executable is named `ExeFile`, so discovery classifies it as an EVE client.

```bash
swift build -c release --package-path src/tests/Eve-O-Mock.Mac
src/tests/Eve-O-Mock.Mac/.build/release/ExeFile --name "Pilot One" --x 80 --y 80
```

| Option | Effect |
| --- | --- |
| `--name NAME` | Window title `EVE - NAME` (default `Pilot One`). |
| `--login` | Window title `EVE` (login screen). |
| `--sound` | Plays a short system sound every second (background-mute checks). |
| `--gamelog` | Writes combat lines in EVE's Gamelog format to `~/Documents/EVE/logs/Gamelogs` every two seconds. |
| `--x`, `--y`, `--width`, `--height` | Initial window frame in points. |

The window shows the name, a moving bar and a frame counter, so live capture and preview frame rate are visible. Results against this mock are provisional; confirm with real EVE clients.

# EVE-O Preview for macOS

A macOS version of EVE-O Preview for the native Apple Silicon EVE client. It uses the same settings window, profiles, cycle groups, previews, regions and thumbnail augments as the Windows version.

**Status:** early port, tested on macOS 26 with a mock client. Real-client testing is still to come; please report anything that behaves differently with EVE itself.

## Requirements

- A Mac with Apple Silicon and macOS 14.2 or later.
- EVE clients in **windowed** mode, all on the same desktop (Space). Native fullscreen is not supported.

## Install from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Xcode Command Line Tools (`xcode-select --install`).
2. Create a code-signing identity named **EVE-O Preview Dev** in your login keychain (Keychain Access > Certificate Assistant > Create a Certificate, type *Code Signing*). A stable signature keeps the macOS permissions below when you rebuild.
3. Build the app:

   ```bash
   cd src
   dotnet publish Eve-O-Preview.Mac/Eve-O-Preview.Mac.csproj -c Release
   ```

4. Move `src/Eve-O-Preview.Mac/bin/Release/EVE-O Preview.app` to **Applications** and open it.

## Permissions

On first start macOS asks for these. You can review them later under **System Settings > Privacy & Security**, and on EVE-O's **macOS** page.

| Permission | Used for |
| --- | --- |
| Screen & System Audio Recording | Live previews and client window titles. Restart EVE-O after allowing it. |
| Accessibility | Switching clients, hotkeys, hiding and moving client windows. |
| System Audio Recording | Muting background clients (asked the first time a client is muted). |

## Using it

EVE-O lives in the **menu bar**, not the Dock. Click its icon to open settings. If your menu bar is crowded, the icon can be hidden behind the camera housing; open EVE-O again from Finder or Spotlight to show the settings window.

Everything works as described in the [main guide](README.md), with these Mac differences:

| Windows | macOS |
| --- | --- |
| Ctrl in shortcuts and gestures | **Command (⌘)**. A shortcut saved as `Ctrl+F1` is ⌘F1. Option is Alt; Control is not used in shortcuts. |
| Cycle only while EVE is focused | Same, with the keys always passed on to the active application (there is no Windows hotkeys mode). |
| Ctrl+click a preview (minimize) | **⌘-click** hides that client. Click its preview or cycle to it to bring it back. |
| Minimize / Minimize all | Hides clients (instant, per client). |
| System tray | Menu bar. |
| Limit client frame rates | Not available. Use **Live preview frame rate** and **Efficiency cores** instead. |
| Automatic CPU affinity | **Efficiency cores for background clients**: inactive clients run on efficiency cores at low priority. The active client, the next client in your cycle and login screens are never limited. |
| Selective sound muting (jump tunnel, warp-in) | Not available. **Mute background clients** silences every client except the active one. |
| Compatibility capture, Enhanced graphics | Not needed; macOS always uses live capture and the standard overlay graphics. |

### UI Mirrors

A UI mirror shows one area of a client window, for example Local chat, in its own window. Open **UI Mirrors** in settings (or **New UI mirror...** in the menu bar), choose a character and click **Select area...**. EVE-O brings that client to the front and shows a still of its window: drag a box over the area and release. Esc cancels.

The mirror then works like a preview: drag it to move, drag an edge or corner to resize (proportionally, so text stays sharp and undistorted), click it to switch to that client, and right-click for **Reselect area**, **Actual size**, opacity, update rate and **Remove**. Mirrors belong to the current profile, show while their client runs and hide together with the previews (**Hide all previews** and **Hide previews outside EVE**). The area is fixed relative to the client window, so reselect it after moving or resizing that window inside EVE.

### Bringing Windows profiles over

Copy your Windows `EVE-O Preview` folder (or just its `Profiles` folder) to the Mac, open the **macOS** page, enter your Windows display size and choose **Choose folder...**. Profiles keep their cycle groups, colors and hotkeys; positions and sizes are scaled to your Mac's main display. Imported profiles never overwrite existing ones.

### Files

| What | Where |
| --- | --- |
| Profiles and settings | `~/Library/Application Support/EVE-O Preview/` |
| Logs | `~/Library/Logs/EVE-O Preview/` |
| EVE combat logs (read only) | `~/Documents/EVE/logs/` by default; change it on the Augments page. |

## Not available on macOS

- Frame-rate limits and sound-specific muting: both need code running inside the EVE client, which macOS does not allow.
- Previews of hidden or minimized clients keep their last image.

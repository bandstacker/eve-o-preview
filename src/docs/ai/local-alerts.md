# Local alerts

Local alerts watch the Local member list of chosen characters and raise an alarm when a non-friendly pilot appears, also while the client is covered by other windows. The feature only reads pixels of the watched client window; it never sends input to EVE.

## Model and storage

[AlertModels.cs](../../Eve-O-Preview/Alerts/AlertModels.cs) defines an `AlertWatch` per character: the full client title (`EVE - Name`), the member-list area and an optional member-count area. Areas use the host's window units: points relative to the window's top-left corner on macOS (title bar included, like UI mirrors), client-area pixels on Windows. `CountRead` is the count area grown by half its height left and right and a third above and below: text recognition misreads glyphs cut by a tight box (a clipped `]` reads as `1`). `Capture` is the union of both, so one capture per watch serves both readings. `AlertOptions` holds the profile-wide switches (pause, scans per second, reminder seconds, preview flash, sounds, volume, Discord webhook).

[AlertStore.cs](../../Eve-O-Preview/Alerts/AlertStore.cs) keeps watches and options in `EVE-O Preview.alerts.json` beside the profile JSON, like UI mirrors, so saving the profile elsewhere cannot drop them.

## Detection

[StandingDetector.cs](../../Eve-O-Preview/Alerts/StandingDetector.cs) finds standing icons by colour and shape, independent of UI scale. Each pixel is classed as glyph (bright, unsaturated), grey, one of six hues (red, orange, yellow, green, blue, purple) or background. Connected same-class regions (pieces split by a glyph are joined) become icons when they are 5–72 px, roughly square, mostly filled by their colour plus glyph, have a rim of their colour on at least 60% of the edge, and one flat shade covers at least 45% of their coloured pixels (a sliding window of 20 brightness levels). Grey icons also need a glyph (the neutral `=`). Calibration comes from client icons: neutral fill `#6E6E6E` with a `#8C8C8C` rim, terrible fill `#750A0A` with a `#950606` rim, white glyphs. The rim and flat-shade tests reject anti-aliased text (for example an `a` in a pilot name at 2x), row highlights, scroll bars and large panels.

Green (corp), blue (alliance, +5, +10) and purple (fleet) are friendly. Every other icon (red, orange, yellow, grey) alarms. Pilots without standings only have an icon when the player enables EVE's neutral standing icon; the count rule covers the rest. Portrait mode in the member list is not supported.

[AlertSounds.cs](../../Eve-O-Preview/Alerts/AlertSounds.cs) also holds `AlertText.ParseCount`: a bracketed number (`Local [12]`), else the last number, with O/l/I slips inside numbers corrected.

## Alarm rules

[AlertWatchMonitor.cs](../../Eve-O-Preview/Alerts/AlertWatchMonitor.cs) is the per-watch state machine, free of capture and output:

- Hostile, friendly and count readings count only after holding for two scans.
- The first stable reading is the baseline: non-friendly icons already present alarm; the count is not compared.
- More non-friendly icons: `HostileArrived`, immediately.
- A higher count waits 2.5 s for new icons (friendly or hostile, appearing just before or after) to explain it; the rest becomes `Unexplained` and raises `UnknownArrived`. A lower count reduces pending, then unexplained pilots.
- While anyone non-friendly or unexplained remains: `Reminder` every `ReminderSeconds` until acknowledged. Acknowledging lasts until the next arrival.
- `AllClear` after 10 s with nobody left, once per alarmed incident.
- No live frame for 5 s: `Paused` (the client is hidden, minimized or cannot be captured); a live frame resumes. Readings keep their values across a pause, so arrivals during it alarm on resume.

## Manager and outputs

[AlertManager.cs](../../Eve-O-Preview/Alerts/AlertManager.cs) owns the watches of the current profile and reloads on profile switches.

- A 400 ms UI-thread timer starts one `IAlertSampler` per enabled watch whose client runs (restarting when the window or areas change), acknowledges a watch when its client becomes the foreground window (clicking its preview or cycling to it), and drives the preview indicators: a `PreviewAlert` pulse every 1.6 s while alarmed (if enabled) and the subtitle note "Watch paused" through `ThumbnailView.SetWatchNote`, which combines it with the system name of Augments.
- A background loop scans at `ScansPerSecond`: it reads each sampler, crops the list and count, reuses the last result when the pixels are unchanged (`AlertFrame.Fingerprint`), detects icons, recognises the count text and updates the monitor. Events play one sound per scan (hostile sound has priority) and post to Discord.
- [DiscordWebhook.cs](../../Eve-O-Preview/Alerts/DiscordWebhook.cs) posts arrival embeds (title, System, Character, Spotted, the member list as an attached PNG from `AlertPng`) at most every 5 s per watch, and an all-clear embed (System, Character) after an incident that posted. The system name comes from `CharacterSystemCache` (Local chat logs read by Augments), else "Unknown system". A rate limit is waited out once; 401/403/404 turns Discord messages off and reports why.
- Sounds are generated tones by default (no third-party audio), "none", or a .wav file; the platform applies the volume.

`IAlertPlatform` ([IAlertPlatform.cs](../../Eve-O-Preview/Alerts/IAlertPlatform.cs)) is the host seam: samplers (live, stale or unavailable frames), text recognition, sound playback and the two-step area selection on a still of the client. A host without an implementation does not register `AlertManager`; `AlertShortcutHandler` then ignores alert shortcuts.

## UI and shortcuts

[AlertsView.cs](../../Eve-O-Preview.UI/AlertsView.cs) is the portable **Local alerts** page (Light/Dark): new watch, the watch list with live status and the "what EVE-O sees" view (count area above the member list, detected icons outlined: red alarms, green ignored), alarm options and Discord. It talks to `IWorkspaceAlerts` ([AlertContract.cs](../../Eve-O-Preview.UI/AlertContract.cs)), implemented by [WindowsWorkspaceBackend.Alerts.cs](../../Eve-O-Preview/View/Implementation/WindowsWorkspaceBackend.Alerts.cs) when the host sets `AlertHost`.

Profiles store two global shortcuts, `AcknowledgeAlertsHotkey` and `ToggleAlertsHotkey` (empty by default). `ThumbnailManager.RegisterAllHotkeys` publishes `AlertShortcut`; the backend publishes their settings only when `AlertHost` is set, so **Switching & hotkeys > Global shortcuts** shows them only on hosts with alerts, and hotkey capture checks them for conflicts.

## Validation

[AlertTests.cs](../../tests/Eve-O-Preview.Tests/Checks/AlertTests.cs) covers icon detection at 1x and 2x, rejection of panels, scroll bars and a captured text glyph, captured fills crossing a shade boundary, every alarm rule, count parsing, PNG and WAV output, Discord payloads, storage and crop scaling. The UI smoke run renders and drives the page. Detection was tuned on mock clients drawing icons in the measured client colours; confirm on real clients (UI scales, colour-blind settings and window transparency) before relying on it.

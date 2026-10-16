using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using EveOPreview.Alerts;
using EveOPreview.Mac.Native;
using EveOPreview.Services;
using EveOPreview.Services.Implementation;
using EveOPreview.UI;
using EveOPreview.View;
using Serilog;

namespace EveOPreview.Mac;

/// <summary>
/// Developer-only test hook, enabled with --debug-harness. Each line written to
/// &lt;settings folder&gt;/debug/command.txt is executed on the UI thread and the file is removed:
///   snapshot NAME | state NAME | click TITLE | minimize TITLE | setting KEY VALUE | open-settings | hide-settings
///   alert-add TITLE|X,Y,W,H[|X,Y,W,H] | alert-state | alert-option KEY VALUE | alert-watch INDEX ACTION [VALUE] | alert-view INDEX | alert-ack | still TITLE
/// Output files are written to the same folder. Never enabled in normal launches.
/// </summary>
internal sealed class DebugHarness
{
    private readonly string _folder = Path.Combine(MacSettings.RootDirectory, "debug");
    private readonly IThumbnailManager _thumbnails;
    private readonly IProcessMonitor _processes;
    private readonly WorkspaceWindow _workspace;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly Mirrors.MirrorManager _mirrors;
    private readonly AlertManager _alerts;

    public DebugHarness(IThumbnailManager thumbnails, IProcessMonitor processes, WorkspaceWindow workspace,
        Mirrors.MirrorManager mirrors, AlertManager alerts, ILogger logger)
    {
        _thumbnails = thumbnails; _processes = processes; _workspace = workspace; _mirrors = mirrors; _alerts = alerts; _logger = logger;
        Directory.CreateDirectory(_folder);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        logger.Warning("Debug harness enabled: {Folder}", _folder);
        MacNative.DebugMonitorMouse();
    }

    private void Poll()
    {
        string path = Path.Combine(_folder, "command.txt");
        if (!File.Exists(path)) return;
        string[] lines;
        try { lines = File.ReadAllLines(path); File.Delete(path); }
        catch (IOException) { return; }
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try { Execute(line.Trim()); }
            catch (Exception ex) { _logger.Error(ex, "Debug command failed: {Command}", line); }
        }
        File.WriteAllText(Path.Combine(_folder, "done.txt"), DateTime.Now.ToString("O"));
    }

    private void Execute(string line)
    {
        int space = line.IndexOf(' ');
        string verb = space < 0 ? line : line[..space], argument = space < 0 ? "" : line[(space + 1)..];
        switch (verb)
        {
            case "snapshot":
                var png = MacNative.CaptureMainDisplay();
                if (png != null) File.WriteAllBytes(Path.Combine(_folder, (argument.Length > 0 ? argument : "snapshot") + ".png"), png);
                break;
            case "state":
                File.WriteAllText(Path.Combine(_folder, (argument.Length > 0 ? argument : "state") + ".txt"), State());
                break;
            case "click":
                if (_thumbnails.GetClientByTitle(argument) is { } clicked) clicked.ThumbnailActivated?.Invoke(clicked.Id);
                break;
            case "activate-check":
                if (_processes.GetAllProcesses().FirstOrDefault(p => p.Title == argument) is not { } target) break;
                var trace = new StringBuilder();
                var started = System.Diagnostics.Stopwatch.StartNew();
                int ok = MacNative.ActivateWindow(target.ProcessId, (uint)target.MainWindowHandle);
                trace.AppendLine($"native result={ok} after {started.Elapsed.TotalMilliseconds:F1} ms");
                for (int i = 0; i < 20; i++)
                {
                    System.Threading.Thread.Sleep(25);
                    var (w, p) = MacNative.FrontmostWindow();
                    trace.AppendLine($"{started.Elapsed.TotalMilliseconds:F0} ms frontmost pid={p} window={w}");
                }
                File.WriteAllText(Path.Combine(_folder, "activate.txt"), $"target pid={target.ProcessId} window={target.MainWindowHandle}\n" + trace);
                break;
            case "move":
                // move X Y TITLE
                var mv = argument.Split(' ', 3);
                if (_thumbnails.GetClientByTitle(mv[2]) is { } moved)
                    moved.ThumbnailLocation = new System.Drawing.Point(int.Parse(mv[0]), int.Parse(mv[1]));
                break;
            case "resize":
                // resize WIDTH HEIGHT TITLE: an individual preview resize, as from the Resize gesture.
                var rs = argument.Split(' ', 3);
                if (_thumbnails.GetClientByTitle(rs[2]) is { } resized)
                {
                    resized.ThumbnailSize = new System.Drawing.Size(int.Parse(rs[0]), int.Parse(rs[1]));
                    resized.ThumbnailResized?.Invoke(resized.Id);
                }
                break;
            case "undo": _ = _thumbnails.UndoThumbnailEdit(); break;
            case "size-all": if (_thumbnails.GetClientByTitle(argument) is { } sized) _ = _thumbnails.UseThumbnailSizeForAll(sized.Id); break;
            case "minimize":
                if (_thumbnails.GetClientByTitle(argument) is { } minimized) minimized.ThumbnailDeactivated?.Invoke(minimized.Id, false);
                break;
            case "setting":
                int split = argument.IndexOf(' ');
                var key = argument[..split];
                _workspace.Backend.ExecuteAsync(new WorkspaceCommand("setting", key, argument[(split + 1)..]))
                    .ContinueWith(t => _logger.Information("Debug setting {Key}: {Success} {Message}", key, t.Result.Success, t.Result.Message));
                break;
            case "cmd":
                var parts = argument.Split('|');
                var command = new WorkspaceCommand(parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
                _workspace.Backend.ExecuteAsync(command).ContinueWith(t =>
                    _logger.Information("Debug command {Action}: {Success} {Message}", command.Action, t.Result.Success, t.Result.Message));
                break;
            case "key":
                var keyParts = argument.Split(' ');
                ushort code = Convert.ToUInt16(keyParts[0], 16);
                ulong flags = keyParts.Length > 1 ? Convert.ToUInt64(keyParts[1], 16) : 0;
                System.Threading.Tasks.Task.Run(() => MacNative.PostKey(code, flags));
                break;
            case "mouse":
                // mouse KIND X Y [X2 Y2 [FLAGS]]
                var m = argument.Split(' ');
                double D(int i) => i < m.Length ? double.Parse(m[i], System.Globalization.CultureInfo.InvariantCulture) : 0;
                ulong mouseFlags = m.Length > 5 ? Convert.ToUInt64(m[5], 16) : 0;
                System.Threading.Tasks.Task.Run(() => MacNative.PostMouse(int.Parse(m[0]), D(1), D(2), D(3), D(4), mouseFlags));
                break;
            case "combatlogs":
                if (_workspace.Backend is IWorkspaceCombatLogs logs)
                    logs.SaveLogSettingsAsync(logs.ReadLogSettings() with { Enabled = argument != "off" })
                        .ContinueWith(t => _logger.Information("Debug combat logs: {Success} {Message}", t.Result.Success, t.Result.Message));
                break;
            case "windows": File.WriteAllText(Path.Combine(_folder, "windows.txt"), MacNative.DebugDescribeWindows()); break;
            case "mirror-create":
                _mirrors.CreateAsync(argument).ContinueWith(t => _logger.Information("Debug mirror-create: {Result}", t.Result ?? "ok"));
                break;
            case "mirror-command":
                // mirror-command INDEX COMMAND
                var mc = argument.Split(' ', 2);
                if (int.TryParse(mc[0], out int mi) && mi < _mirrors.Mirrors.Count) _ = _mirrors.Execute(_mirrors.Mirrors[mi].Id, mc[1]);
                break;
            case "alert-add":
                // alert-add EVE - Pilot|x,y,w,h[|x,y,w,h]: a watch without the selection UI.
                var alertParts = argument.Split('|');
                static AlertArea Area(string text)
                {
                    var n = text.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    return new AlertArea(n[0], n[1], n[2], n[3]);
                }
                _logger.Information("Debug alert-add: {Result}", _alerts.AddWatch(alertParts[0], Area(alertParts[1]), alertParts.Length > 2 ? Area(alertParts[2]) : null).Message);
                break;
            case "alert-state":
                var alerts = _alerts.Snapshot();
                var lines = new StringBuilder($"paused={alerts.Options.Paused} scans={alerts.Options.ScansPerSecond} webhook='{alerts.Options.WebhookStatus}'\n");
                foreach (var watch in alerts.Watches)
                    lines.AppendLine($"watch '{watch.Name}' {watch.Status} hostile={watch.Hostile} friendly={watch.Friendly} count={watch.Count} unknown={watch.Unidentified} detail='{watch.Detail}' countText='{_alerts.ReadCountText(watch.Id).Replace("\n", "\\n")}'");
                File.WriteAllText(Path.Combine(_folder, "alerts.txt"), lines.ToString());
                break;
            case "alert-option":
                var option = argument.Split(' ', 2);
                _logger.Information("Debug alert-option: {Result}", _alerts.EditOptions(option[0], option.Length > 1 ? option[1] : "").Message);
                break;
            case "alert-watch":
                var wa = argument.Split(' ', 3);
                if (int.TryParse(wa[0], out int wi) && wi < _alerts.Snapshot().Watches.Count)
                    _alerts.EditWatchAsync(_alerts.Snapshot().Watches[wi].Id, wa[1], wa.Length > 2 ? wa[2] : "")
                        .ContinueWith(t => _logger.Information("Debug alert-watch: {Result}", t.Result.Message));
                break;
            case "alert-view":
                if (int.TryParse(argument, out int vi) && vi < _alerts.Snapshot().Watches.Count
                    && _alerts.ReadView(_alerts.Snapshot().Watches[vi].Id) is { } view)
                    File.WriteAllBytes(Path.Combine(_folder, "alert-view.png"), view);
                break;
            case "alert-ack": _alerts.AcknowledgeAll(); break;
            case "still":
                // still TITLE: the selection still of a client window (no activation), with its frame.
                var stillClient = _processes.GetAllProcesses().FirstOrDefault(p => p.Title == argument);
                if (stillClient == null) break;
                uint stillWindow = (uint)stillClient.MainWindowHandle;
                MacNative.TryGetWindowBounds(stillWindow, out var stillBounds);
                var stillPng = MacNative.CaptureWindow(stillWindow, (int)(stillBounds.Width * 2), (int)(stillBounds.Height * 2));
                if (stillPng != null) File.WriteAllBytes(Path.Combine(_folder, "still.png"), stillPng);
                File.WriteAllText(Path.Combine(_folder, "still.txt"), $"bounds={stillBounds} png={stillPng?.Length ?? 0}");
                break;
            case "open-settings": _workspace.RestoreWorkspace(); break;
            case "navigate": _workspace.RestoreWorkspace(); _workspace.Workspace.Navigate(argument); break;
            case "hide-settings": _workspace.Hide(); break;
        }
    }

    private string State()
    {
        var text = new StringBuilder();
        var (window, pid) = MacNative.FrontmostWindow();
        text.AppendLine($"frontmost pid={pid} window={window}");
        text.AppendLine($"active={_thumbnails.GetActiveClient()?.Title} hiddenOutsideEve={_thumbnails.ArePreviewsHiddenOutsideEve}");
        foreach (var process in _processes.GetAllProcesses())
            text.AppendLine($"client '{process.Title}' pid={process.ProcessId} window={process.MainWindowHandle} eve={process.IsEveClient} " +
                            $"hidden={MacNative.IsApplicationHidden(process.ProcessId) != 0} muted={MacNative.AudioIsMuted(process.ProcessId) != 0}");
        foreach (var group in _workspace.Backend.Read().CycleGroups)
            text.AppendLine($"group {group.Id} '{group.Name}' clients=[{string.Join(", ", group.Clients)}] forward=[{string.Join(", ", group.ForwardHotkeys)}]");
        foreach (var (id, view) in _thumbnails.GetAllKnownClients())
        {
            var bounds = view is ThumbnailView native ? native.Bounds : default;
            text.AppendLine($"preview '{view.Title}' source={id} active={view.IsActive} bounds={bounds} topmost={(view as ThumbnailView)?.Topmost} window={(view as ThumbnailView)?.Handle} interacting={view.IsInteracting} menu={view.IsContextMenuOpen}");
        var (px, py) = MacNative.PointerLocation();
        text.AppendLine($"pointer {px},{py}");
        }
        foreach (var mirror in _mirrors.Mirrors)
            text.AppendLine($"mirror '{mirror.Name}' source='{mirror.SourceTitle}' area={mirror.SourceX},{mirror.SourceY} {mirror.SourceWidth}x{mirror.SourceHeight} " +
                $"window={mirror.X},{mirror.Y} {mirror.Width}x{mirror.Height} opacity={mirror.Opacity} fps={mirror.FrameRate}");
        if (_workspace.Backend is IWorkspaceCombatLogs combat)
        {
            var snapshot = combat.ReadLogs();
            text.AppendLine($"combat status='{snapshot.Status}' dir='{snapshot.Directory}' recent={snapshot.RecentEntries.Count} unrecognized={snapshot.UnrecognizedCombatEntries}");
            foreach (var character in snapshot.Characters)
                text.AppendLine($"  combat '{character.Name}' files={character.GameFiles} last={character.LastLogAt:HH:mm:ss} categories={character.Categories.Count}: " +
                    string.Join("; ", character.Categories.Select(c => c.ToString())));
            foreach (var entry in snapshot.RecentEntries.Take(3)) text.AppendLine($"  entry {entry}");
        }
        return text.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveOPreview.Configuration;
using EveOPreview.Preview;
using EveOPreview.Services;
using EveOPreview.Services.Interface;
using EveOPreview.Services.Logs;
using EveOPreview.UI;
using EveOPreview.View;
using Serilog;

namespace EveOPreview.Alerts;

/// <summary>
/// Owns the Local watches of the current profile. A UI-thread timer starts and stops one capture per
/// watched, running client and drives the preview indicators; a background loop reads the captures,
/// finds standing icons, reads the member count and turns changes into sounds and Discord messages.
/// </summary>
public sealed class AlertManager : IDisposable
{
    private static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan WebhookSpacing = TimeSpan.FromSeconds(5);
    private const uint PulseColor = 0xFFFF2D2D, NoteColor = 0xFFFFB347;
    public const string PausedNote = "Watch paused";

    private readonly AlertStore _store;
    private readonly IConfigurationStorage _storage;
    private readonly IProcessMonitor _processes;
    private readonly IThumbnailManager _thumbnails;
    private readonly IWindowManager _windows;
    private readonly IAlertPlatform _platform;
    private readonly CharacterSystemCache _systems;
    private readonly ILogger _logger;
    private readonly DiscordWebhook _webhook = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Runner> _runners = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private AlertDocument _document = new();
    private string _profileFolder;
    private string _webhookStatus = "";
    private string _signature = "";
    private IntPtr _lastForeground;
    private bool _selecting, _started, _disposed;

    public AlertManager(AlertStore store, IConfigurationStorage storage, IProcessMonitor processes, IThumbnailManager thumbnails,
        IWindowManager windows, IGlobalEvents events, IAlertPlatform platform, ILogger logger, CharacterSystemCache systems = null)
    {
        _store = store; _storage = storage; _processes = processes; _thumbnails = thumbnails; _windows = windows;
        _platform = platform; _logger = logger; _systems = systems;
        events.CurrentProfileChanged += _ => Dispatcher.UIThread.Post(Reload);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Raised on the UI thread when watches, options or a watch's status change.</summary>
    public event Action Changed;

    public bool IsPaused { get { lock (_gate) return _document.Options.Paused; } }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Reload();
        _timer.Start();
        _ = Task.Run(() => ScanLoop(_stop.Token));
    }

    private sealed class Runner(AlertWatch watch, IntPtr window, IAlertSampler sampler, DateTimeOffset started)
    {
        public readonly object Lock = new();
        public AlertWatch Watch = watch;
        public readonly IntPtr Window = window;
        public IAlertSampler Sampler = sampler;
        public readonly AlertWatchMonitor Monitor = new(started);
        public bool Disposed;
        // Scan thread (read elsewhere under Lock)
        public ulong ListPrint, CountPrint;
        public StandingScan Scan = StandingScan.Empty;
        public AlertFrame List;
        public int? CountRead;
        public string CountText = "";
        public AlertFrame CountFrame;
        public DateTimeOffset NextWebhook;
        public bool WebhookSent;
        // UI thread
        public DateTimeOffset LastPulse;
        public string AppliedNote = "";

        public void Dispose()
        {
            lock (Lock)
            {
                Disposed = true;
                Sampler?.Dispose();
                Sampler = null;
            }
        }
    }

    private void Reload()
    {
        if (_disposed) return;
        string folder = _storage.CurrentProfile?.FolderPath;
        lock (_gate)
        {
            if (folder == _profileFolder && _document.Watches.Count > 0) return;
            foreach (var runner in _runners.Values.ToArray()) Remove(runner);
            _profileFolder = folder;
            _document = _store.Load(folder);
            _webhookStatus = "";
        }
        Tick();
        Changed?.Invoke();
    }

    private void Save()
    {
        AlertDocument document;
        string folder;
        lock (_gate) { document = _document with { Watches = _document.Watches.ToList() }; folder = _profileFolder; }
        try { _store.Save(folder, document); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.Error(ex, "Local alerts could not be saved"); }
        _signature = "";
        Changed?.Invoke();
    }

    private IProcessInfo Client(IEnumerable<IProcessInfo> processes, string title) =>
        processes.FirstOrDefault(p => p.Title == title && p.MainWindowHandle != IntPtr.Zero);

    // ---------- UI thread: captures, acknowledgement and preview indicators ----------

    private void Tick()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        var processes = _processes.GetAllProcesses().ToArray();
        IntPtr foreground = _windows.GetForegroundWindowHandle();
        Runner[] runners;
        bool flash;
        lock (_gate)
        {
            var options = _document.Options;
            flash = options.FlashPreviews;
            foreach (var watch in _document.Watches)
            {
                _runners.TryGetValue(watch.Id, out var runner);
                var client = options.Paused || !watch.Enabled ? null : Client(processes, watch.SourceTitle);
                if (client == null)
                {
                    if (runner != null) Remove(runner);
                    continue;
                }
                if (runner != null && (runner.Window != client.MainWindowHandle || runner.Watch.Capture != watch.Capture))
                {
                    Remove(runner);
                    runner = null;
                }
                if (runner == null)
                {
                    IAlertSampler sampler;
                    try { sampler = _platform.CreateSampler(client.MainWindowHandle, watch.Capture); }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Local alert capture could not start for {Title}", watch.SourceTitle);
                        continue;
                    }
                    _runners[watch.Id] = new Runner(watch, client.MainWindowHandle, sampler, now);
                }
                else lock (runner.Lock) runner.Watch = watch;
            }
            foreach (var orphan in _runners.Values.Where(r => _document.Watches.All(w => w.Id != r.Watch.Id)).ToArray()) Remove(orphan);
            runners = _runners.Values.ToArray();
        }
        foreach (var runner in runners)
        {
            bool alarm, paused;
            lock (runner.Lock)
            {
                // Switching to the client (clicking its preview, cycling) acknowledges its alarm.
                if (foreground == runner.Window && _lastForeground != runner.Window) runner.Monitor.Acknowledge();
                alarm = runner.Monitor.IsActive && runner.Monitor.IsAlarmed;
                paused = runner.Monitor.IsPaused;
            }
            ApplyIndicators(runner, alarm && flash, paused ? PausedNote : "", now);
        }
        _lastForeground = foreground;
        RaiseIfChanged();
    }

    private void ApplyIndicators(Runner runner, bool pulse, string note, DateTimeOffset now)
    {
        if (_thumbnails.GetClientByTitle(runner.Watch.SourceTitle) is not ThumbnailView view) return;
        // A non-empty note is reapplied every tick, so a preview created later still shows it.
        if (note.Length > 0 || runner.AppliedNote.Length > 0) view.SetWatchNote(note, NoteColor);
        runner.AppliedNote = note;
        if (pulse && now - runner.LastPulse >= PulseInterval)
        {
            runner.LastPulse = now;
            view.ShowAlert(new PreviewAlert(PulseColor, 0.9, 0.45));
        }
    }

    /// <summary>Stops a capture and clears its preview note. Called under <see cref="_gate"/> on the UI thread.</summary>
    private void Remove(Runner runner)
    {
        _runners.Remove(runner.Watch.Id);
        runner.Dispose();
        if (runner.AppliedNote.Length > 0 && _thumbnails.GetClientByTitle(runner.Watch.SourceTitle) is ThumbnailView view)
            view.SetWatchNote("", NoteColor);
    }

    private void RaiseIfChanged()
    {
        var snapshot = Snapshot();
        string signature = string.Join("|", snapshot.Watches.Select(w => $"{w.Id}:{w.Status}:{w.Detail}")) + "|" + string.Join(",", snapshot.Clients)
            + "|" + snapshot.Options.WebhookStatus;
        if (signature == _signature) return;
        _signature = signature;
        Changed?.Invoke();
    }

    // ---------- Scan thread ----------

    private async Task ScanLoop(CancellationToken stop)
    {
        var clock = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            AlertOptions options;
            Runner[] runners;
            lock (_gate) { options = _document.Options; runners = _runners.Values.ToArray(); }
            var outputs = new List<(Runner Runner, AlertEvent Event)>();
            if (!options.Paused)
                foreach (var runner in runners)
                {
                    try { await ScanAsync(runner, options, outputs).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.Warning(ex, "Local alert scan failed for {Title}", runner.Watch.SourceTitle); }
                }
            if (outputs.Count > 0) Dispatch(outputs, options);
            var wait = TimeSpan.FromSeconds(1 / options.ScansPerSecond) - (clock.Elapsed - started);
            try { await Task.Delay(wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50), stop).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task ScanAsync(Runner runner, AlertOptions options, List<(Runner, AlertEvent)> outputs)
    {
        AlertSample sample;
        AlertWatch watch;
        lock (runner.Lock)
        {
            if (runner.Disposed || runner.Sampler == null) return;
            sample = runner.Sampler.Read();
            watch = runner.Watch;
        }
        StandingScan scan = null;
        AlertFrame list = null;
        int? count = runner.CountRead;
        if (sample.State == AlertSampleState.Live && sample.Frame != null)
        {
            var origin = watch.Capture;
            list = sample.Frame.Crop(watch.List, origin);
            ulong print = list.Fingerprint();
            scan = print == runner.ListPrint ? runner.Scan : StandingDetector.Detect(list);
            runner.ListPrint = print;
            if (watch.CountRead is { } countArea && _platform.CanRecognizeText)
            {
                var countFrame = sample.Frame.Crop(countArea, origin);
                ulong countPrint = countFrame.Fingerprint();
                if (countPrint != runner.CountPrint)
                {
                    string text = await _platform.RecognizeTextAsync(countFrame).ConfigureAwait(false);
                    count = AlertText.ParseCount(text);
                    runner.CountText = text ?? "";
                    runner.CountFrame = countFrame;
                    runner.CountPrint = countPrint;
                    runner.CountRead = count;
                }
            }
            else count = null;
        }
        lock (runner.Lock)
        {
            if (runner.Disposed) return;
            if (scan != null) { runner.Scan = scan; runner.List = list; }
            foreach (var e in runner.Monitor.Update(new(sample.State, scan, count), DateTimeOffset.UtcNow, options.ReminderSeconds))
                outputs.Add((runner, e));
        }
    }

    private void Dispatch(List<(Runner Runner, AlertEvent Event)> outputs, AlertOptions options)
    {
        bool hostile = false, unknown = false;
        foreach (var (runner, e) in outputs)
        {
            int present;
            string spotted;
            lock (runner.Lock) { present = runner.Monitor.Hostile; spotted = runner.Monitor.Describe(); }
            if (e.Kind == AlertEventKind.HostileArrived || e.Kind == AlertEventKind.Reminder && present > 0) hostile = true;
            else if (e.Kind is AlertEventKind.UnknownArrived or AlertEventKind.Reminder) unknown = true;
            if (e.Kind != AlertEventKind.Reminder)
                _logger.Information("Local alert {Kind} on {Title}: {Detail}", e.Kind, runner.Watch.SourceTitle, e.Detail.Length > 0 ? e.Detail : spotted);
            SendWebhook(runner, e, spotted, options);
        }
        byte[] sound = hostile ? AlertSounds.Resolve(options.Sound, AlertSounds.Hostile)
            : unknown ? AlertSounds.Resolve(options.CountSound, AlertSounds.Count) : null;
        if (sound != null && options.Volume > 0) _platform.PlaySound(sound, options.Volume / 100.0);
        Dispatcher.UIThread.Post(RaiseIfChanged);
    }

    private void SendWebhook(Runner runner, AlertEvent e, string spotted, AlertOptions options)
    {
        if (!options.WebhookEnabled || !DiscordWebhook.IsValidUrl(options.WebhookUrl)) return;
        var now = DateTimeOffset.UtcNow;
        string character = runner.Watch.Character, system = _systems?.GetSystem(character);
        string payload;
        byte[] png = null;
        if (e.Kind is AlertEventKind.HostileArrived or AlertEventKind.UnknownArrived)
        {
            if (now < runner.NextWebhook) return;
            runner.NextWebhook = now + WebhookSpacing;
            runner.WebhookSent = true;
            AlertFrame list;
            lock (runner.Lock) list = runner.List;
            png = list is null ? null : AlertPng.Encode(list);
            payload = e.Kind == AlertEventKind.HostileArrived
                ? DiscordWebhook.ArrivalPayload("Hostile in Local", system, character, spotted, png != null, now)
                : DiscordWebhook.UnknownPayload(system, character, spotted, png != null, now);
        }
        else if (e.Kind == AlertEventKind.AllClear && runner.WebhookSent)
        {
            runner.WebhookSent = false;
            payload = DiscordWebhook.AllClearPayload(system, character, now);
        }
        else return;
        _ = PostWebhookAsync(options.WebhookUrl, payload, png);
    }

    private async Task<WebhookResult> PostWebhookAsync(string url, string payload, byte[] png)
    {
        var result = await _webhook.SendAsync(url, payload, png, _stop.Token).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (result.Outcome == WebhookOutcome.Rejected)
            {
                // Like a deleted webhook in Discord: stop posting until the user fixes the address.
                lock (_gate) _document = _document with { Options = _document.Options with { WebhookEnabled = false } };
                _webhookStatus = result.Message + " Discord messages were turned off.";
                Save();
            }
            else _webhookStatus = result.Outcome == WebhookOutcome.Sent
                ? $"Last message sent at {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}" : result.Message;
            RaiseIfChanged();
        });
        if (result.Outcome != WebhookOutcome.Sent) _logger.Warning("Local alert Discord message failed: {Message}", result.Message);
        return result;
    }

    // ---------- Commands (UI thread) ----------

    public AlertSnapshot Snapshot()
    {
        var clients = _processes.GetAllProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero && p.Title.StartsWith("EVE - ", StringComparison.Ordinal))
            .Select(p => p.Title).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            var o = _document.Options;
            var options = new AlertOptionsItem(o.Paused, o.ScansPerSecond, o.ReminderSeconds, o.FlashPreviews, o.Sound, o.CountSound,
                o.Volume, o.WebhookUrl, o.WebhookEnabled, _webhookStatus);
            return new(options, _document.Watches.Select(Item).ToArray(), clients, _selecting, _platform.CanRecognizeText);
        }
    }

    private AlertWatchItem Item(AlertWatch watch)
    {
        bool hasCount = watch.Count is { IsUsable: true };
        AlertWatchItem Make(AlertWatchStatus status, string detail, int hostile = 0, int friendly = 0, int? count = null, int unknown = 0) =>
            new(watch.Id, watch.Name, watch.SourceTitle, watch.Enabled, hasCount, status, hostile, friendly, count, unknown, detail);
        if (!watch.Enabled) return Make(AlertWatchStatus.Off, "Off");
        if (_document.Options.Paused) return Make(AlertWatchStatus.Off, "Alerts are paused");
        if (!_runners.TryGetValue(watch.Id, out var runner)) return Make(AlertWatchStatus.Waiting, "Waiting for the client to start");
        lock (runner.Lock)
        {
            var m = runner.Monitor;
            if (m.IsPaused) return Make(AlertWatchStatus.Paused, "Not watching: the client is hidden, minimized or cannot be captured", m.Hostile, m.Friendly, m.Count, m.Unexplained);
            if (!m.HasLiveFrame) return Make(AlertWatchStatus.Starting, "Starting capture...");
            if (m.IsActive) return Make(AlertWatchStatus.Alarm, m.Describe() + (m.IsAcknowledged ? " (acknowledged)" : ""), m.Hostile, m.Friendly, m.Count, m.Unexplained);
            var parts = new List<string> { m.LastScan.Icons.Count == 0 ? "No standing icons visible" : $"{m.Friendly} friendly" };
            if (hasCount) parts.Add(m.Count is { } count ? $"Local {count}" : "count not read yet");
            return Make(AlertWatchStatus.Clear, "Clear · " + string.Join(" · ", parts), m.Hostile, m.Friendly, m.Count, m.Unexplained);
        }
    }

    public Task<CommandResult> CreateAsync(string sourceTitle) => SelectAsync(sourceTitle, null);

    private async Task<CommandResult> SelectAsync(string sourceTitle, AlertWatch existing)
    {
        if (_selecting) return CommandResult.Error("Finish the current selection first.");
        var client = Client(_processes.GetAllProcesses(), sourceTitle);
        if (client == null) return CommandResult.Error("Start that client first. Areas are drawn on a running client.");
        _selecting = true;
        Changed?.Invoke();
        try
        {
            Activate(sourceTitle, client.MainWindowHandle);
            await Task.Delay(250);
            var selection = await _platform.SelectAreasAsync(client.MainWindowHandle, existing?.List, existing?.Count);
            if (selection == null) return new CommandResult(false, "Selection cancelled.", WasCancelled: true);
            if (selection.Error != null) return CommandResult.Error(selection.Error);
            string character = sourceTitle.StartsWith("EVE - ", StringComparison.Ordinal) ? sourceTitle[6..] : sourceTitle;
            lock (_gate)
            {
                if (existing == null)
                    _document.Watches.Add(new AlertWatch { Name = $"{character} Local", SourceTitle = sourceTitle, List = selection.List, Count = selection.Count });
                else
                {
                    int index = _document.Watches.FindIndex(w => w.Id == existing.Id);
                    if (index < 0) return CommandResult.Error("That watch no longer exists.");
                    _document.Watches[index] = _document.Watches[index] with { List = selection.List, Count = selection.Count };
                }
            }
            Save();
            Tick();
            return CommandResult.Ok(existing == null ? $"Watching Local for {character}" : "Areas updated");
        }
        finally
        {
            _selecting = false;
            // Selection activated this application; give focus back to the client.
            Activate(sourceTitle, client.MainWindowHandle);
            Changed?.Invoke();
        }
    }

    /// <summary>Adds a watch with known areas, without the selection UI (diagnostics and tests).</summary>
    public CommandResult AddWatch(string sourceTitle, AlertArea list, AlertArea? count)
    {
        if (string.IsNullOrWhiteSpace(sourceTitle) || !list.IsUsable) return CommandResult.Error("Choose a client and a member-list area.");
        string character = sourceTitle.StartsWith("EVE - ", StringComparison.Ordinal) ? sourceTitle[6..] : sourceTitle;
        lock (_gate) _document.Watches.Add(new AlertWatch { Name = $"{character} Local", SourceTitle = sourceTitle, List = list, Count = count });
        Save();
        Tick();
        return CommandResult.Ok($"Watching Local for {character}");
    }

    private void Activate(string title, IntPtr window)
    {
        var view = _thumbnails.GetClientByTitle(title);
        if (view != null) view.ThumbnailActivated?.Invoke(view.Id);
        else _windows.ActivateWindow(window);
    }

    public async Task<CommandResult> EditWatchAsync(string id, string action, string value)
    {
        AlertWatch watch;
        lock (_gate) watch = _document.Watches.FirstOrDefault(w => w.Id == id);
        if (watch == null) return CommandResult.Error("That watch no longer exists.");
        switch (action)
        {
            case "reselect": return await SelectAsync(watch.SourceTitle, watch);
            case "rename":
                if (string.IsNullOrWhiteSpace(value)) return CommandResult.Error("Enter a name.");
                Replace(watch with { Name = value.Trim() });
                return CommandResult.Ok("Renamed");
            case "enabled":
                if (!bool.TryParse(value, out bool enabled)) return CommandResult.Error("Choose on or off.");
                Replace(watch with { Enabled = enabled });
                return CommandResult.Ok(enabled ? "Watch on" : "Watch off");
            case "remove":
                lock (_gate) _document.Watches.RemoveAll(w => w.Id == id);
                Save();
                Tick();
                return CommandResult.Ok("Watch removed");
            case "copy-to-all": return CopyToAll(watch);
            default: return CommandResult.Error("Unknown watch action.");
        }
    }

    private void Replace(AlertWatch watch)
    {
        lock (_gate)
        {
            int index = _document.Watches.FindIndex(w => w.Id == watch.Id);
            if (index >= 0) _document.Watches[index] = watch;
        }
        Save();
        Tick();
    }

    /// <summary>Uses one watch's areas for every other running character (same window layout assumed).</summary>
    private CommandResult CopyToAll(AlertWatch source)
    {
        var titles = Snapshot().Clients.Where(t => t != source.SourceTitle).ToArray();
        if (titles.Length == 0) return CommandResult.Error("No other character is running.");
        lock (_gate)
            foreach (string title in titles)
            {
                int index = _document.Watches.FindIndex(w => w.SourceTitle == title);
                if (index >= 0) _document.Watches[index] = _document.Watches[index] with { List = source.List, Count = source.Count };
                else _document.Watches.Add(new AlertWatch
                {
                    Name = $"{title[6..]} Local", SourceTitle = title, List = source.List, Count = source.Count, Enabled = source.Enabled,
                });
            }
        Save();
        Tick();
        return CommandResult.OkFormat($"Areas copied to {titles.Length} other character{(titles.Length == 1 ? "" : "s")}");
    }

    public CommandResult EditOptions(string key, string value)
    {
        AlertOptions options;
        lock (_gate) options = _document.Options;
        bool Flag(out bool result) => bool.TryParse(value, out result);
        switch (key)
        {
            case "paused" when Flag(out bool paused): options = options with { Paused = paused }; break;
            case "flash" when Flag(out bool flash): options = options with { FlashPreviews = flash }; break;
            case "scans" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double scans) && scans is >= 0.5 and <= 5:
                options = options with { ScansPerSecond = scans }; break;
            case "reminder" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds is >= 0 and <= 600:
                options = options with { ReminderSeconds = seconds }; break;
            case "volume" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int volume) && volume is >= 0 and <= 100:
                options = options with { Volume = volume }; break;
            case "sound" or "count-sound":
                if (value is not (AlertOptions.DefaultSound or AlertOptions.NoSound))
                {
                    try { if (!AlertSounds.IsWave(File.ReadAllBytes(value))) return CommandResult.Error("Choose a .wav sound file."); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    { return CommandResult.Error("That sound file cannot be read."); }
                }
                options = key == "sound" ? options with { Sound = value } : options with { CountSound = value };
                break;
            case "webhook-url":
                value = value?.Trim() ?? "";
                if (value.Length > 0 && !DiscordWebhook.IsValidUrl(value))
                    return CommandResult.Error("Paste the webhook URL from Discord (https://discord.com/api/webhooks/...).");
                options = options with { WebhookUrl = value, WebhookEnabled = value.Length > 0 && (options.WebhookEnabled || options.WebhookUrl.Length == 0) };
                _webhookStatus = "";
                break;
            case "webhook-enabled" when Flag(out bool enabled):
                if (enabled && !DiscordWebhook.IsValidUrl(options.WebhookUrl)) return CommandResult.Error("Enter the webhook URL first.");
                options = options with { WebhookEnabled = enabled };
                _webhookStatus = "";
                break;
            default: return CommandResult.Error("Choose a valid value.");
        }
        lock (_gate) _document = _document with { Options = options.Normalize() };
        Save();
        Tick();
        return CommandResult.Ok("Alert settings saved");
    }

    public async Task<CommandResult> TestAsync(string test)
    {
        AlertOptions options;
        lock (_gate) options = _document.Options;
        switch (test)
        {
            case "sound":
            case "count-sound":
                var sound = test == "sound" ? AlertSounds.Resolve(options.Sound, AlertSounds.Hostile) : AlertSounds.Resolve(options.CountSound, AlertSounds.Count);
                if (sound == null) return CommandResult.Error("This alert is set to no sound.");
                _platform.PlaySound(sound, options.Volume / 100.0);
                return CommandResult.Ok("Test sound played");
            case "webhook":
                if (!DiscordWebhook.IsValidUrl(options.WebhookUrl)) return CommandResult.Error("Enter the webhook URL first.");
                Runner sample;
                lock (_gate) sample = _runners.Values.FirstOrDefault(r => r.List != null);
                AlertFrame list = null;
                string character = "Test pilot", system = null;
                if (sample != null)
                {
                    lock (sample.Lock) list = sample.List;
                    character = sample.Watch.Character;
                    system = _systems?.GetSystem(character);
                }
                var png = list is null ? null : AlertPng.Encode(list);
                var payload = DiscordWebhook.ArrivalPayload("Test alert from EVE-O Preview", system, character, "This is a test.", png != null, DateTimeOffset.UtcNow);
                var result = await PostWebhookAsync(options.WebhookUrl, payload, png);
                return result.Outcome == WebhookOutcome.Sent ? CommandResult.Ok("Test message sent to Discord") : CommandResult.Error(result.Message);
            default: return CommandResult.Error("Unknown test.");
        }
    }

    /// <summary>PNG of the latest captures: the count area (if any) above the member list, with detected icons outlined (red alarms, green ignored).</summary>
    public byte[] ReadView(string id)
    {
        Runner runner;
        lock (_gate) _runners.TryGetValue(id, out runner);
        if (runner == null) return null;
        AlertFrame list, count;
        StandingScan scan;
        lock (runner.Lock) { list = runner.List; scan = runner.Scan; count = runner.Watch.Count is { IsUsable: true } ? runner.CountFrame : null; }
        if (list is null) return null;
        var annotated = StandingDetector.Annotate(list, scan);
        return AlertPng.Encode(count is null ? annotated : AlertFrame.Stack(count, annotated));
    }

    /// <summary>The text last recognised in a watch's count area (diagnostics).</summary>
    public string ReadCountText(string id)
    {
        Runner runner;
        lock (_gate) _runners.TryGetValue(id, out runner);
        return runner?.CountText ?? "";
    }

    public void AcknowledgeAll()
    {
        Runner[] runners;
        lock (_gate) runners = _runners.Values.ToArray();
        foreach (var runner in runners) lock (runner.Lock) runner.Monitor.Acknowledge();
        RaiseIfChanged();
    }

    public void TogglePaused() => EditOptions("paused", (!IsPaused).ToString());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _stop.Cancel();
        lock (_gate) foreach (var runner in _runners.Values.ToArray()) Remove(runner);
    }
}

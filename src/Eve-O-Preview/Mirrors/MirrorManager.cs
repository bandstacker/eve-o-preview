using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveOPreview.Configuration;
using EveOPreview.Configuration.Interface;
using EveOPreview.Services;
using EveOPreview.Services.Interface;
using EveOPreview.UI;
using Serilog;

namespace EveOPreview.Mirrors;

/// <summary>
/// Owns the UI mirrors of the current profile: one <see cref="MirrorWindow"/> per definition, shown while
/// its client runs and previews are not temporarily hidden. Creating or reselecting a mirror brings the
/// client to the front and lets the user draw the area on a still of its client area.
/// Implements the portable <see cref="IWorkspaceMirrors"/> page contract through the workspace backend.
/// </summary>
public sealed class MirrorManager : IDisposable
{
    private readonly MirrorStore _store;
    private readonly IConfigurationStorage _storage;
    private readonly IThumbnailConfiguration _configuration;
    private readonly IProcessMonitor _processes;
    private readonly IThumbnailManager _thumbnails;
    private readonly IWindowManager _windows;
    private readonly ILogger _logger;
    private readonly Dictionary<string, MirrorWindow> _open = new();
    private readonly DispatcherTimer _timer;
    private List<MirrorDefinition> _mirrors = new();
    private string _profileFolder;
    private bool _selecting, _started;
    private int _ticks;

    public MirrorManager(MirrorStore store, IConfigurationStorage storage, IThumbnailConfiguration configuration,
        IProcessMonitor processes, IThumbnailManager thumbnails, IWindowManager windows, IGlobalEvents events, ILogger logger)
    {
        _store = store; _storage = storage; _configuration = configuration; _processes = processes;
        _thumbnails = thumbnails; _windows = windows; _logger = logger;
        events.CurrentProfileChanged += _ => Dispatcher.UIThread.Post(() => Reload(force: true));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
    }

    public event Action Changed;
    /// <summary>DWM composes mirrors live, so Windows has no mirror frame rate.</summary>
    public bool SupportsFrameRate => false;
    public IReadOnlyList<MirrorDefinition> Mirrors => _mirrors;

    public void Start()
    {
        if (_started) return;
        _started = true;
        Reload(force: true);
        _timer.Start();
    }

    private void Reload(bool force)
    {
        string folder = _storage.CurrentProfile?.FolderPath;
        if (!force && folder == _profileFolder) return;
        foreach (var window in _open.Values) window.Close();
        _open.Clear();
        _profileFolder = folder;
        _mirrors = _store.Load(folder).ToList();
        Refresh();
        Changed?.Invoke();
    }

    private void Save()
    {
        try { _store.Save(_profileFolder, _mirrors); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.Error(ex, "UI mirrors could not be saved"); }
        Changed?.Invoke();
    }

    private IProcessInfo Client(string title) =>
        _processes.GetAllProcesses().FirstOrDefault(p => p.Title == title && p.MainWindowHandle != IntPtr.Zero);

    public IReadOnlyList<string> AvailableClients() => _processes.GetAllProcesses()
        .Where(p => p.IsEveClient && p.MainWindowHandle != IntPtr.Zero && p.Title.StartsWith("EVE - ", StringComparison.Ordinal))
        .Select(p => p.Title).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();

    private void Refresh()
    {
        // Mirrors follow the previews: Hide all previews and Hide previews outside EVE apply to both.
        bool hidden = _configuration.IsTemporarilyHidingAllThumbnails || _thumbnails.ArePreviewsHiddenOutsideEve || _selecting;
        bool maintain = ++_ticks % 4 == 0;
        foreach (var mirror in _mirrors)
        {
            var client = hidden ? null : Client(mirror.SourceTitle);
            _open.TryGetValue(mirror.Id, out var window);
            if (client == null) { window?.Hide(); continue; }
            if (window == null)
            {
                window = new MirrorWindow(mirror, _logger, SupportsFrameRate);
                window.ActivateRequested += w => ActivateClient(w.Definition.SourceTitle, w.Source);
                window.GeometryChanged += (_, definition) => Replace(definition);
                window.CommandRequested += (w, command) => _ = ExecuteMenuCommand(w.Definition.Id, command);
                _open[mirror.Id] = window;
            }
            window.Attach(client.MainWindowHandle);
            if (maintain) window.Maintain();
        }
        foreach (var id in _open.Keys.Where(id => _mirrors.All(m => m.Id != id)).ToList())
        {
            _open[id].Close();
            _open.Remove(id);
        }
    }

    private void Replace(MirrorDefinition definition)
    {
        int index = _mirrors.FindIndex(m => m.Id == definition.Id);
        if (index < 0) return;
        _mirrors[index] = definition;
        if (_open.TryGetValue(definition.Id, out var window)) window.Update(definition);
        Save();
    }

    private void ActivateClient(string title, IntPtr fallback)
    {
        // Prefer the shared activation path so selection, borders and affinity follow the switch.
        var view = _thumbnails.GetClientByTitle(title);
        if (view != null) view.ThumbnailActivated?.Invoke(view.Id);
        else if (fallback != IntPtr.Zero) _windows.ActivateWindow(fallback);
    }

    public Task<CommandResult> CreateAsync(string sourceTitle) => SelectAsync(sourceTitle, null);

    public Task<CommandResult> ReselectAsync(string id) => _mirrors.FirstOrDefault(m => m.Id == id) is { } mirror
        ? SelectAsync(mirror.SourceTitle, mirror) : Task.FromResult(CommandResult.Error("That mirror no longer exists."));

    private async Task<CommandResult> SelectAsync(string sourceTitle, MirrorDefinition existing)
    {
        if (_selecting) return CommandResult.Error("Finish the current selection first.");
        var client = Client(sourceTitle);
        if (client == null) return CommandResult.Error("Start that client first. Mirrors are drawn on a running client.");
        _selecting = true;
        Refresh();
        try
        {
            ActivateClient(sourceTitle, client.MainWindowHandle);
            await Task.Delay(250);
            if (MirrorNativeMethods.ClientScreenBounds(client.MainWindowHandle) is not { } bounds)
                return CommandResult.Error("The client window is not available. Restore it and try again.");
            var still = await Task.Run(() => CaptureStill(client.MainWindowHandle));
            if (still == null) return CommandResult.Error("The client could not be captured. Make sure it is not minimized and try again.");
            System.Drawing.Rectangle? initial = existing is null ? null : new((int)existing.SourceX, (int)existing.SourceY, (int)existing.SourceWidth, (int)existing.SourceHeight);
            if (await MirrorRegionSelector.SelectAsync(bounds, still, initial) is not { } area) return CommandResult.Cancelled("Selection cancelled");
            if (existing is null)
            {
                string character = sourceTitle.StartsWith("EVE - ", StringComparison.Ordinal) ? sourceTitle[6..] : sourceTitle;
                int number = _mirrors.Count(m => m.SourceTitle == sourceTitle) + 1;
                _mirrors.Add(new MirrorDefinition
                {
                    Name = number == 1 ? $"{character} mirror" : $"{character} mirror {number}",
                    SourceTitle = sourceTitle,
                    SourceX = area.X, SourceY = area.Y, SourceWidth = area.Width, SourceHeight = area.Height,
                    // Appear at actual size just beside the selected area, so it is easy to find.
                    X = bounds.X + area.X + 24, Y = bounds.Y + area.Y + 24, Width = area.Width, Height = area.Height,
                });
                Save();
            }
            else
            {
                Replace((existing with { SourceX = area.X, SourceY = area.Y, SourceWidth = area.Width, SourceHeight = area.Height })
                    .WithZoom(existing.Zoom));
            }
            return CommandResult.Ok("Mirror saved");
        }
        finally
        {
            _selecting = false;
            Refresh();
            // Selection activated this application; give focus back to the client.
            ActivateClient(sourceTitle, client.MainWindowHandle);
        }
    }

    private byte[] CaptureStill(IntPtr window)
    {
        using var image = _windows.GetStaticThumbnail(window);
        if (image == null) return null;
        using var stream = new MemoryStream();
        image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private Task ExecuteMenuCommand(string id, string command)
    {
        int colon = command.IndexOf(':');
        return colon < 0 ? EditAsync(id, command, "") : EditAsync(id, command[..colon], command[(colon + 1)..]);
    }

    public async Task<CommandResult> EditAsync(string id, string action, string value)
    {
        var mirror = _mirrors.FirstOrDefault(m => m.Id == id);
        if (mirror == null) return CommandResult.Error("That mirror no longer exists.");
        int Number(int minimum, int maximum) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? Math.Clamp(n, minimum, maximum) : throw new ArgumentException("Enter a whole number.");
        try
        {
            switch (action)
            {
                case "reselect": return await ReselectAsync(id);
                case "rename":
                    if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 80) return CommandResult.Error("Enter a mirror name (1-80 characters).");
                    Replace(mirror with { Name = value.Trim() });
                    break;
                case "actual-size": Replace(mirror.WithZoom(1)); break;
                case "zoom": Replace(mirror.WithZoom(Number(10, 1000) / 100.0)); break;
                case "opacity": Replace(mirror with { Opacity = Number(20, 100) / 100.0 }); break;
                case "fps": Replace(mirror with { FrameRate = Number(1, 60) }); break;
                case "remove": _mirrors.Remove(mirror); Refresh(); Save(); return CommandResult.Ok("Mirror removed");
                default: return CommandResult.Error("This action is not available.");
            }
            return CommandResult.Ok("Mirror updated");
        }
        catch (ArgumentException ex) { return CommandResult.Error(ex.Message); }
    }

    public MirrorSnapshot Snapshot() => new(
        _mirrors.Select(m => new MirrorItem(m.Id, m.Name, m.SourceTitle, (int)Math.Round(m.SourceWidth), (int)Math.Round(m.SourceHeight),
            m.Width, m.Height, m.Opacity, m.FrameRate)).ToArray(),
        AvailableClients(), _selecting, SupportsFrameRate);

    public void Dispose()
    {
        _timer.Stop();
        foreach (var window in _open.Values) window.Close();
        _open.Clear();
    }
}

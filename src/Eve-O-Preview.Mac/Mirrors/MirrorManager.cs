using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using EveOPreview.Configuration;
using EveOPreview.Configuration.Interface;
using EveOPreview.Mac.Native;
using EveOPreview.Services;
using EveOPreview.Services.Interface;
using Serilog;

namespace EveOPreview.Mac.Mirrors;

/// <summary>
/// Owns the UI mirrors of the current profile: one <see cref="MirrorWindow"/> per definition, shown
/// while its client runs and previews are not temporarily hidden. Creating or reselecting a mirror
/// brings the client to the front and lets the user draw the area on a still of its window.
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
    private bool _selecting;
    private int _maintenance;

    public MirrorManager(MirrorStore store, IConfigurationStorage storage, IThumbnailConfiguration configuration,
        IProcessMonitor processes, IThumbnailManager thumbnails, IWindowManager windows, IGlobalEvents events, ILogger logger)
    {
        _store = store; _storage = storage; _configuration = configuration; _processes = processes;
        _thumbnails = thumbnails; _windows = windows; _logger = logger;
        events.CurrentProfileChanged += _ => Dispatcher.UIThread.Post(Reload);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
    }

    public event Action Changed;
    public IReadOnlyList<MirrorDefinition> Mirrors => _mirrors;
    public bool IsSelecting => _selecting;

    public void Start() { Reload(); _timer.Start(); }

    private void Reload()
    {
        string folder = _storage.CurrentProfile?.FolderPath;
        if (folder == _profileFolder && _mirrors.Count > 0) return;
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
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _logger.Error(ex, "UI mirrors could not be saved"); }
        Changed?.Invoke();
    }

    private IProcessInfo Client(string title) =>
        _processes.GetAllProcesses().FirstOrDefault(p => p.Title == title && p.MainWindowHandle != IntPtr.Zero);

    private void Refresh()
    {
        // Mirrors follow the previews: Hide all previews and Hide previews outside EVE apply to both.
        bool hidden = _configuration.IsTemporarilyHidingAllThumbnails || _thumbnails.ArePreviewsHiddenOutsideEve || _selecting;
        bool maintain = ++_maintenance % 4 == 0;
        foreach (var mirror in _mirrors)
        {
            var client = hidden ? null : Client(mirror.SourceTitle);
            _open.TryGetValue(mirror.Id, out var window);
            if (client == null) { window?.Hide(); continue; }
            if (window == null)
            {
                window = new MirrorWindow(mirror);
                window.ActivateRequested += Activate;
                window.GeometryChanged += (_, definition) => Replace(definition);
                window.CommandRequested += (w, command) => _ = Execute(w.Definition.Id, command);
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

    private void Activate(MirrorWindow window)
    {
        var view = _thumbnails.GetClientByTitle(window.Definition.SourceTitle);
        if (view != null) view.ThumbnailActivated?.Invoke(view.Id);
        else if (window.SourceWindow != IntPtr.Zero) _windows.ActivateWindow(window.SourceWindow);
    }

    /// <summary>Running clients that can be mirrored (full titles).</summary>
    public IReadOnlyList<string> AvailableClients() => _processes.GetAllProcesses()
        .Where(p => p.MainWindowHandle != IntPtr.Zero && p.Title.StartsWith("EVE - ", StringComparison.Ordinal))
        .Select(p => p.Title).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Brings the client forward, lets the user draw the area and creates a mirror. Returns an error or null.</summary>
    public Task<string> CreateAsync(string sourceTitle) => SelectAsync(sourceTitle, null);

    public Task<string> ReselectAsync(string id) =>
        _mirrors.FirstOrDefault(m => m.Id == id) is { } mirror ? SelectAsync(mirror.SourceTitle, mirror) : Task.FromResult("That mirror no longer exists.");

    private async Task<string> SelectAsync(string sourceTitle, MirrorDefinition existing)
    {
        if (_selecting) return "Finish the current selection first.";
        var client = Client(sourceTitle);
        if (client == null) return "Start that client first. Mirrors are drawn on a running client.";
        _selecting = true;
        Refresh();
        try
        {
            var view = _thumbnails.GetClientByTitle(sourceTitle);
            if (view != null) view.ThumbnailActivated?.Invoke(view.Id); else _windows.ActivateWindow(client.MainWindowHandle);
            await Task.Delay(250);
            uint windowId = (uint)client.MainWindowHandle;
            if (!MacNative.TryGetWindowBounds(windowId, out var bounds)) return "The client window is not available.";
            var still = await Task.Run(() => MacNative.CaptureWindow(windowId, (int)(bounds.Width * 2), (int)(bounds.Height * 2)));
            if (still == null) return "The client could not be captured. Check Screen Recording access on the macOS page.";
            // The capture waits for a client that is still resizing; cover its final frame.
            if (!MacNative.TryGetWindowBounds(windowId, out bounds)) return "The client window is not available.";
            Rect? initial = existing is null ? null : new Rect(existing.SourceX, existing.SourceY, existing.SourceWidth, existing.SourceHeight);
            var rect = await RegionSelector.SelectAsync(bounds, still, initial);
            if (rect is not { } area) return null;
            if (existing is null)
            {
                string character = sourceTitle[6..];
                int number = _mirrors.Count(m => m.SourceTitle == sourceTitle) + 1;
                _mirrors.Add(new MirrorDefinition
                {
                    Name = number == 1 ? $"{character} mirror" : $"{character} mirror {number}",
                    SourceTitle = sourceTitle,
                    SourceX = Math.Round(area.X), SourceY = Math.Round(area.Y),
                    SourceWidth = Math.Round(area.Width), SourceHeight = Math.Round(area.Height),
                    // Appear at actual size just beside the selected area, so it is easy to find.
                    X = (int)Math.Round(bounds.X + area.X + 24), Y = (int)Math.Round(bounds.Y + area.Y + 24),
                    Width = (int)Math.Round(area.Width), Height = (int)Math.Round(area.Height),
                });
                Save();
            }
            else
            {
                double zoom = existing.Zoom;
                Replace(existing with
                {
                    SourceX = Math.Round(area.X), SourceY = Math.Round(area.Y),
                    SourceWidth = Math.Round(area.Width), SourceHeight = Math.Round(area.Height),
                    Width = (int)Math.Round(area.Width * zoom), Height = (int)Math.Round(area.Height * zoom),
                });
            }
            return null;
        }
        finally
        {
            _selecting = false;
            Refresh();
            // Selection activated this application; give focus back to the client.
            var view = _thumbnails.GetClientByTitle(sourceTitle);
            if (view != null) view.ThumbnailActivated?.Invoke(view.Id);
        }
    }

    public Task Execute(string id, string command)
    {
        var mirror = _mirrors.FirstOrDefault(m => m.Id == id);
        if (mirror == null) return Task.CompletedTask;
        switch (command)
        {
            case "reselect": return ReselectAsync(id);
            case "actual-size": Replace(mirror with { Width = (int)Math.Round(mirror.SourceWidth), Height = (int)Math.Round(mirror.SourceHeight) }); break;
            case "remove": _mirrors.Remove(mirror); Refresh(); Save(); break;
            default:
                if (command.StartsWith("opacity:", StringComparison.Ordinal))
                    Replace(mirror with { Opacity = int.Parse(command[8..], CultureInfo.InvariantCulture) / 100.0 });
                else if (command.StartsWith("fps:", StringComparison.Ordinal))
                    Replace(mirror with { FrameRate = Math.Clamp(int.Parse(command[4..], CultureInfo.InvariantCulture), 1, 60) });
                else if (command.StartsWith("zoom:", StringComparison.Ordinal))
                {
                    double zoom = int.Parse(command[5..], CultureInfo.InvariantCulture) / 100.0;
                    Replace(mirror with { Width = (int)Math.Round(mirror.SourceWidth * zoom), Height = (int)Math.Round(mirror.SourceHeight * zoom) });
                }
                break;
        }
        return Task.CompletedTask;
    }

    public void Rename(string id, string name)
    {
        var mirror = _mirrors.FirstOrDefault(m => m.Id == id);
        if (mirror == null || string.IsNullOrWhiteSpace(name) || mirror.Name == name.Trim()) return;
        Replace(mirror with { Name = name.Trim() });
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var window in _open.Values) window.Close();
        _open.Clear();
    }
}

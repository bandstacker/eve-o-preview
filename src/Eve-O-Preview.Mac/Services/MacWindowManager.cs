using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using EveOPreview.Mac.Native;
using EveOPreview.Services.Interface;
using Serilog;

namespace EveOPreview.Services.Implementation;

/// <summary>
/// Window control for client windows (CGWindowIDs) and this host's own windows (NSWindow pointers).
/// "Minimize" hides an EVE client process: instant and per client, as each client is its own process.
/// </summary>
public sealed class MacWindowManager : IWindowManager
{
    private readonly IHookService _hookService;
    private readonly ILogger _logger;
    private readonly IProcessMonitor _processMonitor;

    public MacWindowManager(IHookService hookService, ILogger logger, IProcessMonitor processMonitor)
    {
        _hookService = hookService; _logger = logger; _processMonitor = processMonitor;
    }

    public bool IsCompositionEnabled => true;
    public bool IsCurrentlySwitching { get; set; }

    private static bool IsOwnWindow(IntPtr handle) => (ulong)handle.ToInt64() > uint.MaxValue;

    private int PidOf(IntPtr handle)
    {
        if (handle == IntPtr.Zero || IsOwnWindow(handle)) return 0;
        return _processMonitor.LookupCachedProcessByWindowHandle(handle)?.ProcessId ?? MacNative.WindowPid((uint)handle);
    }

    public IntPtr GetForegroundWindowHandle()
    {
        var (window, pid) = MacNative.FrontmostWindow();
        if (pid == Environment.ProcessId)
        {
            var key = MacNative.OwnKeyWindow();
            return key != IntPtr.Zero ? key : _processMonitor.GetMainProcess().MainWindowHandle;
        }
        if (window != 0) return (IntPtr)window;
        // Without Accessibility the focused window is unknown; a client process has one main window.
        var client = _processMonitor.GetAllProcesses().FirstOrDefault(p => p.ProcessId == pid);
        return client?.MainWindowHandle ?? IntPtr.Zero;
    }

    public void ActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        if (IsOwnWindow(handle)) { MacNative.ActivateSelf(); return; }
        int pid = PidOf(handle);
        if (pid == 0) { _logger.Debug("Skipping unavailable client window {Handle}", handle); return; }
        var client = _processMonitor.LookupCachedProcessByWindowHandle(handle);
        if (client?.IsEveClient == true) _ = _hookService.TellEveClientFocusIsComingAsync(handle);
        try
        {
            IsCurrentlySwitching = true;
            if (MacNative.ActivateWindow(pid, (uint)handle) == 0)
                _logger.Debug("Activation of window {Handle} (pid {Pid}) was not confirmed", handle, pid);
        }
        catch (Exception ex) { _logger.Error(ex, "Activating window {Handle} failed", handle); }
        finally { IsCurrentlySwitching = false; }
    }

    public void MinimizeWindow(IntPtr handle, bool enableAnimation)
    {
        int pid = PidOf(handle);
        if (pid == 0) return;
        var client = _processMonitor.LookupCachedProcessByWindowHandle(handle);
        if (client == null || client.IsEveClient) MacNative.HideApplication(pid);
        else MacNative.MinimizeWindow(pid, (uint)handle, 1);
    }

    public void MoveWindow(IntPtr handle, int left, int top, int width, int height)
    {
        int pid = PidOf(handle);
        if (pid != 0) MacNative.SetWindowFrame(pid, (uint)handle, left, top, width, height);
    }

    public void MaximizeWindow(IntPtr handle)
    {
        int pid = PidOf(handle);
        if (pid != 0) MacNative.MaximizeWindow(pid, (uint)handle);
    }

    public (int Left, int Top, int Right, int Bottom) GetWindowPosition(IntPtr handle)
    {
        if (!MacNative.TryGetWindowBounds((uint)handle, out var b)) return (0, 0, 0, 0);
        return ((int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.X + b.Width), (int)Math.Round(b.Y + b.Height));
    }

    public bool IsWindowMaximized(IntPtr handle)
    {
        if (!MacNative.TryGetWindowBounds((uint)handle, out var b)) return false;
        return MacNative.ListScreens().Any(s => Math.Abs(s.VisibleX - b.X) < 2 && Math.Abs(s.VisibleY - b.Y) < 2
            && Math.Abs(s.VisibleWidth - b.Width) < 2 && Math.Abs(s.VisibleHeight - b.Height) < 2);
    }

    public bool IsWindowMinimized(IntPtr handle)
    {
        int pid = PidOf(handle);
        return pid != 0 && (MacNative.IsApplicationHidden(pid) != 0 || MacNative.IsWindowMinimized(pid, (uint)handle) != 0);
    }

    public IDwmThumbnail GetLiveThumbnail(IntPtr destination, IntPtr source)
    {
        var thumbnail = new MacLiveThumbnail(_logger);
        thumbnail.Register(destination, source);
        return thumbnail;
    }

    /// <summary>Compatibility capture (GDI bitmaps) does not exist on macOS; live previews are always used.</summary>
    public Image GetStaticThumbnail(IntPtr source) => null;

    public void PredictUpcomingClient(IntPtr upcomingHandle)
    {
        if (upcomingHandle == IntPtr.Zero) return;
        if (_processMonitor.LookupCachedProcessByWindowHandle(upcomingHandle)?.IsEveClient == true)
            _ = _hookService.TellEveClientFocusIsMaybeComingSoonAsync(upcomingHandle);
    }
}

/// <summary>A ScreenCaptureKit stream drawn into a layer of the preview window (the DWM relationship's counterpart).</summary>
public sealed class MacLiveThumbnail : IDwmThumbnail
{
    private static readonly HashSet<MacLiveThumbnail> Live = new();
    private static int _frameRate = 15;
    private static int _cropTop;
    private readonly ILogger _logger;
    private long _id;

    public MacLiveThumbnail(ILogger logger) { _logger = logger; }

    /// <summary>Applies the macOS preview settings to every live preview.</summary>
    public static void Configure(int frameRate, int cropTopPoints)
    {
        _frameRate = frameRate; _cropTop = cropTopPoints;
        foreach (var thumbnail in Live)
        {
            MacNative.ThumbnailSetFrameRate(thumbnail._id, frameRate);
            MacNative.ThumbnailSetCropTop(thumbnail._id, cropTopPoints);
        }
    }

    public void Register(IntPtr destination, IntPtr source)
    {
        Unregister();
        _id = MacNative.ThumbnailCreate(destination, (uint)source);
        if (_id == 0) { _logger.Warning("Preview for window {Source} could not be created", source); return; }
        MacNative.ThumbnailSetFrameRate(_id, _frameRate);
        if (_cropTop > 0) MacNative.ThumbnailSetCropTop(_id, _cropTop);
        Live.Add(this);
    }

    public void Unregister()
    {
        if (_id == 0) return;
        Live.Remove(this);
        MacNative.ThumbnailDestroy(_id);
        _id = 0;
    }

    public void Move(int left, int top, int right, int bottom)
    {
        if (_id != 0) MacNative.ThumbnailSetBounds(_id, left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public bool Update() => _id != 0 && MacNative.ThumbnailUpdate(_id) != 0;
}

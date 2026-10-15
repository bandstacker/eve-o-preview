using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EveOPreview.Mac.Native;
using Serilog;

namespace EveOPreview.Services;

/// <summary>
/// macOS counterpart of the upstream EVENT_SYSTEM_FOREGROUND observer: application activation
/// notifications report the newly focused client window. Same name and constructor as upstream,
/// because ThumbnailManager creates it directly.
/// </summary>
internal sealed class ForegroundWindowObserver : IDisposable
{
    private static ForegroundWindowObserver _current;
    private readonly Action<nint> _changed;
    private readonly ILogger _logger;

    public unsafe ForegroundWindowObserver(Action<nint> changed, ILogger logger)
    {
        _changed = changed; _logger = logger;
        _current = this;
        MacNative.ObserveApplications(&OnApplication);
    }

    [UnmanagedCallersOnly]
    private static void OnApplication(int pid)
    {
        var observer = _current;
        if (observer == null || pid <= 0) return;
        try
        {
            var (window, _) = MacNative.FrontmostWindow();
            if (window == 0)
                window = (uint)(Implementation.MacProcessMonitor.Current?.GetAllProcesses()
                    .FirstOrDefaultPid(pid) ?? 0);
            if (window != 0) observer._changed((nint)window);
        }
        catch (Exception ex) { observer._logger.Error(ex, "Updating foreground-client appearance failed"); }
    }

    public unsafe void Dispose()
    {
        if (_current != this) return;
        _current = null;
        MacNative.ObserveApplications(null);
    }
}

internal static class ProcessListExtensions
{
    public static long? FirstOrDefaultPid(this System.Collections.Generic.ICollection<IProcessInfo> processes, int pid)
    {
        foreach (var process in processes) if (process.ProcessId == pid) return process.MainWindowHandle.ToInt64();
        return null;
    }
}

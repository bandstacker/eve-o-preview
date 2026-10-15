using System;
using System.Collections.Generic;
using System.Linq;
using EveOPreview.Configuration;
using EveOPreview.Mac.Native;
using Serilog;

namespace EveOPreview.Services.Implementation;

/// <summary>A client window: MainWindowHandle is its CGWindowID and ProcessHandle is its PID (no kernel handles exist).</summary>
public sealed class MacProcessInfo : IProcessInfo
{
    public MacProcessInfo(IntPtr window, int pid, string title, string processName, string bundleId)
    {
        MainWindowHandle = window; ProcessId = pid; Title = title; ProcessName = processName; BundleId = bundleId;
    }
    public IntPtr MainWindowHandle { get; }
    public IntPtr ProcessHandle => new(ProcessId);
    public string Title { get; }
    public int ProcessId { get; }
    public string ProcessName { get; }
    public string BundleId { get; }
    internal MacProcessInfo WithTitle(string title) => new(MainWindowHandle, ProcessId, title, ProcessName, BundleId);
}

/// <summary>
/// Discovers EVE clients and selected applications through the window server. One main window per
/// process, as on Windows. EVE clients report ProcessName "ExeFile" so shared EVE-only routes apply.
/// </summary>
public sealed class MacProcessMonitor : IProcessMonitor
{
    public const string EveProcessName = "ExeFile";
    internal static MacProcessMonitor Current { get; private set; }

    private readonly object _lock = new();
    private readonly Dictionary<IntPtr, IProcessInfo> _cache = new();
    private readonly Dictionary<int, IntPtr> _mainWindowByPid = new();
    private readonly ILogger _logger;
    private readonly IThumbnailConfiguration _configuration;
    private readonly CharacterIdentityCache _identities;
    private IProcessInfo _main = new MacProcessInfo(IntPtr.Zero, Environment.ProcessId, "", "EVE-O Preview", "");

    public MacProcessMonitor(ILogger logger, IThumbnailConfiguration configuration, CharacterIdentityCache identities = null)
    {
        _logger = logger; _configuration = configuration; _identities = identities;
        Current = this;
    }

    /// <summary>The settings window's NSWindow, used for "EVE-O is in the foreground" checks.</summary>
    public void SetMainWindow(IntPtr handle, string title) =>
        _main = new MacProcessInfo(handle, Environment.ProcessId, title, "EVE-O Preview", "");

    public IProcessInfo GetMainProcess() => _main;

    public ICollection<IProcessInfo> GetAllProcesses() { lock (_lock) return _cache.Values.ToList(); }

    public IProcessInfo LookupCachedProcessByWindowHandle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return null;
        lock (_lock) return _cache.TryGetValue(windowHandle, out var info) ? info : null;
    }

    /// <summary>EVE client detection. The native client's executable and bundle names are matched
    /// loosely; the launcher is excluded. Verify against a real installation (docs/ai/macos-port.md).</summary>
    internal static bool IsEveClient(MacNative.WindowEntry window)
    {
        string executable = window.Executable ?? "", bundle = window.BundleId ?? "", owner = window.Owner ?? "";
        if (bundle.Contains("launcher", StringComparison.OrdinalIgnoreCase) || owner.Contains("launcher", StringComparison.OrdinalIgnoreCase)) return false;
        if (executable.Equals("ExeFile", StringComparison.OrdinalIgnoreCase)) return true;
        if (bundle.Contains("ccpgames", StringComparison.OrdinalIgnoreCase) || bundle.Contains("eveonline", StringComparison.OrdinalIgnoreCase)) return true;
        return window.Title == "EVE" || window.Title.StartsWith("EVE - ", StringComparison.Ordinal)
            && (owner.StartsWith("EVE", StringComparison.OrdinalIgnoreCase) || executable.StartsWith("EVE", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsSelectedApplication(MacNative.WindowEntry window)
    {
        var selected = _configuration.PreviewApplications;
        if (selected == null || selected.Count == 0) return false;
        return selected.Any(name => name.Equals(window.Executable, StringComparison.OrdinalIgnoreCase)
            || name.Equals(window.Owner, StringComparison.OrdinalIgnoreCase));
    }

    public void GetUpdatedProcesses(out ICollection<IProcessInfo> addedProcesses, out ICollection<IProcessInfo> updatedProcesses, out ICollection<IProcessInfo> removedProcesses)
    {
        addedProcesses = new List<IProcessInfo>(8);
        updatedProcesses = new List<IProcessInfo>(8);
        removedProcesses = new List<IProcessInfo>(8);
        MacNative.WindowEntry[] windows;
        try { windows = MacNative.ListWindows(accessibilityTitles: false); }
        catch (Exception ex) { _logger.Warning(ex, "Window discovery failed"); return; }

        // Choose one main window per candidate process; keep the previous choice while it exists.
        var candidates = windows.Select(w => (Window: w, Eve: IsEveClient(w)))
            .Where(c => c.Eve || IsSelectedApplication(c.Window))
            .GroupBy(c => c.Window.Pid);
        var seen = new Dictionary<IntPtr, MacProcessInfo>();
        foreach (var group in candidates)
        {
            int pid = group.Key;
            var chosen = _mainWindowByPid.TryGetValue(pid, out var previous) && group.Any(c => (IntPtr)c.Window.Id == previous)
                ? group.First(c => (IntPtr)c.Window.Id == previous)
                : group.OrderByDescending(c => c.Window.OnScreen).ThenByDescending(c => c.Window.Width * c.Window.Height).First();
            var window = chosen.Window;
            string title = window.Title;
            if (string.IsNullOrEmpty(title)) title = MacNative.WindowTitle(pid, window.Id);
            if (string.IsNullOrEmpty(title)) title = chosen.Eve ? "EVE" : window.Owner;
            string name = chosen.Eve ? EveProcessName : (string.IsNullOrEmpty(window.Executable) ? window.Owner : window.Executable);
            seen[(IntPtr)window.Id] = new MacProcessInfo((IntPtr)window.Id, pid, title, name, window.BundleId);
            _mainWindowByPid[pid] = (IntPtr)window.Id;
        }

        lock (_lock)
        {
            foreach (var (handle, info) in seen)
            {
                if (_cache.TryGetValue(handle, out var cached))
                {
                    if (cached.ProcessId != info.ProcessId)
                    {
                        removedProcesses.Add(cached);
                        _cache[handle] = info;
                        addedProcesses.Add(info);
                    }
                    else if (cached.Title != info.Title)
                    {
                        var renamed = ((MacProcessInfo)cached).WithTitle(info.Title);
                        _cache[handle] = renamed;
                        updatedProcesses.Add(renamed);
                    }
                }
                else
                {
                    _cache[handle] = info;
                    addedProcesses.Add(info);
                }
            }
            foreach (var handle in _cache.Keys.Where(h => !seen.ContainsKey(h)).ToList())
            {
                removedProcesses.Add(_cache[handle]);
                _cache.Remove(handle);
            }
            foreach (var pid in _mainWindowByPid.Keys.Where(pid => !seen.Values.Any(v => v.ProcessId == pid)).ToList())
                _mainWindowByPid.Remove(pid);
        }
        if (addedProcesses.Count + removedProcesses.Count + updatedProcesses.Count > 0)
            _logger.Debug("Discovery: {Added} added, {Updated} renamed, {Removed} removed", addedProcesses.Count, updatedProcesses.Count, removedProcesses.Count);
        _identities?.ObserveProcesses(GetAllProcesses());
    }
}

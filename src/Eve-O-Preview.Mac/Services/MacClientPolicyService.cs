using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveOPreview.Configuration;
using EveOPreview.Mac.Native;
using EveOPreview.Services.Interface;
using Serilog;

namespace EveOPreview.Services.Implementation;

/// <summary>
/// macOS replacement for Robin (IHookService) and CPU placement (ICpuAffinityService). Without code
/// injection it controls whole processes:
/// - Efficiency cores: background clients enter Darwin background policy; the active and predicted
///   clients and login screens are exempt, so a reconnecting client never opens throttled sockets.
/// - Background mute: every EVE client except the active one is muted through a Core Audio tap.
/// FPS limits and selective sound muting are not available and report false.
/// </summary>
public sealed class MacClientPolicyService : IHookService, ICpuAffinityService, IDisposable
{
    private readonly ILogger _logger;
    private readonly IThumbnailConfiguration _configuration;
    private readonly MacSettings _settings;
    private readonly IProcessMonitor _processes;
    private readonly Dictionary<int, bool> _background = new();
    private readonly Dictionary<int, bool> _muted = new();
    private readonly DispatcherTimer _retry;
    private int _activePid, _predictedPid;
    private bool _stopped;

    public MacClientPolicyService(ILogger logger, IThumbnailConfiguration configuration, MacSettings settings, IProcessMonitor processes)
    {
        _logger = logger; _configuration = configuration; _settings = settings; _processes = processes;
        _settings.Changed += () => Dispatcher.UIThread.Post(Reapply);
        // Audio objects appear after a client first initializes sound; retry pending mutes.
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _retry.Tick += (_, _) => Reapply();
        _retry.Start();
    }

    private IEnumerable<IProcessInfo> EveClients => _processes.GetAllProcesses().Where(p => p.IsEveClient);

    private int PidOf(IntPtr window) => _processes.LookupCachedProcessByWindowHandle(window)?.ProcessId ?? 0;

    private void Reapply()
    {
        if (_stopped) return;
        if (_activePid == 0) _activePid = MacNative.FrontmostPid();
        Apply(EveClients.ToList());
    }

    private void Apply(IReadOnlyCollection<IProcessInfo> clients)
    {
        bool efficiency = _configuration.EnableAutomaticCpuAffinity;
        bool mute = _settings.MuteBackgroundClients;
        foreach (var client in clients)
        {
            int pid = client.ProcessId;
            bool active = pid == _activePid;
            bool exempt = active || pid == _predictedPid || client.IsLoginClient;
            SetBackground(pid, efficiency && !exempt);
            SetMuted(pid, mute && !active);
        }
    }

    private void SetBackground(int pid, bool background)
    {
        if (_background.TryGetValue(pid, out bool current) && current == background) return;
        if (MacNative.SetBackgroundPolicy(pid, background ? 1 : 0) != 0) _background[pid] = background;
    }

    private void SetMuted(int pid, bool muted)
    {
        if (_muted.TryGetValue(pid, out bool current) && current == muted) return;
        int result = MacNative.AudioSetMuted(pid, muted ? 1 : 0);
        if (result == 1) _muted[pid] = muted;
        else if (result < 0) _logger.Debug("Audio mute for pid {Pid} failed ({Result}); check System Audio Recording access", pid, result);
    }

    private void Release(int pid)
    {
        SetBackground(pid, false);
        SetMuted(pid, false);
    }

    // ICpuAffinityService

    public void UpdateAffinity(IProcessInfo active, IProcessInfo next, IProcessInfo prev, IEnumerable<IProcessInfo> allClients)
    {
        if (active != null) _activePid = active.ProcessId;
        _predictedPid = next?.ProcessId ?? 0;
        Apply(allClients.ToList());
    }

    public void ResetAll(IEnumerable<IProcessInfo> allClients)
    {
        foreach (var client in allClients) SetBackground(client.ProcessId, false);
    }

    public void Stop(IEnumerable<IProcessInfo> allClients)
    {
        foreach (var client in allClients) Release(client.ProcessId);
    }

    // IHookService

    public bool Ping(IntPtr handle) => false;
    public Task<string> GetVersionAsync(IntPtr handle) => Task.FromResult("");

    /// <summary>Releases the incoming client before activation so it is never throttled or silent when shown.</summary>
    public Task TellEveClientFocusIsComingAsync(IntPtr handle)
    {
        int pid = PidOf(handle);
        if (pid == 0) return Task.CompletedTask;
        _activePid = pid;
        Release(pid);
        return Task.CompletedTask;
    }

    public Task TellEveClientFocusIsMaybeComingSoonAsync(IntPtr handle, int timeoutMs = 5000)
    {
        int pid = PidOf(handle);
        if (pid == 0) return Task.CompletedTask;
        _predictedPid = pid;
        SetBackground(pid, false);
        return Task.CompletedTask;
    }

    public Task<bool> UpdateTargetFpsAsync(IntPtr handle) => Task.FromResult(false);
    public Task<bool> DisableFpsLimiterAsync(IntPtr handle) => Task.FromResult(false);
    public Task<bool> UpdateMutedAudioAsync(IntPtr handle) => Task.FromResult(false);

    public Task TryInstallHooksAsync(IProcessInfo procInfo)
    {
        if (procInfo?.IsEveClient == true) Dispatcher.UIThread.Post(Reapply);
        return Task.CompletedTask;
    }

    public void ForgetClient(IProcessInfo process)
    {
        if (process == null) return;
        // A disappearing window may belong to a client that is relogging: never leave it throttled.
        Release(process.ProcessId);
        _background.Remove(process.ProcessId);
        _muted.Remove(process.ProcessId);
    }

    public Task StopAsync(IEnumerable<IProcessInfo> processes)
    {
        _stopped = true;
        _retry.Stop();
        foreach (var process in processes) Release(process.ProcessId);
        foreach (int pid in _background.Keys.ToList()) SetBackground(pid, false);
        MacNative.AudioUnmuteAll();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_stopped) _ = StopAsync(EveClients.ToList());
    }
}

using System;
using System.Threading.Tasks;
using EveOPreview.Alerts;
using EveOPreview.UI;

namespace EveOPreview.View;

/// <summary>Local alerts capability for the portable alerts page; the host's manager owns capture and storage.</summary>
public sealed partial class WindowsWorkspaceBackend : IWorkspaceAlerts
{
    private AlertManager _alertHost;

    public AlertManager AlertHost
    {
        get => _alertHost;
        set
        {
            if (_alertHost != null) _alertHost.Changed -= OnAlertsChanged;
            _alertHost = value;
            if (_alertHost != null) _alertHost.Changed += OnAlertsChanged;
        }
    }

    public event Action AlertsChanged;
    private void OnAlertsChanged() => AlertsChanged?.Invoke();

    private static readonly Task<CommandResult> AlertsUnavailable = Task.FromResult(CommandResult.Error("Local alerts are not available."));

    public AlertSnapshot ReadAlerts() => _alertHost?.Snapshot()
        ?? new AlertSnapshot(new AlertOptionsItem(false, 2, 30, true, "default", "default", 80, "", false, ""), [], [], false, false);

    public Task<CommandResult> CreateAlertWatchAsync(string sourceTitle) => _alertHost?.CreateAsync(sourceTitle) ?? AlertsUnavailable;

    public Task<CommandResult> EditAlertWatchAsync(string id, string action, string value = "") =>
        _alertHost?.EditWatchAsync(id, action, value) ?? AlertsUnavailable;

    public Task<CommandResult> EditAlertOptionsAsync(string key, string value) =>
        _alertHost is null ? AlertsUnavailable : Task.FromResult(_alertHost.EditOptions(key, value));

    public Task<CommandResult> TestAlertAsync(string test) => _alertHost?.TestAsync(test) ?? AlertsUnavailable;

    public byte[] ReadAlertView(string id) => _alertHost?.ReadView(id);
}

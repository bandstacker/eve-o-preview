using System;
using System.Threading.Tasks;
using EveOPreview.Mirrors;
using EveOPreview.UI;

namespace EveOPreview.View;

/// <summary>UI mirrors capability for the portable mirrors page; the host manager owns windows and storage.</summary>
public sealed partial class WindowsWorkspaceBackend : IWorkspaceMirrors
{
    private MirrorManager _mirrorHost;

    public MirrorManager MirrorHost
    {
        get => _mirrorHost;
        set
        {
            if (_mirrorHost != null) _mirrorHost.Changed -= OnMirrorsChanged;
            _mirrorHost = value;
            if (_mirrorHost != null) _mirrorHost.Changed += OnMirrorsChanged;
        }
    }

    public event Action MirrorsChanged;
    private void OnMirrorsChanged() => MirrorsChanged?.Invoke();

    public MirrorSnapshot ReadMirrors() => _mirrorHost?.Snapshot() ?? new MirrorSnapshot([], [], false, false);

    public Task<CommandResult> CreateMirrorAsync(string sourceTitle) =>
        _mirrorHost?.CreateAsync(sourceTitle) ?? Task.FromResult(CommandResult.Error("UI mirrors are not available."));

    public Task<CommandResult> ReselectMirrorAsync(string id) =>
        _mirrorHost?.ReselectAsync(id) ?? Task.FromResult(CommandResult.Error("UI mirrors are not available."));

    public Task<CommandResult> EditMirrorAsync(string id, string action, string value = "") =>
        _mirrorHost?.EditAsync(id, action, value) ?? Task.FromResult(CommandResult.Error("UI mirrors are not available."));
}

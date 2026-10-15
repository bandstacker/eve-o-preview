using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using EveOPreview.Mac.Native;
using EveOPreview.Mediator.Messages;
using EveOPreview.Services.Implementation;
using EveOPreview.UI;

namespace EveOPreview.View;

/// <summary>macOS parts of the shared workspace backend: application picker and macOS-only settings.</summary>
public sealed partial class WindowsWorkspaceBackend
{
    internal static MacSettings MacSettings { get; set; }

    private async Task<CommandResult> EditPreviewApplicationAsync(WorkspaceCommand command)
    {
        bool add = command.Action == "application-add";
        if (add && !(await GetPreviewApplicationsAsync()).Any(p => p.ProcessName.Equals(command.Target, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Error("Select a running application with a window.");
        var before = _configuration.PreviewApplications;
        var selected = (before ?? new()).ToList();
        selected.RemoveAll(p => p.Equals(command.Target, StringComparison.OrdinalIgnoreCase));
        if (add) selected.Add(command.Target);
        _configuration.PreviewApplications = selected;
        try { await _mediator.Send(new SaveConfiguration()); }
        catch { _configuration.PreviewApplications = before; throw; }
        return CommandResult.Ok(add ? "Application thumbnails enabled" : "Application thumbnails removed");
    }

    /// <summary>Running applications with at least one window, excluding EVE clients and this host.</summary>
    public Task<IReadOnlyList<PreviewApplication>> GetPreviewApplicationsAsync() => Task.Run<IReadOnlyList<PreviewApplication>>(() =>
    {
        var windows = MacNative.ListWindows(accessibilityTitles: false);
        return windows.Where(w => !MacProcessMonitor.IsEveClient(w))
            .GroupBy(w => w.Pid)
            .Select(g => g.OrderByDescending(w => w.OnScreen).ThenByDescending(w => w.Width * w.Height).First())
            .Select(w => new PreviewApplication(string.IsNullOrEmpty(w.Executable) ? w.Owner : w.Executable,
                string.IsNullOrEmpty(w.Title) ? w.Owner : w.Title, w.Pid))
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId).ToArray();
    });

    partial void BuildPlatformSettings()
    {
        var mac = MacSettings;
        if (mac is null) return;
        Bind("PreviewFrameRate", () => mac.PreviewFrameRate, x => mac.SetPreviewFrameRate(Integer(x)));
        Bind("AudioMuteBackgroundClients", () => mac.MuteBackgroundClients, x => mac.SetMuteBackgroundClients(bool.Parse(x)));
        Bind("MacTitleBarCrop", () => mac.TitleBarCropPoints, x => mac.SetTitleBarCropPoints(Integer(x)));
    }
}

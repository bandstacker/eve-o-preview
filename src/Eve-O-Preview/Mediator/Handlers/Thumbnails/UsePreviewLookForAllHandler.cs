using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveOPreview.Configuration;
using EveOPreview.Mediator.Messages;
using EveOPreview.UI;
using EveOPreview.View;
using MediatR;

namespace EveOPreview.Mediator.Handlers.Thumbnails;

/// <summary>
/// Copies one character's preview look to every existing and future preview: its border color becomes
/// the default highlight color and its augment settings the shared default; per-character overrides of
/// both are removed. Sizes and positions are unchanged.
/// </summary>
public sealed class UsePreviewLookForAllHandler(IThumbnailConfiguration configuration, IMainFormView view, IMediator mediator,
    IWorkspaceCombatLogs combatLogs = null) : IRequestHandler<UsePreviewLookForAll>
{
    public async Task Handle(UsePreviewLookForAll request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return;
        var colors = configuration.PerClientActiveClientHighlightColor;
        if (colors.TryGetValue(request.Title, out var color))
        {
            // Keep the workspace view in step: settings saves copy the view back into the profile.
            configuration.ActiveClientHighlightColor = color;
            view.ActiveClientHighlightColor = color;
        }
        colors.Clear();
        await mediator.Send(new SaveConfiguration(), cancellationToken);
        if (combatLogs is null) return;
        var settings = combatLogs.ReadLogSettings();
        var overlay = settings.Overlays.GetValueOrDefault(request.Title) ?? settings.DefaultOverlay;
        await combatLogs.SaveLogSettingsAsync(settings with
        {
            DefaultOverlay = overlay,
            Overlays = new Dictionary<string, LogOverlayOptions>(StringComparer.Ordinal),
        });
    }
}

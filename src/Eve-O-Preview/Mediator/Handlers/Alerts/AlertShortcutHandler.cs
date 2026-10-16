using System.Threading;
using System.Threading.Tasks;
using EveOPreview.Alerts;
using EveOPreview.Mediator.Messages;
using MediatR;

namespace EveOPreview.Mediator.Handlers.Alerts;

/// <summary>Routes alert shortcuts to the host's alert manager; hosts without Local alerts ignore them.</summary>
public sealed class AlertShortcutHandler(AlertManager alerts = null) : INotificationHandler<AlertShortcut>
{
    public Task Handle(AlertShortcut notification, CancellationToken cancellationToken)
    {
        if (alerts is null) return Task.CompletedTask;
        if (notification.Action == AlertShortcutAction.Acknowledge) alerts.AcknowledgeAll();
        else alerts.TogglePaused();
        return Task.CompletedTask;
    }
}

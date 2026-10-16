using MediatR;

namespace EveOPreview.Mediator.Messages;

public enum AlertShortcutAction { Acknowledge, TogglePause }

/// <summary>A global shortcut for Local alerts was pressed.</summary>
public sealed record AlertShortcut(AlertShortcutAction Action) : INotification;

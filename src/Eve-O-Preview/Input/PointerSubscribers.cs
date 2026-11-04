using System;
using System.Diagnostics;

namespace EveOPreview.Input;

/// <summary>
/// The handlers of one global pointer event, each with the time it subscribed; a handler never receives an
/// event that happened before that. The hook captures a click's release before the clicked window receives it,
/// but its queue runs at Input priority, which Avalonia runs after pending OS input. A menu action that starts a
/// gesture can therefore subscribe before its own click's release is delivered, and would read that release as
/// the end of the gesture. Owned by the UI thread. Copy-on-write, so raising allocates nothing and handlers can
/// change the list while it runs.
/// </summary>
internal sealed class PointerSubscribers
{
    private (EventHandler<GlobalPointerEventArgs> Handler, long Since)[] _handlers = [];

    public bool IsEmpty => _handlers.Length == 0;

    public void Add(EventHandler<GlobalPointerEventArgs> handler) => Add(handler, Stopwatch.GetTimestamp());

    internal void Add(EventHandler<GlobalPointerEventArgs> handler, long since)
    {
        if (handler == null) return;
        var next = new (EventHandler<GlobalPointerEventArgs>, long)[_handlers.Length + 1];
        _handlers.CopyTo(next, 0);
        next[^1] = (handler, since);
        _handlers = next;
    }

    /// <summary>Removes the most recent subscription of this handler, like removing it from a delegate.</summary>
    public void Remove(EventHandler<GlobalPointerEventArgs> handler)
    {
        int index = Array.FindLastIndex(_handlers, entry => entry.Handler == handler);
        if (index < 0) return;
        var next = new (EventHandler<GlobalPointerEventArgs>, long)[_handlers.Length - 1];
        Array.Copy(_handlers, next, index);
        Array.Copy(_handlers, index + 1, next, index, next.Length - index);
        _handlers = next;
    }

    public void Clear() => _handlers = [];

    public void Raise(object sender, GlobalPointerEventArgs args)
    {
        foreach (var (handler, since) in _handlers)
            if (since < args.Occurred) handler(sender, args);
    }
}

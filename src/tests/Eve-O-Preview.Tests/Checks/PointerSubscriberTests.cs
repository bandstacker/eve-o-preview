using System;
using System.Collections.Generic;
using System.Drawing;
using EveOPreview.Input;
using Xunit;

namespace EveOPreview.Tests.Checks;

// Platform-neutral: also linked into the macOS tests.
public class PointerSubscriberTests
{
    private readonly Queue<Action> _jobs = new();
    private readonly PointerSubscribers _move = new(), _up = new();
    private readonly PointerDispatchQueue _queue;

    public PointerSubscriberTests() => _queue = new PointerDispatchQueue(_jobs.Enqueue, () => 1,
        args => _move.Raise(this, args), args => _up.Raise(this, args));

    private void Hook(Point position, PointerButtons button, long occurred) =>
        _queue.Enqueue(position, button, PointerButtons.None, ShortcutKeys.None, 1, occurred);

    [Fact]
    public void AMenuActionDoesNotReceiveTheReleaseOfItsOwnClick()
    {
        var menu = new Point(500, 300);
        var dismissals = new List<Point>();
        var resized = new List<Point>();
        var ended = new List<Point>();
        _up.Add((_, args) => dismissals.Add(args.Location), since: 10); // The open menu's outside-click check.

        // The hook sees the release first, but its queue waits for pending OS input.
        Hook(menu, PointerButtons.Left, occurred: 20);
        // So the menu item's click runs now and starts the gesture.
        _move.Add((_, args) => resized.Add(args.Location), since: 30);
        _up.Add((_, args) => ended.Add(args.Location), since: 30);
        _jobs.Dequeue()();

        Assert.Equal([menu], dismissals); // Subscribed earlier: still sees the click.
        Assert.Empty(resized); // No jump to the menu item's position.
        Assert.Empty(ended); // The gesture is still running.

        // Later input belongs to the gesture.
        Hook(new Point(640, 400), PointerButtons.None, occurred: 40);
        _jobs.Dequeue()();
        Assert.Equal([new Point(640, 400)], resized);
        Assert.Empty(ended);

        Hook(new Point(650, 410), PointerButtons.Left, occurred: 50);
        _jobs.Dequeue()();
        Assert.Equal([new Point(640, 400), new Point(650, 410)], resized);
        Assert.Equal([new Point(650, 410)], ended);
    }

    [Fact]
    public void RemovingTakesTheLatestSubscriptionLikeADelegate()
    {
        int calls = 0;
        EventHandler<GlobalPointerEventArgs> handler = (_, _) => calls++;
        var args = new GlobalPointerEventArgs(PointerButtons.Left, PointerButtons.None, Point.Empty, ShortcutKeys.None) { Occurred = 100 };
        _up.Add(handler, since: 1);
        _up.Add(handler, since: 200);
        _up.Remove(handler);
        _up.Raise(this, args);
        Assert.Equal(1, calls); // The earlier subscription remains, and it is older than the event.
        _up.Remove(handler);
        Assert.True(_up.IsEmpty);
        _up.Remove(handler); // Removing an absent handler is harmless, like a delegate.
        _up.Add(null, since: 1);
        Assert.True(_up.IsEmpty);
    }

    [Fact]
    public void HandlersMayChangeTheListWhileItIsRaised()
    {
        var calls = new List<string>();
        EventHandler<GlobalPointerEventArgs> second = (_, _) => calls.Add("second");
        var args = new GlobalPointerEventArgs(PointerButtons.Left, PointerButtons.None, Point.Empty, ShortcutKeys.None) { Occurred = 100 };
        _up.Add((_, _) =>
        {
            calls.Add("first");
            _up.Remove(second);
            _up.Add((_, _) => calls.Add("third"), since: 150);
        }, since: 1);
        _up.Add(second, since: 2);
        _up.Raise(this, args);
        Assert.Equal(["first", "second"], calls); // This event used the list as it was when raised, like a delegate.
        calls.Clear();
        _up.Raise(this, new(PointerButtons.Left, PointerButtons.None, Point.Empty, ShortcutKeys.None) { Occurred = 200 });
        Assert.Equal(["first", "third"], calls);
    }

    [Fact]
    public void RaisingAllocatesNothing()
    {
        int calls = 0;
        _move.Add((_, _) => calls++, since: 1);
        _move.Add((_, _) => calls++, since: 500);
        var args = new GlobalPointerEventArgs(PointerButtons.None, PointerButtons.None, Point.Empty, ShortcutKeys.None) { Occurred = 100 };
        _move.Raise(this, args);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _move.Raise(this, args);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

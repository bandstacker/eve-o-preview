using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using EveOPreview.Mac.Native;
using EveOPreview.Services.Implementation;
using Serilog;

namespace EveOPreview.Input;

/// <summary>
/// Global pointer position, buttons and modifiers in preview pixels (Quartz points). Events are
/// delivered on the UI thread like the Windows low-level mouse hook adapter.
/// </summary>
public sealed class MacGlobalPointerInput : IGlobalPointerInput
{
    private static MacGlobalPointerInput _current;
    private readonly ILogger _logger;
    private EventHandler<GlobalPointerEventArgs> _mouseMove;
    // Windows' low-level hook sees a click before the clicked window does. Here the event reaches
    // the UI thread afterwards, so a handler subscribed while that click is processed (for example
    // a menu action that starts move mode) must not receive the same click's release.
    private readonly List<(EventHandler<GlobalPointerEventArgs> Handler, long Since)> _mouseUp = new();
    private bool _tapRunning;
    private int _movePending;
    private bool _disposed;

    public MacGlobalPointerInput(ILogger logger)
    {
        _logger = logger;
        _current = this;
    }

    public event EventHandler<GlobalPointerEventArgs> MouseMove
    {
        add { _mouseMove += value; EnsureTap(); }
        remove { _mouseMove -= value; }
    }

    public event EventHandler<GlobalPointerEventArgs> MouseUp
    {
        add { _mouseUp.Add((value, Stopwatch.GetTimestamp())); EnsureTap(); }
        remove
        {
            int index = _mouseUp.FindLastIndex(x => x.Handler == value);
            if (index >= 0) _mouseUp.RemoveAt(index);
        }
    }

    private void RaiseMouseUp(GlobalPointerEventArgs args, long occurred)
    {
        foreach (var (handler, since) in _mouseUp.ToArray())
            if (since < occurred && _mouseUp.Contains((handler, since))) handler(this, args);
    }

    public Point Position
    {
        get { var (x, y) = MacNative.PointerLocation(); return new((int)Math.Round(x), (int)Math.Round(y)); }
        set => MacNative.PointerWarp(value.X, value.Y);
    }

    public PointerButtons Buttons => ToButtons(MacNative.PressedButtons());

    public ShortcutKeys Modifiers => MacKeyCodes.Modifiers(MacNative.ModifierFlags());

    private static PointerButtons ToButtons(int mask)
    {
        var buttons = PointerButtons.None;
        if ((mask & 1) != 0) buttons |= PointerButtons.Left;
        if ((mask & 2) != 0) buttons |= PointerButtons.Right;
        if ((mask & 4) != 0) buttons |= PointerButtons.Middle;
        if ((mask & 8) != 0) buttons |= PointerButtons.XButton1;
        if ((mask & 16) != 0) buttons |= PointerButtons.XButton2;
        return buttons;
    }

    private static PointerButtons ToButton(int button) => button switch
    {
        0 => PointerButtons.Left, 1 => PointerButtons.Right, 2 => PointerButtons.Middle,
        3 => PointerButtons.XButton1, 4 => PointerButtons.XButton2, _ => PointerButtons.None,
    };

    private unsafe void EnsureTap()
    {
        if (_tapRunning || _disposed) return;
        _tapRunning = MacNative.PointerTapStart(&OnPointer) != 0;
        if (!_tapRunning) _logger.Warning("Pointer event tap unavailable; move/resize gestures need Accessibility access");
    }

    [UnmanagedCallersOnly]
    private static void OnPointer(int kind, int button, double x, double y, ulong flags)
    {
        var input = _current;
        if (input == null) return;
        var location = new Point((int)Math.Round(x), (int)Math.Round(y));
        var modifiers = MacKeyCodes.Modifiers(flags);
        if (kind == 0)
        {
            // Coalesce moves: one pending UI dispatch reads the latest position.
            if (System.Threading.Interlocked.Exchange(ref input._movePending, 1) == 1) return;
            Dispatcher.UIThread.Post(() =>
            {
                System.Threading.Interlocked.Exchange(ref input._movePending, 0);
                var args = new GlobalPointerEventArgs(PointerButtons.None, input.Buttons, input.Position, input.Modifiers);
                input._mouseMove?.Invoke(input, args);
            }, DispatcherPriority.Input);
        }
        else if (kind == 2)
        {
            var released = ToButton(button);
            long occurred = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
                input.RaiseMouseUp(new GlobalPointerEventArgs(released, input.Buttons, location, modifiers), occurred),
                DispatcherPriority.Send);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tapRunning) MacNative.PointerTapStop();
        if (_current == this) _current = null;
    }
}

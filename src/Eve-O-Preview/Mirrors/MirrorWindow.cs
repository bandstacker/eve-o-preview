using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using EveOPreview.View.CustomControl;
using EveOPreview.View.Rendering;
using Serilog;
using static EveOPreview.Mirrors.MirrorNativeMethods;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace EveOPreview.Mirrors;

/// <summary>
/// A floating, nonactivating window showing one DWM-composed area of a client window. Pointer input is
/// handled from native messages, as in ThumbnailView, so a click never focuses this application:
/// drag inside to move, drag within <see cref="EdgeSize"/> of an edge to resize with the source aspect
/// ratio, click to switch to the client, right-click for options.
/// </summary>
internal sealed class MirrorWindow : Window
{
    private const int EdgeSize = 10, DragThreshold = 4, HoverBorder = 2;
    private readonly WindowsPreviewWindowAdapter _native;
    private readonly MirrorImage _image;
    private readonly ContextMenu _menu = new();
    private readonly bool _frameRateMenu;
    private IntPtr _source;
    private bool _hover, _tracking;
    private Gesture _gesture;
    private (int X, int Y) _pointerStart;
    private Rectangle _frameStart;
    private MirrorGeometry.Edges _edges;

    private enum Gesture { None, Pending, Move, Resize }

    public MirrorWindow(MirrorDefinition definition, ILogger logger, bool frameRateMenu = false)
    {
        Definition = definition;
        _frameRateMenu = frameRateMenu;
        Title = "EVE-O UI mirror";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false; ShowActivated = false; CanResize = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        _image = new MirrorImage(logger);
        _native = new WindowsPreviewWindowAdapter(this);
        Win32Properties.AddWndProcHookCallback(this, Messages);
        Opened += (_, _) => { _native.EnsureStyles(); ApplyGeometry(); Register(); };
        Closed += (_, _) => { _image.Dispose(); Win32Properties.RemoveWndProcHookCallback(this, Messages); _native.Dispose(); };
        BuildMenu();
    }

    public MirrorDefinition Definition { get; private set; }
    public IntPtr Source => _source;
    public event Action<MirrorWindow> ActivateRequested;
    public event Action<MirrorWindow, MirrorDefinition> GeometryChanged;
    public event Action<MirrorWindow, string> CommandRequested;

    /// <summary>Shows the mirror for this client window, re-registering when the client changed.</summary>
    public void Attach(IntPtr source)
    {
        if (_source != source) { _source = source; _image.Unregister(); }
        if (!IsVisible) Show();
        else if (!_image.IsRegistered) Register();
        _native.Restore(topmost: true, raise: false);
    }

    /// <summary>Re-registers an image whose source closed or whose DWM relationship failed.</summary>
    public void Maintain()
    {
        if (IsVisible && !_image.Update()) Register();
    }

    public void Update(MirrorDefinition definition)
    {
        var previous = Definition;
        Definition = definition;
        if (previous.X != definition.X || previous.Y != definition.Y || previous.Width != definition.Width || previous.Height != definition.Height)
            ApplyGeometry();
        _native.SetOpacity(definition.Opacity);
        ApplyImage();
    }

    private void Register()
    {
        if (_source == IntPtr.Zero || _native.Handle == IntPtr.Zero) return;
        ApplyImage(update: false);
        _image.Register(_native.Handle, _source);
    }

    private void ApplyGeometry()
    {
        _native.ClientSize = new(Math.Max(16, Definition.Width), Math.Max(16, Definition.Height));
        _native.Location = new(Definition.X, Definition.Y);
        _native.SetOpacity(Definition.Opacity);
        ApplyImage();
    }

    private void ApplyImage(bool update = true)
    {
        var size = _native.ClientSize;
        int inset = _hover ? HoverBorder : 0;
        _image.SetSource(Definition.SourceX, Definition.SourceY, Definition.SourceWidth, Definition.SourceHeight);
        _image.SetDestination(inset, inset, Math.Max(inset + 1, size.Width - inset), Math.Max(inset + 1, size.Height - inset));
        if (update) _image.Update();
    }

    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        // DWM draws above the window content; inset the image to reveal a frame.
        Background = hover ? new SolidColorBrush(Color.Parse("#6D9FFF")) : Brushes.Black;
        ApplyImage();
    }

    private IntPtr Messages(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_SETCURSOR when (lParam.ToInt64() & 0xffff) == HTCLIENT:
                var cursor = CursorPosition();
                var local = _native.PointToClient(new Point(cursor.X, cursor.Y));
                var size = _native.ClientSize;
                SetStandardCursor(CursorFor(_gesture == Gesture.Resize ? _edges : MirrorGeometry.HitEdges(local.X, local.Y, size.Width, size.Height, EdgeSize)));
                handled = true;
                return new IntPtr(1);
            case WM_MOUSEMOVE:
                TrackLeave(hwnd);
                SetHover(true);
                if (_gesture != Gesture.None) Drag();
                handled = true;
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                _tracking = false;
                if (_gesture == Gesture.None) SetHover(false);
                handled = true;
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
                var point = PointFromLParam(lParam);
                var client = _native.ClientSize;
                _edges = MirrorGeometry.HitEdges(point.X, point.Y, client.Width, client.Height, EdgeSize);
                _gesture = _edges == MirrorGeometry.Edges.None ? Gesture.Pending : Gesture.Resize;
                _pointerStart = CursorPosition();
                _frameStart = new Rectangle(_native.Location, client);
                SetCapture(hwnd);
                handled = true;
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                var gesture = _gesture;
                _gesture = Gesture.None;
                if (GetCapture() == hwnd) ReleaseCapture();
                Finish(gesture);
                handled = true;
                return IntPtr.Zero;
            case WM_CAPTURECHANGED when _gesture != Gesture.None:
                var lost = _gesture;
                _gesture = Gesture.None;
                if (lost != Gesture.Pending) Finish(lost);
                return IntPtr.Zero;
            case WM_RBUTTONDOWN:
            case WM_RBUTTONDBLCLK:
                OpenMenu(PointFromLParam(lParam));
                handled = true;
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private static Point PointFromLParam(IntPtr lParam) =>
        new(unchecked((short)(lParam.ToInt64() & 0xffff)), unchecked((short)((lParam.ToInt64() >> 16) & 0xffff)));

    private void TrackLeave(IntPtr hwnd)
    {
        if (_tracking) return;
        var track = new TRACKMOUSEEVENT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2, hwndTrack = hwnd };
        _tracking = TrackMouseEvent(ref track);
    }

    private static int CursorFor(MirrorGeometry.Edges edges) => edges switch
    {
        MirrorGeometry.Edges.Left | MirrorGeometry.Edges.Top or MirrorGeometry.Edges.Right | MirrorGeometry.Edges.Bottom => IDC_SIZENWSE,
        MirrorGeometry.Edges.Right | MirrorGeometry.Edges.Top or MirrorGeometry.Edges.Left | MirrorGeometry.Edges.Bottom => IDC_SIZENESW,
        MirrorGeometry.Edges.Left or MirrorGeometry.Edges.Right => IDC_SIZEWE,
        MirrorGeometry.Edges.Top or MirrorGeometry.Edges.Bottom => IDC_SIZENS,
        _ => IDC_ARROW,
    };

    private void Drag()
    {
        var (x, y) = CursorPosition();
        int dx = x - _pointerStart.X, dy = y - _pointerStart.Y;
        if (_gesture == Gesture.Pending && Math.Abs(dx) + Math.Abs(dy) >= DragThreshold) _gesture = Gesture.Move;
        if (_gesture == Gesture.Move) _native.Location = new(_frameStart.X + dx, _frameStart.Y + dy);
        else if (_gesture == Gesture.Resize)
        {
            var next = MirrorGeometry.Resize(_frameStart, _edges, dx, dy, Definition.SourceWidth / Definition.SourceHeight);
            _native.ClientSize = next.Size;
            _native.Location = next.Location;
            ApplyImage();
        }
    }

    private void Finish(Gesture gesture)
    {
        if (gesture == Gesture.Pending) { ActivateRequested?.Invoke(this); return; }
        if (gesture is not (Gesture.Move or Gesture.Resize)) return;
        var location = _native.Location; var size = _native.ClientSize;
        Definition = Definition with { X = location.X, Y = location.Y, Width = size.Width, Height = size.Height };
        ApplyImage();
        GeometryChanged?.Invoke(this, Definition);
    }

    private void OpenMenu(Point point)
    {
        if (_menu.IsOpen) { _menu.Close(); return; }
        _menu.Placement = PlacementMode.AnchorAndGravity;
        _menu.PlacementRect = new Rect(point.X / RenderScaling, point.Y / RenderScaling, 1, 1);
        _menu.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        _menu.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        _menu.Open(this);
    }

    private void BuildMenu()
    {
        void Add(ItemsControl parent, string header, string command)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => CommandRequested?.Invoke(this, command);
            parent.Items.Add(item);
        }
        Add(_menu, "Reselect area...", "reselect");
        Add(_menu, "Actual size", "actual-size");
        var opacity = new MenuItem { Header = "Opacity" };
        foreach (int percent in new[] { 100, 80, 60, 40 }) Add(opacity, $"{percent}%", "opacity:" + percent);
        _menu.Items.Add(opacity);
        if (_frameRateMenu)
        {
            var rate = new MenuItem { Header = "Frame rate" };
            foreach (int fps in new[] { 1, 2, 5, 10, 15, 30 }) Add(rate, $"{fps} FPS", "fps:" + fps);
            _menu.Items.Add(rate);
        }
        _menu.Items.Add(new Separator());
        Add(_menu, "Remove mirror", "remove");
        NativeMenuTheme.Track(_menu, thumbnail: true);
    }
}

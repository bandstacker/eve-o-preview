using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EveOPreview.Mac.Native;
using EveOPreview.View.CustomControl;
using EveOPreview.View.Rendering;

namespace EveOPreview.Mac.Mirrors;

/// <summary>
/// A floating, nonactivating window showing a live crop of one client window. Drag inside to move,
/// drag within <see cref="EdgeSize"/> of an edge to resize (aspect ratio kept so text is not stretched),
/// click to switch to the client, right-click for options.
/// </summary>
internal sealed class MirrorWindow : Window
{
    private const double EdgeSize = 10, DragThreshold = 4, HoverBorder = 2;
    private readonly WindowsPreviewWindowAdapter _native;
    private readonly ContextMenu _menu = new();
    private long _thumbnail;
    private IntPtr _sourceWindow;
    private bool _hover;
    private Gesture _gesture;
    private (double X, double Y) _pointerStart;
    private System.Drawing.Rectangle _frameStart;
    private Edges _edges;

    [Flags] private enum Edges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
    private enum Gesture { None, Pending, Move, Resize }

    public MirrorWindow(MirrorDefinition definition)
    {
        Definition = definition;
        Title = "EVE-O UI mirror";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false; ShowActivated = false; CanResize = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        _native = new WindowsPreviewWindowAdapter(this);
        Opened += (_, _) => { _native.EnsureStyles(); ApplyGeometry(); StartCapture(); };
        Resized += (_, _) => UpdateImageBounds();
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => { if (_gesture == Gesture.None) SetHover(false); };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        Closed += (_, _) => StopCapture();
        BuildMenu();
    }

    public MirrorDefinition Definition { get; private set; }
    public IntPtr SourceWindow => _sourceWindow;

    public event Action<MirrorWindow> ActivateRequested;
    public event Action<MirrorWindow, MirrorDefinition> GeometryChanged;
    public event Action<MirrorWindow, string> CommandRequested;

    /// <summary>Shows the mirror for this client window, restarting capture when the client changed.</summary>
    public void Attach(IntPtr sourceWindow)
    {
        if (_sourceWindow != sourceWindow) { StopCapture(); _sourceWindow = sourceWindow; }
        if (!IsVisible) Show(); else StartCapture();
        _native.Restore(topmost: true, raise: false);
    }

    public void Update(MirrorDefinition definition)
    {
        var previous = Definition;
        Definition = definition;
        if (previous.X != definition.X || previous.Y != definition.Y || previous.Width != definition.Width || previous.Height != definition.Height)
            ApplyGeometry();
        if (_thumbnail != 0)
        {
            MacNative.ThumbnailSetSourceRect(_thumbnail, definition.SourceX, definition.SourceY, definition.SourceWidth, definition.SourceHeight);
            MacNative.ThumbnailSetFrameRate(_thumbnail, definition.FrameRate);
        }
        _native.SetOpacity(definition.Opacity);
    }

    private void ApplyGeometry()
    {
        _native.Location = new(Definition.X, Definition.Y);
        _native.ClientSize = new(Math.Max(16, Definition.Width), Math.Max(16, Definition.Height));
        _native.SetOpacity(Definition.Opacity);
        UpdateImageBounds();
    }

    private void StartCapture()
    {
        if (_thumbnail != 0 || _sourceWindow == IntPtr.Zero || _native.Handle == IntPtr.Zero) return;
        _thumbnail = MacNative.ThumbnailCreate(_native.Handle, (uint)_sourceWindow);
        if (_thumbnail == 0) return;
        MacNative.ThumbnailSetSourceRect(_thumbnail, Definition.SourceX, Definition.SourceY, Definition.SourceWidth, Definition.SourceHeight);
        MacNative.ThumbnailSetFrameRate(_thumbnail, Definition.FrameRate);
        UpdateImageBounds();
    }

    private void StopCapture()
    {
        if (_thumbnail == 0) return;
        MacNative.ThumbnailDestroy(_thumbnail);
        _thumbnail = 0;
    }

    /// <summary>Restarts a stream that stopped (for example after the client was hidden and shown).</summary>
    public void Maintain()
    {
        if (_thumbnail != 0 && MacNative.ThumbnailUpdate(_thumbnail) == 0) { StopCapture(); StartCapture(); }
    }

    private void UpdateImageBounds()
    {
        if (_thumbnail == 0) return;
        var size = _native.ClientSize;
        double inset = _hover ? HoverBorder : 0;
        MacNative.ThumbnailSetBounds(_thumbnail, inset, inset, Math.Max(1, size.Width - 2 * inset), Math.Max(1, size.Height - 2 * inset));
    }

    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        // The live image is a native layer above Avalonia content; inset it to reveal a frame.
        Background = hover ? new SolidColorBrush(Color.Parse("#6D9FFF")) : Brushes.Black;
        UpdateImageBounds();
    }

    private Edges HitEdges(Point point)
    {
        var edges = Edges.None;
        if (point.X <= EdgeSize) edges |= Edges.Left;
        if (point.X >= Bounds.Width - EdgeSize) edges |= Edges.Right;
        if (point.Y <= EdgeSize) edges |= Edges.Top;
        if (point.Y >= Bounds.Height - EdgeSize) edges |= Edges.Bottom;
        return edges;
    }

    private static Cursor CursorFor(Edges edges) => new(edges switch
    {
        Edges.Left | Edges.Top or Edges.Right | Edges.Bottom => StandardCursorType.TopLeftCorner,
        Edges.Right | Edges.Top or Edges.Left | Edges.Bottom => StandardCursorType.TopRightCorner,
        Edges.Left or Edges.Right => StandardCursorType.SizeWestEast,
        Edges.Top or Edges.Bottom => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.Arrow,
    });

    private void OnPointerPressed(object sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            _menu.Open(this);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        _edges = HitEdges(point.Position);
        _gesture = _edges == Edges.None ? Gesture.Pending : Gesture.Resize;
        _pointerStart = MacNative.PointerLocation();
        _frameStart = new(_native.Location, _native.ClientSize);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerEventArgs e)
    {
        if (_gesture == Gesture.None) { Cursor = CursorFor(HitEdges(e.GetPosition(this))); return; }
        var (x, y) = MacNative.PointerLocation();
        double dx = x - _pointerStart.X, dy = y - _pointerStart.Y;
        if (_gesture == Gesture.Pending && Math.Abs(dx) + Math.Abs(dy) >= DragThreshold) _gesture = Gesture.Move;
        if (_gesture == Gesture.Move)
            _native.Location = new((int)Math.Round(_frameStart.X + dx), (int)Math.Round(_frameStart.Y + dy));
        else if (_gesture == Gesture.Resize) Resize(dx, dy);
    }

    private void Resize(double dx, double dy)
    {
        double aspect = Definition.SourceWidth / Definition.SourceHeight;
        double width = _frameStart.Width + ((_edges & Edges.Right) != 0 ? dx : (_edges & Edges.Left) != 0 ? -dx : 0);
        double height = _frameStart.Height + ((_edges & Edges.Bottom) != 0 ? dy : (_edges & Edges.Top) != 0 ? -dy : 0);
        // Follow the axis the pointer moved most along, keeping the source aspect ratio.
        bool horizontal = (_edges & (Edges.Left | Edges.Right)) != 0
            && ((_edges & (Edges.Top | Edges.Bottom)) == 0 || Math.Abs(dx) >= Math.Abs(dy) * aspect);
        if (horizontal) height = width / aspect; else width = height * aspect;
        width = Math.Clamp(width, 32, 4000); height = Math.Clamp(width / aspect, 16, 4000); width = height * aspect;
        double left = (_edges & Edges.Left) != 0 ? _frameStart.Right - width : _frameStart.X;
        double top = (_edges & Edges.Top) != 0 ? _frameStart.Bottom - height : _frameStart.Y;
        _native.ClientSize = new((int)Math.Round(width), (int)Math.Round(height));
        _native.Location = new((int)Math.Round(left), (int)Math.Round(top));
    }

    private void OnPointerReleased(object sender, PointerReleasedEventArgs e)
    {
        var gesture = _gesture;
        _gesture = Gesture.None;
        e.Pointer.Capture(null);
        if (gesture == Gesture.Pending) { ActivateRequested?.Invoke(this); return; }
        if (gesture is Gesture.Move or Gesture.Resize)
        {
            var location = _native.Location; var size = _native.ClientSize;
            Definition = Definition with { X = location.X, Y = location.Y, Width = size.Width, Height = size.Height };
            GeometryChanged?.Invoke(this, Definition);
        }
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
        var rate = new MenuItem { Header = "Frame rate" };
        foreach (int fps in new[] { 1, 2, 5, 10, 15, 30 }) Add(rate, $"{fps} FPS", "fps:" + fps);
        _menu.Items.Add(rate);
        _menu.Items.Add(new Separator());
        Add(_menu, "Remove mirror", "remove");
        NativeMenuTheme.Track(_menu, thumbnail: true);
    }
}

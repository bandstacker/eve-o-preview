using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EveOPreview.Mirrors;

/// <summary>
/// Covers a client area with a still image of it and lets the user drag the area to mirror. Selecting
/// on a still keeps previews and other topmost windows from hiding the game's UI. The result is in
/// preview pixels relative to the client area's top-left corner; null when cancelled (Esc or right-click).
/// </summary>
internal sealed class MirrorRegionSelector : Window
{
    private readonly TaskCompletionSource<System.Drawing.Rectangle?> _result = new();
    private readonly System.Drawing.Rectangle _bounds;
    private readonly Canvas _canvas = new();
    private readonly Image _still = new() { Stretch = Stretch.Fill };
    private readonly Border _dim = new() { Background = new SolidColorBrush(Color.Parse("#66000000")) };
    private readonly Rectangle _selection = new()
    {
        Stroke = new SolidColorBrush(Color.Parse("#6D9FFF")), StrokeThickness = 2,
        Fill = new SolidColorBrush(Color.Parse("#336D9FFF")), IsVisible = false,
    };
    private Point? _start;

    private MirrorRegionSelector(System.Drawing.Rectangle bounds, byte[] still, System.Drawing.Rectangle? initial)
    {
        _bounds = bounds;
        Title = "Select the area to mirror";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false; Topmost = true; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(bounds.X, bounds.Y);
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);
        if (still is { Length: > 0 }) _still.Source = new Bitmap(new MemoryStream(still));
        _canvas.Children.Add(_still);
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_selection);
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E0101825")), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
            Child = new TextBlock { Text = "Drag over the area to mirror. Release to create the mirror; Esc or right-click cancels.", Foreground = Brushes.White, FontSize = 13 },
        };
        Canvas.SetLeft(hint, 16); Canvas.SetTop(hint, 16);
        _canvas.Children.Add(hint);
        Content = _canvas;
        Opened += (_, _) =>
        {
            // Window size is in DIPs; the client area is physical pixels.
            double scale = RenderScaling;
            Width = bounds.Width / scale; Height = bounds.Height / scale;
            foreach (var layer in new Control[] { _still, _dim }) { layer.Width = Width; layer.Height = Height; }
            if (initial is { } rect) ShowSelection(new Rect(rect.X / scale, rect.Y / scale, rect.Width / scale, rect.Height / scale));
        };
        PointerPressed += (_, e) =>
        {
            var properties = e.GetCurrentPoint(this).Properties;
            if (properties.IsRightButtonPressed) { Complete(null); return; }
            if (!properties.IsLeftButtonPressed) return;
            _start = e.GetPosition(this);
            e.Pointer.Capture(this);
            ShowSelection(new Rect(_start.Value, new Size(0, 0)));
        };
        PointerMoved += (_, e) => { if (_start is { } start) ShowSelection(new Rect(start, e.GetPosition(this)).Normalize()); };
        PointerReleased += (_, e) =>
        {
            if (_start is not { } start) return;
            _start = null;
            e.Pointer.Capture(null);
            double scale = RenderScaling;
            var rect = new Rect(start, e.GetPosition(this)).Normalize().Intersect(new Rect(0, 0, Width, Height));
            var pixels = new System.Drawing.Rectangle((int)Math.Round(rect.X * scale), (int)Math.Round(rect.Y * scale),
                (int)Math.Round(rect.Width * scale), (int)Math.Round(rect.Height * scale));
            if (pixels.Width >= 16 && pixels.Height >= 12) Complete(pixels);
            else _selection.IsVisible = false;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Complete(null); };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    private void ShowSelection(Rect rect)
    {
        Canvas.SetLeft(_selection, rect.X); Canvas.SetTop(_selection, rect.Y);
        _selection.Width = rect.Width; _selection.Height = rect.Height;
        _selection.IsVisible = true;
    }

    private void Complete(System.Drawing.Rectangle? rect)
    {
        _result.TrySetResult(rect);
        Close();
    }

    public static Task<System.Drawing.Rectangle?> SelectAsync(System.Drawing.Rectangle bounds, byte[] still, System.Drawing.Rectangle? initial = null)
    {
        var selector = new MirrorRegionSelector(bounds, still, initial);
        selector.Show();
        selector.Activate();
        return selector._result.Task;
    }
}

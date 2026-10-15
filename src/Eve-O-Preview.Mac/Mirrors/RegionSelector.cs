using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EveOPreview.Mac.Native;

namespace EveOPreview.Mac.Mirrors;

/// <summary>
/// Covers one client window with a still image of it and lets the user drag the area to mirror.
/// Selecting on a still keeps previews and other floating windows from hiding the game's UI.
/// The result is in points relative to the client window's top-left corner; null when cancelled.
/// </summary>
internal sealed class RegionSelector : Window
{
    private readonly TaskCompletionSource<Rect?> _result = new();
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new() { Stroke = new SolidColorBrush(Color.Parse("#6D9FFF")), StrokeThickness = 2, Fill = new SolidColorBrush(Color.Parse("#336D9FFF")), IsVisible = false };
    private readonly Border _dim = new() { Background = new SolidColorBrush(Color.Parse("#66000000")) };
    private Point? _start;

    private RegionSelector((double X, double Y, double Width, double Height) bounds, byte[] still, Rect? initial)
    {
        Title = "Select the area to mirror";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false; Topmost = true; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));
        Width = bounds.Width; Height = bounds.Height;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);
        if (still is { Length: > 0 })
            _canvas.Children.Add(new Image { Source = new Bitmap(new MemoryStream(still)), Width = bounds.Width, Height = bounds.Height, Stretch = Stretch.Fill });
        _dim.Width = bounds.Width; _dim.Height = bounds.Height;
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_selection);
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E0101825")), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
            Child = new TextBlock { Text = "Drag over the area to mirror. Release to create the mirror, Esc to cancel.", Foreground = Brushes.White, FontSize = 13 },
        };
        Canvas.SetLeft(hint, 16); Canvas.SetTop(hint, Math.Min(48, bounds.Height / 4));
        _canvas.Children.Add(hint);
        Content = _canvas;
        if (initial is { } rect) ShowSelection(rect);
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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
            var rect = new Rect(start, e.GetPosition(this)).Normalize().Intersect(new Rect(0, 0, bounds.Width, bounds.Height));
            if (rect.Width >= 16 && rect.Height >= 12) Complete(rect);
            else _selection.IsVisible = false;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Complete(null); };
        Closed += (_, _) => _result.TrySetResult(null);
        Deactivated += (_, _) => { if (!_result.Task.IsCompleted) Activate(); };
    }

    private void ShowSelection(Rect rect)
    {
        Canvas.SetLeft(_selection, rect.X); Canvas.SetTop(_selection, rect.Y);
        _selection.Width = rect.Width; _selection.Height = rect.Height;
        _selection.IsVisible = true;
    }

    private void Complete(Rect? rect)
    {
        _result.TrySetResult(rect);
        Close();
    }

    public static Task<Rect?> SelectAsync((double X, double Y, double Width, double Height) bounds, byte[] still, Rect? initial = null)
    {
        var selector = new RegionSelector(bounds, still, initial);
        selector.Show();
        MacNative.ActivateSelf();
        selector.Activate();
        return selector._result.Task;
    }
}

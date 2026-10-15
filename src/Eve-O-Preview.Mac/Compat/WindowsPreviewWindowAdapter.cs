using System;
using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using EveOPreview.Mac.Native;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace EveOPreview.View.Rendering;

/// <summary>
/// macOS implementation of the upstream Windows preview-window adapter (same name and surface, so
/// ThumbnailView, ThumbnailOverlay, region and snap-guide windows compile unchanged).
/// Geometry is in preview pixels, which are global Quartz points on macOS (see PreviewPixels).
/// Previews never activate this application when clicked; overlays and guides are click-through.
/// </summary>
public sealed class WindowsPreviewWindowAdapter : IDisposable
{
    private readonly Window _window;
    private readonly bool _clickThrough;
    private IntPtr _nsWindow;
    private double _opacity = 1;
    private bool _disposed;

    public WindowsPreviewWindowAdapter(Window window, bool clickThrough = false)
    {
        _window = window;
        _clickThrough = clickThrough;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opened += (_, _) => EnsureStyles();
    }

    /// <summary>The NSWindow pointer, or zero before the native window exists.</summary>
    public IntPtr Handle
    {
        get
        {
            if (_nsWindow != IntPtr.Zero || _disposed) return _nsWindow;
            var platform = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (platform != IntPtr.Zero) _nsWindow = MacNative.WindowFromHandle(platform);
            return _nsWindow;
        }
    }

    public Point Location
    {
        get
        {
            if (MacNative.TryGetOwnWindowFrames(Handle, out var frame, out _, out _))
                return new((int)Math.Round(frame.X), (int)Math.Round(frame.Y));
            return new(_window.Position.X, _window.Position.Y);
        }
        set
        {
            _window.Position = new PixelPoint(value.X, value.Y);
            if (MacNative.TryGetOwnWindowFrames(Handle, out var frame, out var content, out _))
            {
                // Position the outer frame exactly; the content origin follows the decorations.
                double dx = content.X - frame.X, dy = content.Y - frame.Y;
                MacNative.SetWindowContentFrame(Handle, value.X + dx, value.Y + dy, 0, 0, 1, 0);
            }
        }
    }

    public Size ClientSize
    {
        get
        {
            if (MacNative.TryGetOwnWindowFrames(Handle, out _, out var content, out _))
                return new((int)Math.Round(content.Width), (int)Math.Round(content.Height));
            var size = _window.ClientSize;
            return new((int)Math.Round(size.Width), (int)Math.Round(size.Height));
        }
        set
        {
            int width = Math.Max(1, value.Width), height = Math.Max(1, value.Height);
            // One preview pixel is one point (DIP) on macOS.
            _window.Width = width;
            _window.Height = height;
            if (Handle != IntPtr.Zero) MacNative.SetWindowContentFrame(Handle, 0, 0, width, height, 0, 1);
        }
    }

    public Size Size
    {
        get
        {
            if (MacNative.TryGetOwnWindowFrames(Handle, out var frame, out _, out _))
                return new((int)Math.Round(frame.Width), (int)Math.Round(frame.Height));
            return ClientSize;
        }
        set
        {
            var oldClient = ClientSize; var oldOuter = Size;
            ClientSize = new(Math.Max(1, value.Width - oldOuter.Width + oldClient.Width), Math.Max(1, value.Height - oldOuter.Height + oldClient.Height));
        }
    }

    public Rectangle ClientScreenBounds => new(PointToScreen(Point.Empty), ClientSize);

    public Point PointToScreen(Point point)
    {
        if (MacNative.TryGetOwnWindowFrames(Handle, out _, out var content, out _))
            return new((int)Math.Round(content.X) + point.X, (int)Math.Round(content.Y) + point.Y);
        return new(_window.Position.X + point.X, _window.Position.Y + point.Y);
    }

    public Point PointToClient(Point point)
    {
        var origin = PointToScreen(Point.Empty);
        return new(point.X - origin.X, point.Y - origin.Y);
    }

    public void EnsureStyles()
    {
        if (_disposed || Handle == IntPtr.Zero) return;
        MacNative.ConfigureWindow(Handle, _clickThrough ? 1 : 0, nonactivating: 1);
        if (_opacity < 1) MacNative.SetWindowAlpha(Handle, _opacity);
    }

    public bool Restore(bool topmost, bool raise)
    {
        if (Handle == IntPtr.Zero) return false;
        EnsureStyles();
        if (!raise) return true;
        return MacNative.OrderWindowFront(Handle, topmost ? 1 : 0) != 0;
    }

    public void SetOpacity(double opacity)
    {
        _opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        if (Handle != IntPtr.Zero) MacNative.SetWindowAlpha(Handle, _opacity);
    }

    public void Dispose()
    {
        _disposed = true;
        _nsWindow = IntPtr.Zero;
    }
}

using System;
using Avalonia.Controls;
using EveOPreview.Preview;
using EveOPreview.UI.Previews;

namespace EveOPreview.View.Rendering;

/// <summary>
/// macOS overlay graphics: the portable Avalonia vector renderer hosted in the overlay window.
/// Same type name as the Windows glyph-rasterizer renderer it replaces, so ThumbnailOverlay compiles unchanged.
/// </summary>
internal sealed class CompatibilityOverlayRenderer : Panel, IOverlayRenderer
{
    private readonly AvaloniaPreviewOverlay _overlay = new();

    public CompatibilityOverlayRenderer()
    {
        IsHitTestVisible = false;
        Children.Add(_overlay);
    }

    public OverlayCapabilities Capabilities => _overlay.Capabilities;
    public void Resize(PreviewSize size) => _overlay.Resize(size);
    public void SetScene(OverlayScene scene) => _overlay.SetScene(scene);
    public void SetOpacity(double opacity) => _overlay.SetOpacity(opacity);
    public void SetVisible(bool visible) => _overlay.SetVisible(visible);
    public void ShowAlert(PreviewAlert alert) => _overlay.ShowAlert(alert);
    public void ClearAlerts() => _overlay.ClearAlerts();
    public void Dispose() => _overlay.Dispose();
}

/// <summary>
/// DirectComposition does not exist on macOS. Construction fails, and ThumbnailOverlay falls back to
/// <see cref="CompatibilityOverlayRenderer"/>. The macOS host also stores the portable renderer preference.
/// </summary>
public sealed class NativeCompositionOverlayRenderer : IOverlayRenderer
{
    public NativeCompositionOverlayRenderer(IntPtr handle) =>
        throw new PlatformNotSupportedException("Native composition overlays are Windows-only.");

    public OverlayCapabilities Capabilities => default;
    public void CheckDeviceHealth() { }
    public void Resize(PreviewSize size) { }
    public void SetScene(OverlayScene scene) { }
    public void SetOpacity(double opacity) { }
    public void SetVisible(bool visible) { }
    public void ShowAlert(PreviewAlert alert) { }
    public void ClearAlerts() { }
    public void Dispose() { }
}

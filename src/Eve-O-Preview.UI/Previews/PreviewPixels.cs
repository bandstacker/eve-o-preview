namespace EveOPreview.UI.Previews;

/// <summary>
/// Preview geometry is persisted in preview pixels. Windows uses physical pixels (DIPs multiplied by
/// render scaling). The macOS host uses points, matching Avalonia's macOS window and screen coordinates,
/// so one preview pixel is one DIP there. Hosts set this once at startup.
/// </summary>
public static class PreviewPixels
{
    public static bool AreDips { get; set; }

    public static double PerDip(double renderScaling) => AreDips ? 1 : renderScaling;
}

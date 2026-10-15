using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EveOPreview.Mac.Native;
using EveOPreview.Preview;
using EveOPreview.Services;
using EveOPreview.UI;
using EveOPreview.UI.Previews;
using Serilog;

namespace EveOPreview.View;

/// <summary>
/// Settings-page preview renderer for macOS (same type name as the Windows GDI renderer that the shared
/// workspace backend creates). Draws with the portable overlay renderer used by live macOS previews.
/// PNGs are produced at the main display's backing scale so they display at true preview size.
/// </summary>
public sealed class WindowsWorkspacePreviewRenderer : IWorkspacePreviewRenderer
{
    private static readonly Lazy<IReadOnlyList<string>> InstalledFonts = new(() =>
        FontManager.Current.SystemFonts.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());

    public IReadOnlyList<string> FontFamilies => InstalledFonts.Value;

    public WorkspacePreviewImage RenderPreview(WorkspacePreviewRequest request)
    {
        string Value(string key, string fallback) => request.Settings.TryGetValue(key, out var value) ? value : fallback;
        double Number(string key, string fallback, double minimum, double maximum)
        {
            if (!double.TryParse(Value(key, fallback), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value < minimum || value > maximum)
                throw new ArgumentException($"Enter a valid {key} before previewing.");
            return value;
        }
        bool Enabled(string key, bool fallback) => bool.TryParse(Value(key, fallback.ToString()), out var value) ? value : fallback;
        uint ColorValue(string key, string fallback)
        {
            var value = Value(key, fallback);
            if (value.Length != 7 || value[0] != '#' || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                throw new ArgumentException("Use a six-digit hex color before previewing.");
            return 0xFF000000 | rgb;
        }

        int width = (int)Number("ThumbnailWidth", "384", 1, 3840);
        int height = (int)Number("ThumbnailHeight", "216", 1, 2160);
        string fontName = Value("TitleFontName", "Helvetica");
        if (!FontFamilies.Contains(fontName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an installed font to preview it.");
        if (!Enum.TryParse<OverlayFontStyle>(Value("TitleFontStyle", "Regular"), out var style) || ((int)style & ~15) != 0)
            throw new ArgumentException("Choose a valid title style before previewing.");

        var root = new Canvas { Width = width, Height = height, Background = new SolidColorBrush(Color.Parse(request.BackgroundColor)) };
        var imageBounds = new Rect(0, 0, width, height);
        if (request.Active && Enabled("EnableActiveClientHighlight", false))
        {
            int thickness = (int)Number("ActiveClientHighlightThickness", "3", 1, 6);
            double imageHeight = Math.Max(0, height - 2 * thickness);
            double imageWidth = Math.Round(imageHeight * ((double)width / height), MidpointRounding.AwayFromZero);
            double left = (width - imageWidth) / 2;
            root.Background = new SolidColorBrush(Color.FromUInt32(ColorValue("ActiveClientHighlightColor", "#ADFF2F")));
            var inset = new Border { Width = imageWidth, Height = imageHeight, Background = new SolidColorBrush(Color.Parse(request.BackgroundColor)) };
            Canvas.SetLeft(inset, left); Canvas.SetTop(inset, thickness);
            root.Children.Add(inset);
            imageBounds = new Rect(left, thickness, imageWidth, imageHeight);
        }
        if (request.BackgroundPng is { Length: > 0 } png)
        {
            var image = new Image { Source = new Bitmap(new MemoryStream(png, false)), Width = imageBounds.Width, Height = imageBounds.Height, Stretch = Stretch.Fill };
            Canvas.SetLeft(image, imageBounds.X); Canvas.SetTop(image, imageBounds.Y);
            root.Children.Add(image);
        }
        var overlay = new AvaloniaPreviewOverlay();
        if (Enabled("ShowThumbnailOverlays", true) || request.CycleSkipped || Enabled("ShowCurrentSolarSystem", false))
        {
            overlay.Resize(new PreviewSize(width, height));
            overlay.SetScene(new OverlayScene
            {
                Title = request.Title.Replace("EVE - ", ""),
                ShowTitle = Enabled("ShowThumbnailOverlays", true),
                Subtitle = Enabled("ShowCurrentSolarSystem", false) ? Value("SolarSystemSample", "Jita") : "",
                SubtitleColor = ColorValue("SolarSystemColor", "#D4E8FF"),
                SubtitlePlacement = Enum.TryParse<SubtitlePlacement>(Value("SolarSystemPlacement", "Below"), out var placement) ? placement : SubtitlePlacement.Below,
                TitlePosition = Enum.TryParse<OverlayPosition>(Value("TitlePosition", "TopLeft"), out var position) ? position : OverlayPosition.TopLeft,
                SubtitleFontSize = Number("SolarSystemFontSize", "0", 0, 200) is > 0 and var systemSize ? (float)systemSize : null,
                Font = new OverlayFont(fontName, (float)Number("TitleFontSize", "14.25", 1, 200), style,
                    ColorValue("TitleFontForeColor", "#FFA500"), ColorValue("TitleFontOutlineColor", "#000000"),
                    (float)Number("TitleFontOutlineWidth", "3", 0, 20),
                    (int)Number("TitleFontOffsetLeft", "10", -10000, 10000),
                    (int)Number("TitleFontOffsetTop", "5", -10000, 10000)),
                CycleSkipped = request.CycleSkipped,
                MarkerStyle = Value("CycleSkipIndicatorStyle", "Circle with slash") switch
                { "Pause" => CycleMarkerStyle.Pause, "Cross" => CycleMarkerStyle.Cross, _ => CycleMarkerStyle.CircleSlash },
                MarkerColor = ColorValue("CycleSkipIndicatorColor", "#FF0000"),
            });
            root.Children.Add(overlay);
        }

        double scale = MacNative.ListScreens().FirstOrDefault(s => s.Primary)?.Scale ?? 2;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(root);
        overlay.Dispose();
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return new(stream.ToArray(), pixels.Width, pixels.Height);
    }
}

/// <summary>Settings sample from a running client: one ScreenCaptureKit still, never activating the client.</summary>
public sealed class MacWorkspacePreviewCapture(IProcessMonitor processes, IWindowManager windows, ILogger logger) : IWorkspacePreviewCapture
{
    public Task<WorkspaceClientStill> CapturePreviewStillAsync(string preferredTitle) => Task.Run(() =>
    {
        var candidates = processes.GetAllProcesses()
            .Where(client => client.MainWindowHandle != IntPtr.Zero && client.Title.StartsWith("EVE - ", StringComparison.Ordinal))
            .OrderByDescending(client => client.Title == preferredTitle).ThenBy(client => client.Title, StringComparer.OrdinalIgnoreCase);
        foreach (var client in candidates)
        {
            try
            {
                if (windows.IsWindowMinimized(client.MainWindowHandle)) continue;
                var png = MacNative.CaptureWindow((uint)client.MainWindowHandle, 960, 540);
                if (png is { Length: > 0 }) return new WorkspaceClientStill(client.Title, png);
            }
            catch (Exception ex) { logger.Debug(ex, "Could not capture settings sample for {Title}", client.Title); }
        }
        return null;
    });
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace EveOPreview.Mirrors;

/// <summary>
/// One mirrored area of a client window. Source coordinates are preview pixels relative to the client
/// area's top-left corner; the mirror window rectangle is desktop preview pixels.
/// </summary>
public sealed record MirrorDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    /// <summary>Full client title, for example "EVE - Pilot One" (the shared persisted identity).</summary>
    public string SourceTitle { get; init; } = "";
    public double SourceX { get; init; }
    public double SourceY { get; init; }
    public double SourceWidth { get; init; }
    public double SourceHeight { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Opacity { get; init; } = 1;
    public int FrameRate { get; init; } = 5;

    [JsonIgnore] public string Character => SourceTitle.StartsWith("EVE - ", StringComparison.Ordinal) ? SourceTitle[6..] : SourceTitle;
    [JsonIgnore] public double Zoom => SourceWidth > 0 ? Width / SourceWidth : 1;

    /// <summary>Mirror size for a zoom factor, keeping the source aspect ratio.</summary>
    public MirrorDefinition WithZoom(double zoom) => this with
    {
        Width = Math.Max(16, (int)Math.Round(SourceWidth * zoom)), Height = Math.Max(16, (int)Math.Round(SourceHeight * zoom)),
    };
}

/// <summary>
/// Mirrors are stored per profile in their own file beside the profile JSON. Older builds and other
/// platforms that rewrite the profile cannot drop them, and the format is shared with the macOS host.
/// </summary>
public sealed class MirrorStore(ILogger logger)
{
    public const string FileName = "EVE-O Preview.mirrors.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public IReadOnlyList<MirrorDefinition> Load(string profileFolder)
    {
        if (string.IsNullOrEmpty(profileFolder)) return [];
        string path = Path.Combine(profileFolder, FileName);
        try
        {
            if (!File.Exists(path)) return [];
            var mirrors = JsonSerializer.Deserialize<List<MirrorDefinition>>(File.ReadAllText(path)) ?? [];
            return mirrors.Where(m => m is not null && !string.IsNullOrEmpty(m.SourceTitle) && m.SourceWidth >= 4 && m.SourceHeight >= 4).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "UI mirrors could not be read from {Path}", path);
            return [];
        }
    }

    public void Save(string profileFolder, IReadOnlyList<MirrorDefinition> mirrors)
    {
        if (string.IsNullOrEmpty(profileFolder)) return;
        string path = Path.Combine(profileFolder, FileName), temporary = path + ".tmp";
        Directory.CreateDirectory(profileFolder);
        File.WriteAllText(temporary, JsonSerializer.Serialize(mirrors, Json));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Aspect-preserving resize from a window edge or corner (pure geometry, preview pixels).</summary>
public static class MirrorGeometry
{
    [Flags] public enum Edges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    public static Edges HitEdges(int x, int y, int width, int height, int edge)
    {
        var edges = Edges.None;
        if (x <= edge) edges |= Edges.Left;
        if (x >= width - edge) edges |= Edges.Right;
        if (y <= edge) edges |= Edges.Top;
        if (y >= height - edge) edges |= Edges.Bottom;
        return edges;
    }

    /// <summary>New window rectangle after dragging <paramref name="edges"/> by (dx, dy). The opposite edges stay fixed.</summary>
    public static System.Drawing.Rectangle Resize(System.Drawing.Rectangle start, Edges edges, int dx, int dy, double aspect, int minimumWidth = 32)
    {
        double width = start.Width + ((edges & Edges.Right) != 0 ? dx : (edges & Edges.Left) != 0 ? -dx : 0);
        double height = start.Height + ((edges & Edges.Bottom) != 0 ? dy : (edges & Edges.Top) != 0 ? -dy : 0);
        bool horizontal = (edges & (Edges.Left | Edges.Right)) != 0
            && ((edges & (Edges.Top | Edges.Bottom)) == 0 || Math.Abs(dx) >= Math.Abs(dy) * aspect);
        width = horizontal ? width : height * aspect;
        width = Math.Clamp(width, minimumWidth, 8000);
        height = width / aspect;
        int w = (int)Math.Round(width), h = Math.Max(1, (int)Math.Round(height));
        int left = (edges & Edges.Left) != 0 ? start.Right - w : start.X;
        int top = (edges & Edges.Top) != 0 ? start.Bottom - h : start.Y;
        return new(left, top, w, h);
    }
}

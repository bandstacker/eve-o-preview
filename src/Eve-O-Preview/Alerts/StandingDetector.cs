using System;
using System.Collections.Generic;
using System.Linq;

namespace EveOPreview.Alerts;

public enum StandingColor { Red, Orange, Yellow, Grey, Green, Blue, Purple }

/// <summary>A standing icon in frame pixels.</summary>
public readonly record struct StandingIcon(int X, int Y, int Width, int Height, StandingColor Color)
{
    /// <summary>Corp (green), alliance and positive standings (blue) and fleet (purple/pink) never alarm.</summary>
    public bool Friendly => Color is StandingColor.Green or StandingColor.Blue or StandingColor.Purple;
}

public sealed record StandingScan(IReadOnlyList<StandingIcon> Icons)
{
    public static readonly StandingScan Empty = new(Array.Empty<StandingIcon>());
    public int Hostile => Icons.Count(i => !i.Friendly);
    public int Friendly => Icons.Count(i => i.Friendly);

    /// <summary>For example "1 red, 2 neutral".</summary>
    public string DescribeHostile()
    {
        var parts = Icons.Where(i => !i.Friendly).GroupBy(i => i.Color).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Name(g.Key)}");
        return string.Join(", ", parts);
    }

    public static string Name(StandingColor color) => color switch
    {
        StandingColor.Red => "red",
        StandingColor.Orange => "orange",
        StandingColor.Yellow => "yellow",
        StandingColor.Grey => "neutral",
        StandingColor.Green => "corp",
        StandingColor.Blue => "blue",
        _ => "fleet",
    };
}

/// <summary>
/// Finds EVE standing icons in a Local member list by colour and shape, independent of UI scale:
/// a filled, roughly square patch of one standing colour, optionally with a white glyph inside.
/// Calibrated on client icons (neutral fill #6E6E6E with a #8C8C8C rim; terrible fill #750A0A
/// with a #950606 rim; white glyphs). Text, row highlights and scroll bars fail the shape tests.
/// </summary>
public static class StandingDetector
{
    private const byte Background = 0, Glyph = 1, ClassBase = 2;
    private const int MinimumSize = 5, MaximumSize = 72, MinimumPixels = 6, ShadeWindow = 20;

    public static StandingScan Detect(AlertFrame frame)
    {
        int width = frame.Width, height = frame.Height;
        var classes = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * frame.Stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                classes[y * width + x] = Classify(frame.Pixels[i + 2], frame.Pixels[i + 1], frame.Pixels[i]);
            }
        }
        var components = FindComponents(classes, width, height);
        var merged = Merge(components);
        var icons = new List<StandingIcon>();
        foreach (var c in merged)
        {
            int w = c.MaxX - c.MinX + 1, h = c.MaxY - c.MinY + 1;
            if (w < MinimumSize || h < MinimumSize || w > MaximumSize || h > MaximumSize) continue;
            if (Math.Max(w, h) > Math.Min(w, h) * 1.35) continue;
            double area = w * h;
            if (c.Count / area < 0.40) continue;
            int glyph = 0, inside = 0;
            Span<int> shades = stackalloc int[256];
            shades.Clear();
            for (int y = c.MinY; y <= c.MaxY; y++)
                for (int x = c.MinX; x <= c.MaxX; x++)
                {
                    byte cls = classes[y * width + x];
                    if (cls == Glyph) glyph++;
                    else if (cls == c.Class)
                    {
                        int i = y * frame.Stride + x * 4;
                        shades[Math.Max(frame.Pixels[i], Math.Max(frame.Pixels[i + 1], frame.Pixels[i + 2]))]++;
                        inside++;
                    }
                }
            if ((c.Count + glyph) / area < 0.75) continue;
            // Icons are filled with one flat shade (neutral #6E6E6E); anti-aliased text spreads over many.
            // A sliding window of brightness levels, so a fill near any fixed boundary still counts as one shade.
            int dominant = 0, window = 0;
            for (int v = 0; v < 256; v++)
            {
                window += shades[v] - (v >= ShadeWindow ? shades[v - ShadeWindow] : 0);
                dominant = Math.Max(dominant, window);
            }
            if (dominant < inside * 0.45) continue;
            // Icons have a solid rim of their colour; a text glyph (anti-aliased grey around a white
            // core, for example "a") touches its bounding box only in a few places.
            int rim = 0;
            for (int x = c.MinX; x <= c.MaxX; x++) rim += (classes[c.MinY * width + x] == c.Class ? 1 : 0) + (classes[c.MaxY * width + x] == c.Class ? 1 : 0);
            for (int y = c.MinY + 1; y < c.MaxY; y++) rim += (classes[y * width + c.MinX] == c.Class ? 1 : 0) + (classes[y * width + c.MaxX] == c.Class ? 1 : 0);
            if (rim < (2 * w + 2 * h - 4) * 0.6) continue;
            var color = (StandingColor)(c.Class - ClassBase);
            // Plain grey panels and buttons are common in EVE's UI; the neutral icon has its "=" glyph.
            if (color == StandingColor.Grey && glyph < area * 0.04) continue;
            icons.Add(new(c.MinX, c.MinY, w, h, color));
        }
        // A rim and fill of different shades can produce nested candidates; keep the outer one.
        var result = icons.OrderByDescending(i => i.Width * i.Height)
            .Aggregate(new List<StandingIcon>(), (kept, icon) =>
            {
                if (!kept.Any(k => Overlaps(k, icon))) kept.Add(icon);
                return kept;
            })
            .OrderBy(i => i.Y).ThenBy(i => i.X).ToArray();
        return new(result);
    }

    internal static byte Classify(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double value = max / 255.0, saturation = max == 0 ? 0 : (max - min) / (double)max;
        if (value >= 0.75 && saturation <= 0.45) return Glyph;
        if (saturation <= 0.12) return value >= 0.30 ? (byte)(ClassBase + (int)StandingColor.Grey) : Background;
        if (saturation < 0.35 || value < 0.30) return Background;
        double hue;
        double delta = max - min;
        if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * ((b - r) / delta + 2);
        else hue = 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        var color = hue switch
        {
            < 15 or >= 345 => StandingColor.Red,
            < 40 => StandingColor.Orange,
            < 70 => StandingColor.Yellow,
            < 165 => StandingColor.Green,
            < 255 => StandingColor.Blue,
            _ => StandingColor.Purple,
        };
        return (byte)(ClassBase + (int)color);
    }

    private sealed class Component
    {
        public byte Class;
        public int MinX, MinY, MaxX, MaxY, Count;
    }

    private static List<Component> FindComponents(byte[] classes, int width, int height)
    {
        var visited = new bool[classes.Length];
        var stack = new Stack<int>();
        var found = new List<Component>();
        for (int start = 0; start < classes.Length; start++)
        {
            byte cls = classes[start];
            if (cls < ClassBase || visited[start]) continue;
            var c = new Component { Class = cls, MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1 };
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                int x = index % width, y = index / width;
                c.Count++;
                if (x < c.MinX) c.MinX = x;
                if (x > c.MaxX) c.MaxX = x;
                if (y < c.MinY) c.MinY = y;
                if (y > c.MaxY) c.MaxY = y;
                // Large regions (panels, nebulae) are filled completely and then dropped whole;
                // stopping early would leave icon-sized fragments.
                if (x > 0) Visit(index - 1);
                if (x < width - 1) Visit(index + 1);
                if (y > 0) Visit(index - width);
                if (y < height - 1) Visit(index + width);
            }
            if (c.Count >= MinimumPixels && c.MaxX - c.MinX < MaximumSize && c.MaxY - c.MinY < MaximumSize) found.Add(c);

            void Visit(int next)
            {
                if (visited[next] || classes[next] != cls) return;
                visited[next] = true;
                stack.Push(next);
            }
        }
        return found;
    }

    /// <summary>Joins same-colour pieces a full-width glyph split apart (small icons at low UI scale).</summary>
    private static List<Component> Merge(List<Component> components)
    {
        if (components.Count > 4000) return components;
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < components.Count && !merged; i++)
                for (int j = i + 1; j < components.Count; j++)
                {
                    var a = components[i];
                    var b = components[j];
                    if (a.Class != b.Class || b.MinX > a.MaxX + 1 || a.MinX > b.MaxX + 1 || b.MinY > a.MaxY + 1 || a.MinY > b.MaxY + 1) continue;
                    // Only join touching pieces that together still fit one icon. Glyph strips are wide,
                    // so the pairwise limit is loose; the final icon test requires a square.
                    int w = Math.Max(a.MaxX, b.MaxX) - Math.Min(a.MinX, b.MinX) + 1, h = Math.Max(a.MaxY, b.MaxY) - Math.Min(a.MinY, b.MinY) + 1;
                    if (w > MaximumSize || h > MaximumSize || Math.Max(w, h) > Math.Min(w, h) * 2.2) continue;
                    a.MinX = Math.Min(a.MinX, b.MinX); a.MinY = Math.Min(a.MinY, b.MinY);
                    a.MaxX = Math.Max(a.MaxX, b.MaxX); a.MaxY = Math.Max(a.MaxY, b.MaxY);
                    a.Count += b.Count;
                    components.RemoveAt(j);
                    merged = true;
                    break;
                }
        }
        return components;
    }

    private static bool Overlaps(StandingIcon a, StandingIcon b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>Outlines detected icons in their colour class (diagnostic view).</summary>
    public static AlertFrame Annotate(AlertFrame frame, StandingScan scan)
    {
        var copy = frame.Clone();
        int thickness = Math.Max(1, (int)Math.Round(frame.Scale));
        foreach (var icon in scan.Icons)
            copy.DrawRectangle(icon.X, icon.Y, icon.Width, icon.Height, icon.Friendly ? 0xFF35D06Bu : 0xFFFF3B3Bu, thickness);
        return copy;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace EveOPreview.Mac;

/// <summary>
/// Imports gameplay profiles copied from a Windows installation. The JSON format is unchanged; only
/// screen geometry is rescaled from Windows pixels to this Mac's points so layouts land on screen.
/// Hotkeys need no conversion: the macOS host reads the persisted Ctrl modifier as Command.
/// </summary>
public static partial class WindowsProfileImporter
{
    public const string ProfileFileName = "EVE-O Preview.json";

    public sealed record ImportResult(IReadOnlyList<string> Imported, IReadOnlyList<string> Skipped);

    /// <summary>Profile folders below <paramref name="source"/>: a Profiles folder, one profile folder, or an EVE-O folder.</summary>
    public static IReadOnlyList<string> FindProfiles(string source)
    {
        if (File.Exists(Path.Combine(source, ProfileFileName))) return [source];
        string root = Directory.Exists(Path.Combine(source, "Profiles")) ? Path.Combine(source, "Profiles") : source;
        return Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, ProfileFileName))).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    public static ImportResult Import(string source, string profileRoot, double scale)
    {
        var imported = new List<string>();
        var skipped = new List<string>();
        foreach (string folder in FindProfiles(source))
        {
            string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            try
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(folder, ProfileFileName)));
                Rescale(json, scale);
                string target = UniqueFolder(profileRoot, name);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, ProfileFileName), json.ToString());
                imported.Add(Path.GetFileName(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
            {
                skipped.Add($"{name}: {ex.Message}");
            }
        }
        return new(imported, skipped);
    }

    private static string UniqueFolder(string root, string name)
    {
        string candidate = Path.Combine(root, name);
        if (!Directory.Exists(candidate)) return candidate;
        candidate = Path.Combine(root, name + " (Windows)");
        for (int i = 2; Directory.Exists(candidate); i++) candidate = Path.Combine(root, $"{name} (Windows {i})");
        return candidate;
    }

    /// <summary>Scales every persisted point, size and rectangle. Names match ThumbnailConfiguration.</summary>
    public static void Rescale(JObject json, double scale)
    {
        if (Math.Abs(scale - 1) < 0.0001) return;
        foreach (string key in new[] { "ThumbnailSize", "ThumbnailMaximumSize", "ThumbnailMinimumSize", "LoginThumbnailLocation" })
            if (json[key] is JToken token) json[key] = ScalePair(token, scale);
        foreach (string key in new[] { "PerClientThumbnailSizes", "FlatLayout" })
            if (json[key] is JObject map)
                foreach (var property in map.Properties().ToList()) property.Value = ScalePair(property.Value, scale);
        if (json["PerClientLayout"] is JObject perClient)
            foreach (var layout in perClient.Properties().Select(p => p.Value).OfType<JObject>())
                foreach (var property in layout.Properties().ToList()) property.Value = ScalePair(property.Value, scale);
        if (json["ClientLayout"] is JObject clients)
            foreach (var layout in clients.Properties().Select(p => p.Value).OfType<JObject>()) ScaleRectangle(layout, scale);
        if (json["ThumbnailRegions"] is JArray regions)
            foreach (var region in regions.OfType<JObject>()) ScaleRectangle(region, scale);
    }

    [GeneratedRegex(@"^\s*(-?\d+)\s*,\s*(-?\d+)\s*$")]
    private static partial Regex PairText();

    private static JToken ScalePair(JToken token, double scale)
    {
        int S(long value) => (int)Math.Round(value * scale);
        if (token.Type == JTokenType.String && PairText().Match(token.Value<string>()) is { Success: true } match)
            return $"{S(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))}, {S(long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))}";
        if (token is JObject pair)
        {
            foreach (string axis in new[] { "X", "Y", "Width", "Height", "x", "y", "width", "height" })
                if (pair[axis]?.Type == JTokenType.Integer) pair[axis] = S(pair[axis].Value<long>());
        }
        return token;
    }

    private static void ScaleRectangle(JObject rectangle, double scale)
    {
        foreach (string axis in new[] { "X", "Y", "Width", "Height" })
            if (rectangle[axis]?.Type == JTokenType.Integer) rectangle[axis] = (int)Math.Round(rectangle[axis].Value<long>() * scale);
    }
}

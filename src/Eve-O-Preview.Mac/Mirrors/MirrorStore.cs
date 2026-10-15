using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace EveOPreview.Mac.Mirrors;

/// <summary>
/// One mirrored area of a client window. Source coordinates are points relative to the client
/// window's top-left corner (title bar included); the mirror window frame is global points.
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
}

/// <summary>
/// Mirrors are stored per profile in their own file beside the profile JSON, so a Windows
/// installation saving the same profile cannot drop them.
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
            return mirrors.Where(m => !string.IsNullOrEmpty(m.SourceTitle) && m.SourceWidth >= 4 && m.SourceHeight >= 4).ToList();
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

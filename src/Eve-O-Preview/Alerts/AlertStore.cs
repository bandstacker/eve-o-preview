using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace EveOPreview.Alerts;

/// <summary>
/// Local alerts are stored per profile in their own file beside the profile JSON, like UI mirrors,
/// so saving the profile from another installation cannot drop them.
/// </summary>
public sealed class AlertStore(ILogger logger)
{
    public const string FileName = "EVE-O Preview.alerts.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AlertDocument Load(string profileFolder)
    {
        if (string.IsNullOrEmpty(profileFolder)) return new();
        string path = Path.Combine(profileFolder, FileName);
        try
        {
            if (!File.Exists(path)) return new();
            var document = JsonSerializer.Deserialize<AlertDocument>(File.ReadAllText(path)) ?? new();
            return document with
            {
                Options = (document.Options ?? new()).Normalize(),
                Watches = (document.Watches ?? new()).Where(w => !string.IsNullOrEmpty(w.SourceTitle) && w.List.IsUsable).ToList(),
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Local alerts could not be read from {Path}", path);
            return new();
        }
    }

    public void Save(string profileFolder, AlertDocument document)
    {
        if (string.IsNullOrEmpty(profileFolder)) return;
        string path = Path.Combine(profileFolder, FileName), temporary = path + ".tmp";
        Directory.CreateDirectory(profileFolder);
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
        File.Move(temporary, path, overwrite: true);
    }
}

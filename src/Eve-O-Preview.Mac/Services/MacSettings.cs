using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace EveOPreview.Services.Implementation;

/// <summary>
/// macOS-only application settings, stored beside the shared global settings file. Gameplay profiles
/// keep the upstream format so they remain interchangeable with Windows.
/// </summary>
public sealed class MacSettings
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "EVE-O Preview");
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "EVE-O Preview");

    private sealed record Stored(int PreviewFrameRate = 15, bool MuteBackgroundClients = false, int TitleBarCropPoints = 0);

    private readonly string _path;
    private readonly ILogger _logger;
    private Stored _values = new();

    public MacSettings(ILogger logger) : this(Path.Combine(RootDirectory, "EVE-O Preview.macos.json"), logger) { }

    public MacSettings(string path, ILogger logger)
    {
        _path = path; _logger = logger;
        try
        {
            if (File.Exists(path)) _values = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "macOS settings could not be read; using defaults");
        }
    }

    public event Action Changed;

    public int PreviewFrameRate => Math.Clamp(_values.PreviewFrameRate, 1, 60);
    public bool MuteBackgroundClients => _values.MuteBackgroundClients;
    public int TitleBarCropPoints => Math.Clamp(_values.TitleBarCropPoints, 0, 64);

    public void SetPreviewFrameRate(int value) => Update(_values with { PreviewFrameRate = Math.Clamp(value, 1, 60) });
    public void SetMuteBackgroundClients(bool value) => Update(_values with { MuteBackgroundClients = value });
    public void SetTitleBarCropPoints(int value) => Update(_values with { TitleBarCropPoints = Math.Clamp(value, 0, 64) });

    private void Update(Stored next)
    {
        if (next == _values) return;
        _values = next;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "macOS settings could not be saved");
            throw;
        }
        Changed?.Invoke();
    }
}

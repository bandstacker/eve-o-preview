namespace EveOPreview.UI;

public enum AlertWatchStatus
{
    /// <summary>Switched off by the user.</summary>
    Off,
    /// <summary>The client is not running.</summary>
    Waiting,
    /// <summary>Capture is starting; no stable reading yet.</summary>
    Starting,
    Clear,
    Alarm,
    /// <summary>The client is hidden, minimized or cannot be captured.</summary>
    Paused,
}

/// <summary>One Local watch as the alerts page shows it.</summary>
public sealed record AlertWatchItem(string Id, string Name, string SourceTitle, bool Enabled, bool HasCount,
    AlertWatchStatus Status, int Hostile, int Friendly, int? Count, int Unidentified, string Detail)
{
    public string Character => SourceTitle.StartsWith("EVE - ", StringComparison.Ordinal) ? SourceTitle[6..] : SourceTitle;
}

/// <summary>Sounds are "default", "none" or a .wav file path.</summary>
public sealed record AlertOptionsItem(bool Paused, double ScansPerSecond, int ReminderSeconds, bool FlashPreviews,
    string Sound, string CountSound, int Volume, string WebhookUrl, bool WebhookEnabled, string WebhookStatus);

public sealed record AlertSnapshot(AlertOptionsItem Options, IReadOnlyList<AlertWatchItem> Watches, IReadOnlyList<string> Clients,
    bool Selecting, bool CanReadCount);

/// <summary>
/// Optional platform capability for Local alerts. Hosts own capture, detection, selection and storage;
/// the page only displays snapshots and sends commands.
/// </summary>
public interface IWorkspaceAlerts
{
    AlertSnapshot ReadAlerts();
    event Action? AlertsChanged;
    /// <summary>Brings the client forward and lets the user draw the member list and the optional count.</summary>
    Task<CommandResult> CreateAlertWatchAsync(string sourceTitle);
    /// <summary>Actions: rename (Value=name), enabled (true/false), reselect, copy-to-all, remove.</summary>
    Task<CommandResult> EditAlertWatchAsync(string id, string action, string value = "");
    /// <summary>Keys: paused, scans, reminder, flash, sound, count-sound, volume, webhook-url, webhook-enabled.</summary>
    Task<CommandResult> EditAlertOptionsAsync(string key, string value);
    /// <summary>Tests: sound, count-sound, webhook.</summary>
    Task<CommandResult> TestAlertAsync(string test);
    /// <summary>PNG of the watch's latest member-list capture with detected icons outlined, or null.</summary>
    byte[]? ReadAlertView(string id);
}

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveOPreview.Alerts;

/// <summary>
/// An area of a client window in the host's window units: points relative to the window's top-left
/// corner on macOS, client-area pixels on Windows. Captured frames carry the matching pixel scale.
/// </summary>
public readonly record struct AlertArea(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public bool IsUsable => Width >= 4 && Height >= 4 && double.IsFinite(X) && double.IsFinite(Y);
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;

    public AlertArea Union(AlertArea other)
    {
        double left = Math.Min(X, other.X), top = Math.Min(Y, other.Y);
        return new(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }

    public AlertArea Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Grown by the margins on each side, never past the window's top-left corner.</summary>
    public AlertArea Inflate(double dx, double dy)
    {
        double left = Math.Max(0, X - dx), top = Math.Max(0, Y - dy);
        return new(left, top, Right + dx - left, Bottom + dy - top);
    }
}

/// <summary>One watched client: its Local member list and, optionally, the member count.</summary>
public sealed record AlertWatch
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    /// <summary>Full client title, for example "EVE - Pilot One" (the shared persisted identity).</summary>
    public string SourceTitle { get; init; } = "";
    public AlertArea List { get; init; }
    public AlertArea? Count { get; init; }
    public bool Enabled { get; init; } = true;

    [JsonIgnore] public string Character => SourceTitle.StartsWith("EVE - ", StringComparison.Ordinal) ? SourceTitle[6..] : SourceTitle;
    /// <summary>
    /// The count box with a margin: text recognition misreads glyphs cut by a tight box (a clipped "]" reads as "1").
    /// </summary>
    [JsonIgnore] public AlertArea? CountRead => Count is { IsUsable: true } count ? count.Inflate(count.Height * 0.5, count.Height * 0.35) : null;
    /// <summary>The captured area: both boxes, so one capture per watch serves detection and the count.</summary>
    [JsonIgnore] public AlertArea Capture => CountRead is { } count ? List.Union(count) : List;
}

/// <summary>Profile-wide alert options. Sounds are "default", "none" or a .wav file path.</summary>
public sealed record AlertOptions
{
    public const string DefaultSound = "default", NoSound = "none";

    public bool Paused { get; init; }
    public double ScansPerSecond { get; init; } = 2;
    /// <summary>Seconds between reminder sounds while someone is still in Local; 0 turns reminders off.</summary>
    public int ReminderSeconds { get; init; } = 30;
    public bool FlashPreviews { get; init; } = true;
    public string Sound { get; init; } = DefaultSound;
    public string CountSound { get; init; } = DefaultSound;
    public int Volume { get; init; } = 80;
    public string WebhookUrl { get; init; } = "";
    public bool WebhookEnabled { get; init; }

    public AlertOptions Normalize() => this with
    {
        ScansPerSecond = double.IsFinite(ScansPerSecond) ? Math.Clamp(ScansPerSecond, 0.5, 5) : 2,
        ReminderSeconds = Math.Clamp(ReminderSeconds, 0, 600),
        Volume = Math.Clamp(Volume, 0, 100),
        Sound = string.IsNullOrWhiteSpace(Sound) ? DefaultSound : Sound,
        CountSound = string.IsNullOrWhiteSpace(CountSound) ? DefaultSound : CountSound,
        WebhookUrl = WebhookUrl?.Trim() ?? "",
    };
}

public sealed record AlertDocument
{
    public AlertOptions Options { get; init; } = new();
    public List<AlertWatch> Watches { get; init; } = new();
}

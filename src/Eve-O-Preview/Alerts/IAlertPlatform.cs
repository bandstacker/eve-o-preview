using System;
using System.Threading.Tasks;

namespace EveOPreview.Alerts;

public enum AlertSampleState
{
    /// <summary>A current frame is available.</summary>
    Live,
    /// <summary>Capture runs but the client draws nothing new (hidden, minimized or off screen).</summary>
    Stale,
    /// <summary>Capture is not running (starting, failed or the window is gone).</summary>
    Unavailable,
}

public readonly record struct AlertSample(AlertSampleState State, AlertFrame Frame);

/// <summary>Captures one area of one client window. Read is called from the scan thread.</summary>
public interface IAlertSampler : IDisposable
{
    AlertSample Read();
}

/// <summary>
/// Platform services for Local alerts. Capture and text recognition run on the scan thread;
/// selection runs on the UI thread.
/// </summary>
public interface IAlertPlatform
{
    /// <summary>Starts capturing <paramref name="area"/> of a client window.</summary>
    IAlertSampler CreateSampler(IntPtr clientWindow, AlertArea area);

    /// <summary>False when this system has no text recognition; count boxes are then not offered.</summary>
    bool CanRecognizeText { get; }

    /// <summary>Recognised text in the frame, or null when nothing could be read.</summary>
    Task<string> RecognizeTextAsync(AlertFrame frame);

    /// <summary>Plays WAV data at a volume from 0 to 1 without blocking.</summary>
    void PlaySound(byte[] wav, double volume);

    /// <summary>
    /// Shows a still of the (already activated) client window and lets the user drag the member list,
    /// then optionally the member count. Null when cancelled; an error message when the window cannot be shown.
    /// </summary>
    Task<AlertSelection> SelectAreasAsync(IntPtr clientWindow, AlertArea? list, AlertArea? count);
}

public sealed record AlertSelection(AlertArea List, AlertArea? Count, string Error = null)
{
    public static AlertSelection Failed(string error) => new(default, null, error);
}

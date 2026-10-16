using System;
using System.Threading.Tasks;
using Avalonia;
using EveOPreview.Alerts;
using EveOPreview.Mac.Mirrors;
using EveOPreview.Mac.Native;

namespace EveOPreview.Mac.Alerts;

/// <summary>
/// Local alerts on macOS: ScreenCaptureKit captures the watched area of each client window (also while
/// covered by other windows), Vision reads the member count and NSSound plays the alarms. Areas are
/// points relative to the client window's top-left corner, like UI mirrors.
/// </summary>
internal sealed class MacAlertPlatform : IAlertPlatform
{
    public bool CanRecognizeText => true;

    public IAlertSampler CreateSampler(IntPtr clientWindow, AlertArea area) => new MacAlertSampler((uint)clientWindow, area);

    public Task<string> RecognizeTextAsync(AlertFrame frame) =>
        Task.Run(() => MacNative.RecognizeText(frame.Pixels, frame.Width, frame.Height, frame.Stride));

    public void PlaySound(byte[] wav, double volume) => MacNative.PlaySound(wav, volume);

    public async Task<AlertSelection> SelectAreasAsync(IntPtr clientWindow, AlertArea? list, AlertArea? count)
    {
        uint windowId = (uint)clientWindow;
        if (!MacNative.TryGetWindowBounds(windowId, out var bounds)) return AlertSelection.Failed("The client window is not available.");
        var still = await Task.Run(() => MacNative.CaptureWindow(windowId, (int)(bounds.Width * 2), (int)(bounds.Height * 2)));
        if (still == null) return AlertSelection.Failed("The client could not be captured. Check Screen Recording access on the macOS page.");
        var listRect = await RegionSelector.SelectAsync(bounds, still, ToRect(list),
            "Drag over the Local member list: the standing icons and the names. Esc cancels.", new Size(12, 12));
        if (listRect is not { } member) return null;
        var countRect = await RegionSelector.SelectAsync(bounds, still, ToRect(count),
            "Now drag a small box over the member count (the number of pilots in Local). Esc skips the count.", new Size(6, 6), member);
        return new AlertSelection(FromRect(member), countRect is { } number ? FromRect(number) : null);
    }

    private static Rect? ToRect(AlertArea? area) => area is { IsUsable: true } a ? new Rect(a.X, a.Y, a.Width, a.Height) : null;

    private static AlertArea FromRect(Rect rect) =>
        new(Math.Round(rect.X, 1), Math.Round(rect.Y, 1), Math.Round(rect.Width, 1), Math.Round(rect.Height, 1));
}

/// <summary>Reads the newest captured frame on demand; a hidden application or minimized window draws nothing.</summary>
internal sealed unsafe class MacAlertSampler : IAlertSampler
{
    private readonly uint _window;
    private readonly int _pid;
    private long _id;
    private long _sequence;
    private AlertFrame _frame;

    public MacAlertSampler(uint window, AlertArea area)
    {
        _window = window;
        _pid = MacNative.WindowPid(window);
        _id = MacNative.SamplerCreate(window, area.X, area.Y, area.Width, area.Height);
    }

    public AlertSample Read()
    {
        if (_id == 0) return new(AlertSampleState.Unavailable, null);
        if (_pid > 0 && (MacNative.IsApplicationHidden(_pid) != 0 || MacNative.IsWindowMinimized(_pid, _window) != 0))
            return new(AlertSampleState.Stale, null);
        int width = 0, height = 0, stride = 0;
        long sequence = 0;
        double scale = 1;
        int status = MacNative.SamplerRead(_id, null, 0, _sequence, &width, &height, &stride, &sequence, &scale);
        if (status == 4 && width > 0 && height > 0 && stride >= width * 4)
        {
            // A new frame: copy into a fresh buffer, because earlier frames may still be in use (Discord image, diagnostic view).
            var pixels = new byte[stride * height];
            fixed (byte* target = pixels)
                status = MacNative.SamplerRead(_id, target, pixels.Length, _sequence, &width, &height, &stride, &sequence, &scale);
            if (status == 1 && pixels.Length >= stride * height)
            {
                _sequence = sequence;
                _frame = new AlertFrame(width, height, stride, pixels, scale);
            }
        }
        if (status == 3) return new(AlertSampleState.Unavailable, null);
        return _frame == null ? new(AlertSampleState.Unavailable, null) : new(AlertSampleState.Live, _frame);
    }

    public void Dispose()
    {
        if (_id != 0) MacNative.SamplerDestroy(_id);
        _id = 0;
    }
}

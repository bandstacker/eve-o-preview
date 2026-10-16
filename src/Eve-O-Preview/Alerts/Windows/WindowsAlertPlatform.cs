using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using EveOPreview.Mirrors;
using EveOPreview.Services;
using EveOPreview.Services.Interop;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace EveOPreview.Alerts;

/// <summary>
/// Local alerts on Windows: PrintWindow capture of the client area (also while covered by other windows),
/// Windows text recognition for the member count and in-memory WAV playback. Areas are client-area
/// pixels, like UI mirrors.
/// </summary>
internal sealed class WindowsAlertPlatform(IWindowManager windows) : IAlertPlatform
{
    private const uint SoundAsync = 0x0001, SoundNoDefault = 0x0002, SoundMemory = 0x0004;
    private readonly Lazy<OcrEngine> _ocr = new(CreateEngine);
    private readonly object _soundLock = new();
    private IntPtr _playing;

    public bool CanRecognizeText => _ocr.Value != null;

    public IAlertSampler CreateSampler(IntPtr clientWindow, AlertArea area) => new WindowsAlertSampler(windows, clientWindow, area);

    private static OcrEngine CreateEngine()
    {
        try { return OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); }
        catch (Exception ex) when (ex is COMException or TypeLoadException or PlatformNotSupportedException) { return null; }
    }

    public async Task<string> RecognizeTextAsync(AlertFrame frame)
    {
        var engine = _ocr.Value;
        if (engine == null) return null;
        var (pixels, width, height) = Compose(frame, (int)OcrEngine.MaxImageDimension);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Count == 0 ? null : string.Join("\n", result.Lines.Select(line => line.Text));
    }

    /// <summary>
    /// The crop enlarged, padded with its own background and preceded by the word "Count": text
    /// recognition misreads glyphs touching the image edge and skips a lone digit. The word has no digit,
    /// so count parsing ignores it.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) Compose(AlertFrame frame, int maximum)
    {
        int r = 0, g = 0, b = 0;
        foreach (var (x, y) in new[] { (0, 0), (frame.Width - 1, 0), (0, frame.Height - 1), (frame.Width - 1, frame.Height - 1) })
        {
            int i = y * frame.Stride + x * 4;
            b += frame.Pixels[i]; g += frame.Pixels[i + 1]; r += frame.Pixels[i + 2];
        }
        var background = Color.FromArgb(r / 4, g / 4, b / 4);
        bool dark = (r * 0.3 + g * 0.59 + b * 0.11) / 4 < 128;
        double factor = Math.Clamp(96.0 / frame.Height, 1, 4);
        int cropWidth = (int)(frame.Width * factor), cropHeight = (int)(frame.Height * factor), pad = cropHeight / 2;
        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            // Rgb ignores alpha: PrintWindow often leaves it at zero.
            using var source = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            using var font = new Font("Segoe UI", Math.Max(6, cropHeight * 0.45f), FontStyle.Bold, GraphicsUnit.Pixel);
            int labelWidth;
            using (var measure = Graphics.FromImage(source)) labelWidth = (int)Math.Ceiling(measure.MeasureString("Count ", font).Width);
            int width = Math.Min(maximum, pad + labelWidth + cropWidth + pad), height = Math.Min(maximum, cropHeight + 2 * pad);
            using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(background);
                graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                using var brush = new SolidBrush(dark ? Color.White : Color.Black);
                graphics.DrawString("Count ", font, brush, pad, pad + (cropHeight - font.GetHeight()) / 2);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(pad + labelWidth, pad, cropWidth, cropHeight));
            }
            var data = canvas.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
                return (pixels, width, height);
            }
            finally { canvas.UnlockBits(data); }
        }
        finally { handle.Free(); }
    }

    public void PlaySound(byte[] wav, double volume)
    {
        if (wav is not { Length: > 0 }) return;
        var scaled = AlertSounds.ScaleVolume(wav, volume);
        lock (_soundLock)
        {
            // Asynchronous playback reads the buffer until it ends; stop the previous sound before freeing it.
            PlaySoundW(IntPtr.Zero, IntPtr.Zero, 0);
            if (_playing != IntPtr.Zero) Marshal.FreeHGlobal(_playing);
            _playing = Marshal.AllocHGlobal(scaled.Length);
            Marshal.Copy(scaled, 0, _playing, scaled.Length);
            PlaySoundW(_playing, IntPtr.Zero, SoundMemory | SoundAsync | SoundNoDefault);
        }
    }

    public async Task<AlertSelection> SelectAreasAsync(IntPtr clientWindow, AlertArea? list, AlertArea? count)
    {
        if (MirrorNativeMethods.ClientScreenBounds(clientWindow) is not { } bounds)
            return AlertSelection.Failed("The client window is not available. Restore it and try again.");
        var still = await Task.Run(() => CaptureStill(clientWindow));
        if (still == null) return AlertSelection.Failed("The client could not be captured. Make sure it is not minimized and try again.");
        var member = await MirrorRegionSelector.SelectAsync(bounds, still, ToRectangle(list),
            "Drag over the Local member list: the standing icons and the names. Esc cancels.", new Size(12, 12));
        if (member is not { } area) return null;
        Rectangle? number = CanRecognizeText
            ? await MirrorRegionSelector.SelectAsync(bounds, still, ToRectangle(count),
                "Now drag a small box over the member count (the number of pilots in Local). Esc skips the count.", new Size(6, 6), area)
            : null;
        return new AlertSelection(FromRectangle(area), number is { } box ? FromRectangle(box) : null);
    }

    private byte[] CaptureStill(IntPtr window)
    {
        using var image = windows.GetStaticThumbnail(window);
        if (image == null) return null;
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Rectangle? ToRectangle(AlertArea? area) => area is { IsUsable: true } a
        ? new Rectangle((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(a.Width), (int)Math.Round(a.Height)) : null;

    private static AlertArea FromRectangle(Rectangle rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySoundW(IntPtr sound, IntPtr module, uint flags);
}

/// <summary>Captures the client area on each read and crops the watched area; a minimized client draws nothing.</summary>
internal sealed class WindowsAlertSampler(IWindowManager windows, IntPtr window, AlertArea area) : IAlertSampler
{
    public AlertSample Read()
    {
        if (!User32NativeMethods.IsWindow(window)) return new(AlertSampleState.Unavailable, null);
        if (User32NativeMethods.IsIconic(window)) return new(AlertSampleState.Stale, null);
        using var image = windows.GetStaticThumbnail(window) as Bitmap;
        if (image == null) return new(AlertSampleState.Unavailable, null);
        var crop = Rectangle.Intersect(new Rectangle((int)Math.Round(area.X), (int)Math.Round(area.Y),
            (int)Math.Round(area.Width), (int)Math.Round(area.Height)), new Rectangle(0, 0, image.Width, image.Height));
        if (crop.Width < 4 || crop.Height < 4) return new(AlertSampleState.Unavailable, null);
        var data = image.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[crop.Width * crop.Height * 4];
            for (int y = 0; y < crop.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * crop.Width * 4, crop.Width * 4);
            return new(AlertSampleState.Live, new AlertFrame(crop.Width, crop.Height, crop.Width * 4, pixels));
        }
        finally { image.UnlockBits(data); }
    }

    public void Dispose() { }
}

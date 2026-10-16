using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using EveOPreview.Alerts;
using Serilog;
using Xunit;

namespace EveOPreview.Tests.Checks;

// Platform-neutral: also linked into the macOS tests.
public class AlertTests
{
    // Colours measured on client standing icons (fill, rim); white glyphs.
    private const uint Neutral = 0x6E6E6E, NeutralRim = 0x8C8C8C, Red = 0x750A0A, RedRim = 0x950606;
    private const uint Corp = 0x2F7A1E, CorpRim = 0x3A9426, Blue = 0x0B3D91, BlueRim = 0x134EB0, Fleet = 0x7A2A8F, FleetRim = 0x9133AA;
    private const uint Orange = 0xB85C00, OrangeRim = 0xD06A00;

    private static AlertFrame Frame(int width, int height, double scale = 1)
    {
        var frame = new AlertFrame(width, height, width * 4, new byte[width * height * 4], scale);
        Fill(frame, 0, 0, width, height, 0x121214);
        return frame;
    }

    private static void Fill(AlertFrame frame, int x, int y, int width, int height, uint rgb)
    {
        for (int py = y; py < y + height; py++)
            for (int px = x; px < x + width; px++)
            {
                int i = py * frame.Stride + px * 4;
                frame.Pixels[i] = (byte)rgb; frame.Pixels[i + 1] = (byte)(rgb >> 8); frame.Pixels[i + 2] = (byte)(rgb >> 16); frame.Pixels[i + 3] = 255;
            }
    }

    /// <summary>A standing icon like the client's: rim, fill and a white glyph, at a UI scale.</summary>
    private static void Icon(AlertFrame frame, int x, int y, uint fill, uint rim, char glyph, int scale = 1)
    {
        int s = 12 * scale, u = scale;
        Fill(frame, x, y, s, s, rim);
        Fill(frame, x + u, y + u, s - 2 * u, s - 2 * u, fill);
        switch (glyph)
        {
            case '=': Fill(frame, x + 3 * u, y + 3 * u, 6 * u, 2 * u, 0xFFFFFF); Fill(frame, x + 3 * u, y + 7 * u, 6 * u, 2 * u, 0xFFFFFF); break;
            case '-': Fill(frame, x + 3 * u, y + 5 * u, 6 * u, 2 * u, 0xFFFFFF); break;
            case '+': Fill(frame, x + 3 * u, y + 5 * u, 6 * u, 2 * u, 0xFFFFFF); Fill(frame, x + 5 * u, y + 3 * u, 2 * u, 6 * u, 0xFFFFFF); break;
            default: Fill(frame, x + 4 * u, y + 4 * u, 4 * u, 4 * u, 0xFFFFFF); break;
        }
    }

    /// <summary>Thin light strokes, like a pilot name.</summary>
    private static void Name(AlertFrame frame, int x, int y, int scale = 1)
    {
        for (int letter = 0; letter < 8; letter++)
        {
            Fill(frame, x + letter * 7 * scale, y, scale, 9 * scale, 0xC8C8C8);
            Fill(frame, x + letter * 7 * scale, y + 4 * scale, 4 * scale, scale, 0x9A9A9A);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void StandingIconsAreFoundAndClassifiedAtEachUiScale(int scale)
    {
        var frame = Frame(220 * scale, 140 * scale, scale);
        (uint Fill, uint Rim, char Glyph)[] rows = [(Neutral, NeutralRim, '='), (Red, RedRim, '-'), (Corp, CorpRim, '*'),
            (Blue, BlueRim, '+'), (Fleet, FleetRim, '*'), (Orange, OrangeRim, '-')];
        for (int i = 0; i < rows.Length; i++)
        {
            Icon(frame, 6 * scale, (4 + i * 20) * scale, rows[i].Fill, rows[i].Rim, rows[i].Glyph, scale);
            Name(frame, 26 * scale, (6 + i * 20) * scale, scale);
        }

        var scan = StandingDetector.Detect(frame);

        Assert.Equal([StandingColor.Grey, StandingColor.Red, StandingColor.Green, StandingColor.Blue, StandingColor.Purple, StandingColor.Orange],
            scan.Icons.Select(i => i.Color));
        Assert.Equal(3, scan.Hostile);
        Assert.Equal(3, scan.Friendly);
        Assert.Equal("1 red, 1 orange, 1 neutral", scan.DescribeHostile());
        Assert.All(scan.Icons, icon => Assert.InRange(icon.Width, 12 * scale - 1, 12 * scale + 1));
    }

    [Fact]
    public void ACapturedFillVaryingAcrossAShadeBoundaryIsStillOneIcon()
    {
        // Captures resample the icon: the neutral fill arrives as #6D-#71, the +5 fill as #BE-#C0 (blue channel).
        var frame = Frame(80, 40, 2);
        Icon(frame, 4, 4, Neutral, NeutralRim, '=', 2);
        Icon(frame, 44, 4, 0x437DBE, 0x4F8FD2, '+', 2);
        for (int y = 8; y < 26; y += 2)
        {
            Fill(frame, 6, y, 20, 1, 0x717171);
            Fill(frame, 46, y, 20, 1, 0x447EC0);
        }
        var scan = StandingDetector.Detect(frame);
        Assert.Equal([StandingColor.Grey, StandingColor.Blue], scan.Icons.Select(i => i.Color));
    }

    [Fact]
    public void PanelsButtonsAndTextAreNotIcons()
    {
        var frame = Frame(320, 160);
        Fill(frame, 10, 10, 40, 20, Neutral);       // A plain grey button.
        Fill(frame, 100, 10, 200, 120, Red);        // A large red area (a nebula behind a translucent window).
        Fill(frame, 60, 40, 3, 90, NeutralRim);     // A scroll bar.
        Name(frame, 10, 140);
        // The letter "a" of a pilot name at 2x, as captured: anti-aliased greys all round a white core.
        uint[][] letter =
        [
            [0x121215, 0x1E1E20, 0x414143, 0x5D5D5F, 0x717172, 0x7A7A7C, 0x727273, 0x606061, 0x444446, 0x242426, 0x141517],
            [0x1B1B1E, 0x5A5A5B, 0x9C9B9C, 0xBABABB, 0xC3C4C4, 0xC7C6C7, 0xC4C3C4, 0xBDBDBD, 0x9F9FA0, 0x5E5E60, 0x272729],
            [0x3F3E40, 0xAAAAAA, 0xD0D0D1, 0xCACACA, 0xAFAFB0, 0xA5A5A6, 0xB3B3B3, 0xCACACA, 0xD2D2D2, 0xA1A1A2, 0x4B4B4D],
            [0x59585A, 0xB0B0B0, 0xB5B5B6, 0x888788, 0x515153, 0x404142, 0x59595B, 0x949496, 0xCDCDCD, 0xC1C1C1, 0x6D6D6F],
            [0x3D3C3F, 0x656466, 0x626262, 0x39393B, 0x1B1B1E, 0x141416, 0x1F1F21, 0x5F5F62, 0xBCBCBC, 0xCACBCA, 0x828283],
            [0x1A1A1C, 0x29282A, 0x3B3B3D, 0x404042, 0x49494A, 0x4F4F50, 0x525253, 0x787879, 0xC0C1C1, 0xCECFCE, 0x8A8A8B],
            [0x232325, 0x525253, 0x7D7D7E, 0x979798, 0xAAAAAA, 0xB4B4B4, 0xB8B8B8, 0xC1C1C1, 0xD3D3D3, 0xCFCFCF, 0x8B8B8C],
            [0x5B5B5C, 0xAAABAB, 0xC7C7C7, 0xC7C7C7, 0xBCBCBC, 0xB2B2B2, 0xACABAC, 0xB4B4B4, 0xCFCFD0, 0xCFCFCF, 0x8B8B8C],
            [0x9E9E9E, 0xD5D5D5, 0xCBCBCB, 0x939394, 0x6D6E6E, 0x585859, 0x4B4B4C, 0x6D6D6F, 0xBDBDBE, 0xCFCFCF, 0x8B8B8B],
            [0xC0C0C0, 0xDBDBDB, 0xB3B3B4, 0x454547, 0x232425, 0x1B1B1E, 0x212123, 0x636364, 0xBDBDBD, 0xCFCFCF, 0x8B8B8B],
            [0xBDBEBE, 0xDADBDB, 0xB6B6B7, 0x4E4F50, 0x2B2B2D, 0x2C2C2F, 0x4F4F51, 0x969697, 0xCCCBCC, 0xCFCFCF, 0x8A8A8B],
            [0x9D9D9E, 0xD5D5D6, 0xCDCDCE, 0x9C9C9D, 0x7D7D7E, 0x818182, 0xA3A3A3, 0xC6C7C6, 0xD7D7D7, 0xCFCFCF, 0x8B8A8B],
            [0x5C5C5E, 0xACACAD, 0xCECFCF, 0xD0D0D0, 0xC8C8C9, 0xC9C9CA, 0xC0C0C0, 0xB5B5B5, 0xCBCCCC, 0xCECECE, 0x8A8A8B],
            [0x232325, 0x59585A, 0x878788, 0x9B9B9C, 0xA0A0A1, 0x9B9B9C, 0x7B7B7B, 0x626263, 0x808181, 0x8C8C8D, 0x606062],
        ];
        for (int y = 0; y < letter.Length; y++)
            for (int x = 0; x < letter[y].Length; x++) Fill(frame, 70 + x, 60 + y, 1, 1, letter[y][x]);

        Assert.Empty(StandingDetector.Detect(frame).Icons);
    }

    [Fact]
    public void AnnotatedViewOutlinesIconsWithoutChangingTheSource()
    {
        var frame = Frame(60, 40);
        Icon(frame, 10, 10, Red, RedRim, '-');
        ulong before = frame.Fingerprint();
        var copy = StandingDetector.Annotate(frame, StandingDetector.Detect(frame));
        Assert.NotEqual(before, copy.Fingerprint());
        Assert.Equal(before, frame.Fingerprint());
    }

    private static AlertObservation Seen(int hostile, int friendly, int? count = null)
    {
        var icons = Enumerable.Range(0, hostile).Select(i => new StandingIcon(0, i * 20, 12, 12, StandingColor.Red))
            .Concat(Enumerable.Range(0, friendly).Select(i => new StandingIcon(0, 400 + i * 20, 12, 12, StandingColor.Green))).ToArray();
        return new(AlertSampleState.Live, new StandingScan(icons), count);
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Runs the observation at 2 scans per second for the given seconds; returns all events and the end time.</summary>
    private static (AlertEventKind[] Events, DateTimeOffset Time) Run(AlertWatchMonitor monitor, AlertObservation observation, DateTimeOffset from,
        double seconds, int reminder = 30)
    {
        var events = new System.Collections.Generic.List<AlertEventKind>();
        var time = from;
        for (int i = 0; i < (int)(seconds * 2); i++)
        {
            time = time.AddMilliseconds(500);
            events.AddRange(monitor.Update(observation, time, reminder).Select(e => e.Kind));
        }
        return (events.ToArray(), time);
    }

    [Fact]
    public void ArrivalAlarmsOnceThenRemindsUntilAcknowledgedAndClearsAfterTenSeconds()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (events, time) = Run(monitor, Seen(0, 2), Start, 2);
        Assert.Empty(events);

        (events, time) = Run(monitor, Seen(1, 2), time, 2);
        Assert.Equal([AlertEventKind.HostileArrived], events);
        Assert.True(monitor.IsActive && monitor.IsAlarmed);

        (events, time) = Run(monitor, Seen(1, 2), time, 31, reminder: 30);
        Assert.Equal([AlertEventKind.Reminder], events);

        monitor.Acknowledge();
        (events, time) = Run(monitor, Seen(1, 2), time, 61, reminder: 30);
        Assert.Empty(events);

        (events, time) = Run(monitor, Seen(0, 2), time, 9);
        Assert.Empty(events);
        (events, _) = Run(monitor, Seen(0, 2), time, 2);
        Assert.Equal([AlertEventKind.AllClear], events);
        Assert.False(monitor.IsAlarmed);
    }

    [Fact]
    public void SomeonePresentWhenWatchingStartsAlarms()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (events, _) = Run(monitor, Seen(2, 0), Start, 2);
        Assert.Equal([AlertEventKind.HostileArrived], events);
        Assert.Equal(2, monitor.Hostile);
    }

    [Fact]
    public void ASingleMisreadScanNeverAlarms()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(0, 1, 5), Start, 2);
        Assert.Empty(monitor.Update(Seen(1, 1, 9), time.AddMilliseconds(500), 30));
        var (events, _) = Run(monitor, Seen(0, 1, 5), time.AddMilliseconds(500), 5);
        Assert.Empty(events);
    }

    [Fact]
    public void CountIncreaseWithoutANewIconIsAnUnidentifiedArrival()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(0, 1, 2), Start, 2);
        var (events, time2) = Run(monitor, Seen(0, 1, 3), time, 4);
        Assert.Equal([AlertEventKind.UnknownArrived], events);
        Assert.Equal(1, monitor.Unexplained);

        // Leaving restores the clean state and sends the all-clear.
        (events, _) = Run(monitor, Seen(0, 1, 2), time2, 12);
        Assert.Equal([AlertEventKind.AllClear], events);
        Assert.Equal(0, monitor.Unexplained);
    }

    [Fact]
    public void CountIncreaseExplainedByAFriendlyIconDoesNotAlarm()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(0, 1, 2), Start, 2);
        // The count updates a scan before the new corp member's row appears.
        var (events, time2) = Run(monitor, Seen(0, 1, 3), time, 1);
        Assert.Empty(events);
        (events, _) = Run(monitor, Seen(0, 2, 3), time2, 5);
        Assert.Empty(events);
        Assert.False(monitor.IsActive);
    }

    [Fact]
    public void CountIncreaseFromAHostileIconAlarmsOnlyOnce()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(0, 1, 2), Start, 2);
        var (events, _) = Run(monitor, Seen(1, 1, 3), time, 5);
        Assert.Equal([AlertEventKind.HostileArrived], events);
        Assert.Equal(0, monitor.Unexplained);
    }

    [Fact]
    public void AWatchThatSeesNothingPausesAndResumes()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(0, 1), Start, 2);
        var hidden = new AlertObservation(AlertSampleState.Stale, null, null);
        var (events, time2) = Run(monitor, hidden, time, 6);
        Assert.Equal([AlertEventKind.Paused], events);
        Assert.True(monitor.IsPaused);
        (events, _) = Run(monitor, Seen(0, 1), time2, 1);
        Assert.Equal([AlertEventKind.Resumed], events);
    }

    [Theory]
    [InlineData("Local [12]", 12)]
    [InlineData("12", 12)]
    [InlineData("Local [1O]", 10)]
    [InlineData("Members: 7", 7)]
    [InlineData("Local\n(3)", 3)]
    [InlineData("", null)]
    [InlineData("Local", null)]
    public void MemberCountIsReadFromRecognisedText(string text, int? expected) => Assert.Equal(expected, AlertText.ParseCount(text));

    [Fact]
    public void PngIsAValidImageOfTheFrame()
    {
        var frame = Frame(7, 5);
        var png = AlertPng.Encode(frame);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(png, 41, length), CompressionMode.Decompress)) zlib.CopyTo(inflated);
        Assert.Equal(5 * (7 * 4 + 1), inflated.Length);
    }

    [Fact]
    public void BuiltInSoundsAreWavesAndVolumeScalesSamples()
    {
        Assert.True(AlertSounds.IsWave(AlertSounds.Hostile));
        Assert.True(AlertSounds.IsWave(AlertSounds.Count));
        var half = AlertSounds.ScaleVolume(AlertSounds.Hostile, 0.5);
        int peak = 0, halfPeak = 0;
        for (int i = 44; i < AlertSounds.Hostile.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(AlertSounds.Hostile.AsSpan(i))));
            halfPeak = Math.Max(halfPeak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(half.AsSpan(i))));
        }
        Assert.InRange(halfPeak, peak / 2 - 2, peak / 2 + 2);
        Assert.Null(AlertSounds.Resolve(AlertOptions.NoSound, AlertSounds.Hostile));
        Assert.Same(AlertSounds.Hostile, AlertSounds.Resolve("/nonexistent/file.wav", AlertSounds.Hostile));
    }

    [Fact]
    public void DiscordEmbedsCarrySystemCharacterAndImage()
    {
        var time = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        using var arrival = JsonDocument.Parse(DiscordWebhook.ArrivalPayload("Hostile in Local", "Jita", "Maya Aldard", "1 red · Local 4", true, time));
        var embed = arrival.RootElement.GetProperty("embeds")[0];
        Assert.Equal("Hostile in Local", embed.GetProperty("title").GetString());
        Assert.Equal(["System:Jita", "Character:Maya Aldard", "Spotted:1 red · Local 4"],
            embed.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString() + ":" + f.GetProperty("value").GetString()));
        Assert.Equal("attachment://local.png", embed.GetProperty("image").GetProperty("url").GetString());

        using var clear = JsonDocument.Parse(DiscordWebhook.AllClearPayload(null, "Maya Aldard", time));
        var clearEmbed = clear.RootElement.GetProperty("embeds")[0];
        Assert.Equal("All clear", clearEmbed.GetProperty("title").GetString());
        Assert.Equal("Unknown system", clearEmbed.GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.False(clearEmbed.TryGetProperty("image", out _));

        Assert.True(DiscordWebhook.IsValidUrl("https://discord.com/api/webhooks/123/abc"));
        Assert.False(DiscordWebhook.IsValidUrl("http://discord.com/api/webhooks/123/abc"));
        Assert.False(DiscordWebhook.IsValidUrl("https://example.com/api/webhooks/123/abc"));
    }

    [Fact]
    public void WatchesAndOptionsRoundTripPerProfile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eveo-alerts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AlertStore(new LoggerConfiguration().CreateLogger());
            var document = new AlertDocument
            {
                Options = new AlertOptions { ReminderSeconds = 15, WebhookUrl = "https://discord.com/api/webhooks/1/x", WebhookEnabled = true },
                Watches = [new AlertWatch { Name = "Maya Local", SourceTitle = "EVE - Maya Aldard", List = new(700, 90, 280, 360), Count = new(708, 70, 90, 18) }],
            };
            store.Save(folder, document);
            var loaded = store.Load(folder);
            Assert.Equal(15, loaded.Options.ReminderSeconds);
            Assert.True(loaded.Options.WebhookEnabled);
            var watch = Assert.Single(loaded.Watches);
            // The count is read with a margin of half its height beside and a third above and below.
            Assert.Equal(new AlertArea(699, 63.7, 281, 386.3), watch.Capture with { Y = Math.Round(watch.Capture.Y, 1), Height = Math.Round(watch.Capture.Height, 1) });
            Assert.Equal("Maya Aldard", watch.Character);
            Assert.Empty(new AlertStore(new LoggerConfiguration().CreateLogger()).Load(Path.Combine(folder, "missing")).Watches);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void CropsFollowTheFrameScale()
    {
        var frame = Frame(200, 100, scale: 2);
        Fill(frame, 40, 20, 10, 10, 0xFF0000);
        var crop = frame.Crop(new AlertArea(120, 60, 10, 10), new AlertArea(100, 50, 100, 50));
        Assert.Equal(20, crop.Width);
        Assert.Equal(20, crop.Height);
        Assert.Equal(0xFF, crop.Pixels[2]); // Top-left pixel is the red square.
    }
}

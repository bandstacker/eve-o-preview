using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EveOPreview.Alerts;

/// <summary>Built-in alarm tones (generated, so no third-party audio is bundled) and WAV helpers.</summary>
public static class AlertSounds
{
    private const int SampleRate = 22050;

    /// <summary>Urgent two-tone alarm for a non-friendly standing icon.</summary>
    public static byte[] Hostile { get; } = Synthesize([(880, .13), (0, .04), (587, .13), (0, .04), (880, .13), (0, .04), (587, .13)]);
    /// <summary>Softer double beep for an unidentified count increase.</summary>
    public static byte[] Count { get; } = Synthesize([(660, .11), (0, .07), (660, .11)]);

    /// <summary>The sound for a setting: built-in for "default", nothing for "none", else the WAV file (built-in if unreadable).</summary>
    public static byte[] Resolve(string choice, byte[] builtIn)
    {
        if (string.IsNullOrWhiteSpace(choice) || choice == AlertOptions.DefaultSound) return builtIn;
        if (choice == AlertOptions.NoSound) return null;
        try
        {
            var data = File.ReadAllBytes(choice);
            return IsWave(data) ? data : builtIn;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return builtIn; }
    }

    public static bool IsWave(byte[] data) => data is { Length: > 44 }
        && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "WAVE";

    /// <summary>A copy with 16-bit PCM samples scaled by <paramref name="volume"/>; other formats are returned unchanged.</summary>
    public static byte[] ScaleVolume(byte[] wav, double volume)
    {
        if (!IsWave(wav) || volume >= 0.999) return wav;
        int offset = 12, format = 0, bits = 0;
        while (offset + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4));
            if (size < 0 || offset + 8 + size > wav.Length) size = wav.Length - offset - 8;
            if (id == "fmt " && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 8));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 22));
            }
            else if (id == "data")
            {
                if (format != 1 || bits != 16) return wav;
                var copy = (byte[])wav.Clone();
                double factor = Math.Clamp(volume, 0, 1);
                for (int i = offset + 8; i + 1 < offset + 8 + size; i += 2)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(copy.AsSpan(i));
                    BinaryPrimitives.WriteInt16LittleEndian(copy.AsSpan(i), (short)Math.Round(sample * factor));
                }
                return copy;
            }
            offset += 8 + size + (size & 1);
        }
        return wav;
    }

    private static byte[] Synthesize((double Frequency, double Seconds)[] notes)
    {
        int total = 0;
        foreach (var note in notes) total += (int)(note.Seconds * SampleRate);
        var wav = new byte[44 + total * 2];
        void Text(int at, string value) => Encoding.ASCII.GetBytes(value).CopyTo(wav, at);
        Text(0, "RIFF"); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8); Text(8, "WAVE");
        Text(12, "fmt "); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), SampleRate); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        Text(36, "data"); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), total * 2);
        int position = 44;
        foreach (var (frequency, seconds) in notes)
        {
            int count = (int)(seconds * SampleRate), ramp = Math.Min(count / 2, SampleRate / 200);
            for (int i = 0; i < count; i++)
            {
                double t = i / (double)SampleRate, envelope = Math.Min(1, Math.Min(i, count - 1 - i) / (double)Math.Max(1, ramp));
                double value = frequency <= 0 ? 0 : (Math.Sin(2 * Math.PI * frequency * t) + 0.25 * Math.Sin(4 * Math.PI * frequency * t)) / 1.25;
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(position), (short)(value * envelope * 0.7 * short.MaxValue));
                position += 2;
            }
        }
        return wav;
    }
}

public static partial class AlertText
{
    /// <summary>
    /// The member count in recognised text: a bracketed number ("Local [12]") if present, else the
    /// last number. Common recognition slips inside numbers (O for 0, l or I for 1) are corrected.
    /// </summary>
    public static int? ParseCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string normalized = Slips().Replace(text, m => m.Value.Replace('O', '0').Replace('o', '0')
            .Replace('I', '1').Replace('l', '1').Replace('|', '1'));
        var bracket = Bracketed().Match(normalized);
        if (bracket.Success) return int.Parse(bracket.Groups[1].Value);
        var numbers = Numbers().Matches(normalized);
        return numbers.Count == 0 ? null : int.Parse(numbers[^1].Value);
    }

    [GeneratedRegex(@"[\[\(]\s*(\d{1,4})\s*[\]\)]")] private static partial Regex Bracketed();
    [GeneratedRegex(@"(?<![\d.,:])\d{1,4}(?![\d.,:])")] private static partial Regex Numbers();
    // Tokens mixing digits with look-alike letters, for example "1O" or "l2".
    [GeneratedRegex(@"\b(?=[0-9OoIl|]*\d)[0-9OoIl|]{1,4}\b")] private static partial Regex Slips();
}

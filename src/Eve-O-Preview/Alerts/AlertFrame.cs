using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace EveOPreview.Alerts;

/// <summary>Top-down BGRA pixels of a captured area. <see cref="Scale"/> is pixels per window unit.</summary>
public sealed class AlertFrame
{
    public AlertFrame(int width, int height, int stride, byte[] pixels, double scale = 1)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels is null || pixels.Length < stride * (height - 1) + width * 4)
            throw new ArgumentException("The frame does not match its pixel buffer.");
        Width = width; Height = height; Stride = stride; Pixels = pixels;
        Scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }
    public double Scale { get; }

    /// <summary>The part of this frame showing <paramref name="area"/>, when the frame shows <paramref name="origin"/>.</summary>
    public AlertFrame Crop(AlertArea area, AlertArea origin)
    {
        int left = Math.Clamp((int)Math.Round((area.X - origin.X) * Scale), 0, Width - 1);
        int top = Math.Clamp((int)Math.Round((area.Y - origin.Y) * Scale), 0, Height - 1);
        int width = Math.Clamp((int)Math.Round(area.Width * Scale), 1, Width - left);
        int height = Math.Clamp((int)Math.Round(area.Height * Scale), 1, Height - top);
        if (left == 0 && top == 0 && width == Width && height == Height) return this;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(Pixels, (top + y) * Stride + left * 4, pixels, y * width * 4, width * 4);
        return new(width, height, width * 4, pixels, Scale);
    }

    /// <summary>A cheap content hash, so unchanged areas are not analysed again.</summary>
    public ulong Fingerprint()
    {
        ulong hash = 14695981039346656037UL;
        for (int y = 0; y < Height; y++)
        {
            var row = MemoryMarshal.Cast<byte, ulong>(Pixels.AsSpan(y * Stride, Width * 4 & ~7));
            foreach (ulong value in row) hash = (hash ^ value) * 1099511628211UL;
        }
        return hash ^ (ulong)Width << 32 ^ (ulong)Height;
    }

    public AlertFrame Clone() => new(Width, Height, Stride, (byte[])Pixels.Clone(), Scale);

    /// <summary>Two frames one above the other with a small gap (diagnostic view).</summary>
    public static AlertFrame Stack(AlertFrame top, AlertFrame bottom, int gap = 6)
    {
        int width = Math.Max(top.Width, bottom.Width), height = top.Height + gap + bottom.Height;
        var pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        for (int y = 0; y < top.Height; y++) Buffer.BlockCopy(top.Pixels, y * top.Stride, pixels, y * width * 4, top.Width * 4);
        for (int y = 0; y < bottom.Height; y++)
            Buffer.BlockCopy(bottom.Pixels, y * bottom.Stride, pixels, (top.Height + gap + y) * width * 4, bottom.Width * 4);
        return new(width, height, width * 4, pixels, bottom.Scale);
    }

    /// <summary>Draws a one-pixel rectangle outline (used for the "what EVE-O sees" view).</summary>
    public void DrawRectangle(int x, int y, int width, int height, uint argb, int thickness = 1)
    {
        byte b = (byte)argb, g = (byte)(argb >> 8), r = (byte)(argb >> 16);
        void Put(int px, int py)
        {
            if (px < 0 || py < 0 || px >= Width || py >= Height) return;
            int i = py * Stride + px * 4;
            Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = 255;
        }
        for (int t = 0; t < thickness; t++)
        {
            for (int px = x - t; px < x + width + t; px++) { Put(px, y - 1 - t); Put(px, y + height + t); }
            for (int py = y - 1 - t; py <= y + height + t; py++) { Put(x - 1 - t, py); Put(x + width + t, py); }
        }
    }
}

/// <summary>Minimal PNG writer (RGBA, no filtering) for Discord attachments and the diagnostic view.</summary>
public static class AlertPng
{
    private static readonly uint[] Crc = BuildCrc();

    public static byte[] Encode(AlertFrame frame)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), frame.Height);
        header[8] = 8; header[9] = 6; // 8-bit RGBA
        Chunk(output, "IHDR", header);
        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[frame.Width * 4 + 1];
                for (int y = 0; y < frame.Height; y++)
                {
                    int source = y * frame.Stride;
                    for (int x = 0; x < frame.Width; x++)
                    {
                        int i = source + x * 4, o = 1 + x * 4;
                        row[o] = frame.Pixels[i + 2]; row[o + 1] = frame.Pixels[i + 1]; row[o + 2] = frame.Pixels[i]; row[o + 3] = 255;
                    }
                    zlib.Write(row);
                }
            }
            Chunk(output, "IDAT", raw.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        output.Write(typed);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typed) crc = Crc[(crc ^ value) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFF);
        output.Write(number);
    }

    private static uint[] BuildCrc()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

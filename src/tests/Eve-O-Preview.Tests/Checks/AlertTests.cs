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

    /// <summary>Copies captured pixels (rows of RRGGBB values) into the frame.</summary>
    private static void Paste(AlertFrame frame, int x, int y, string[] rows)
    {
        for (int row = 0; row < rows.Length; row++)
        {
            var pixels = rows[row].Split(' ');
            for (int column = 0; column < pixels.Length; column++)
                Fill(frame, x + column, y + row, 1, 1, Convert.ToUInt32(pixels[column], 16));
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
        foreach (int y in new[] { 8, 14, 16, 22, 24 })
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

    // Icon crops from real clients at 100% UI scale, and the textured one as the client resamples it at 110% and 90%.
    private static readonly string[] NeutralCrop =
    [
        "434343 848484 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 7D7D7D 434343",
        "7D7D7D 727272 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 727272 848484",
        "8C8C8C 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 8C8C8C",
        "8C8C8C 6E6E6E 6E6E6E FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF 6E6E6E 6E6E6E 8C8C8C",
        "8C8C8C 6E6E6E 6E6E6E FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF 6E6E6E 6E6E6E 8C8C8C",
        "8C8C8C 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 8C8C8C",
        "8C8C8C 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 8C8C8C",
        "8C8C8C 6E6E6E 6E6E6E FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF 6E6E6E 6E6E6E 8C8C8C",
        "8C8C8C 6E6E6E 6E6E6E FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF FFFFFF 6E6E6E 6E6E6E 8C8C8C",
        "8C8C8C 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 8C8C8C",
        "848484 727272 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 6E6E6E 727272 7D7D7D",
        "434343 7D7D7D 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 8C8C8C 848484 434343",
    ];
    private static readonly string[] SkullCrop =
    [
        "2F1414 780A0A 940606 930606 930606 930606 930606 930606 940606 920606 750A0A 331313",
        "520F0F 880808 740A0A 740A0A 740A0A 720707 720808 740A0A 730A0A 730A0A 890808 5B0E0E",
        "590E0E 8A0707 730A0A 750A0A 750A0A 801D1D 7A1313 750909 750A0A 730A0A 880808 5D0E0E",
        "580E0E 8A0707 730B0B 6E0000 6A0000 C89D9D C08F8F 700000 6E0000 730B0B 880808 5D0E0E",
        "580E0E 8A0707 6F0505 B07373 BA8484 FFFFFF FFFFFF C08E8E B37777 6F0505 880808 5D0E0E",
        "580E0E 8A0707 6E0303 B57C7C F7F0F0 FFFFFF FFFFFF F2E8E8 BA8484 6F0303 880808 5D0E0E",
        "580E0E 8A0707 720808 791313 E0C7C7 FBF8F8 FCF9F9 E6D2D2 760A0A 730909 880808 5D0E0E",
        "580E0E 8A0707 6F0202 AD6E6E D0ADAD 9C4D4D 9A4A4A D3B1B1 B57B7B 6F0202 880808 5D0E0E",
        "580E0E 8A0707 720808 811E1E 801E1E 740808 740707 801E1E 832222 720808 880808 5D0E0E",
        "530F0F 890808 770A0A 750808 740707 760909 760A0A 740707 740707 770A0A 860808 540F0F",
        "2B1515 6A0C0C 8C0707 8E0707 8E0707 8E0707 8E0707 8E0707 8E0707 8F0707 700B0B 2C1414",
    ];
    private static readonly string[] TexturedCrop =
    [
        "680000 86160B 7C0902 8A1211 8A0D0B 88050A 8A070C 8B0810 880F16 7C070D 84110C 720300",
        "781003 790900 820F08 7C0403 820503 8A070C 89060B 89060E 7D040B 830E14 7A0702 7E0F08",
        "8B1312 7F0706 730504 700608 750D0E 6F0A0E 670B0E 6D1114 74070D 780009 87040A 8E0B11",
        "7E0605 88100F 7D0F0E 72080A 7A1213 710C10 690D10 6B0F12 7A0D13 830914 89060C 820005",
        "860D12 82090E 630708 611A18 52130C 4E1C15 48231A 452017 581411 640809 870A10 8C0F15",
        "840B10 80070C 6F1314 FFE9E7 FFECE5 FFF1EA FFF5EC FFF3EA FFEBE8 6F1314 81040A 85080E",
        "8E0D12 86050A 6C070B FFE8E4 FFEAE4 FFECE3 FFF1E3 FFF2E4 FFF1E3 531103 7C0C0A 7F0F0D",
        "8B0A0F 830207 740F13 62130F 5D140E 591910 4E1D0F 4F1E10 511C0E 5D1B0D 7A0A08 841412",
        "840C0D 820A0B 710D0F 700C0E 6F0D0A 710F0C 6C120A 6C120A 800508 890209 8E0205 94080B",
        "840C0D 820A0B 720E10 700C0E 6E0C09 6E0C09 680E06 680E06 810609 8A030A 8E0205 93070A",
        "700E0B 790B0C 7A0A16 800716 830513 890213 8A020E 8B030F 7D0B15 760D14 730D09 6F120B",
        "6D0B08 780A0B 7A0A16 820918 860816 8C0516 8E0612 8F0713 7E0C16 770E15 730D09 6D1009",
    ];
    private static readonly string[] TexturedCropAt110 =
    [
        "360A0C 590D0A 62120C 5E0D0A 661111 660E0E 64090D 660A0E 670B11 641015 5E0B11 600F0E 5B0C0A 3A0B0C",
        "4D0E0A 780C03 7D0C03 800C06 800707 840705 8A070B 89060C 89070F 81060E 820A11 7E0B08 7C0B06 510E0C",
        "54110F 7F0D07 7B0804 790906 770607 7B0909 7C090D 78080D 7B0B10 79060D 7D070E 7F0608 830909 570F0F",
        "591111 850D0C 7F0907 750707 700808 760E0F 700B0F 680C0F 6C0F12 740A0F 79040C 84030C 89060D 5A0C10",
        "540C0D 830B0B 820E0F 740D0D 6F0D0F 6F1212 681011 611212 621413 701013 790A11 85080E 86050B 57090D",
        "570F13 840B10 7B080D 681413 72332F 672F28 643730 5E3D34 5C3A32 6A312D 671213 7F090E 890C12 5A1014",
        "560E12 82090E 7B0A0D 934848 FFE9E7 FFEDE5 FFF0EA FFF5EC FFF3EA FFEDE8 924A49 7B070C 82060C 560C10",
        "5C0F13 8A090E 80050A 904042 FFE8E4 FFEAE4 FFECE3 FFF1E3 FFF2E4 FFF1E3 7E493B 710D08 7D0D0B 531010",
        "5A0D11 87060B 80050A 6F1012 61130F 5D140F 591910 4E1D0F 4F1E0F 501C0F 5A1C0D 730E09 7F0F0D 561313",
        "560E10 830B0C 7E0B0D 700D0F 6F0D0E 6E0F0A 6F100D 6A120A 6A120B 7B0808 840409 8B0206 900509 5F0C0F",
        "560E10 830B0C 7E0B0C 720E0F 6F0C0E 6E0C09 6F0C09 680F07 680F07 7E0708 88040A 8D0206 900508 5F0C0E",
        "4D0F0F 780C0B 7B0B0E 790B15 7C0814 7F0711 820511 82050C 83060D 7F0911 7C0A11 7A0A0A 790C09 4F100F",
        "490E0D 730B0A 780A0E 7C0A16 820717 850715 8B0415 8D0511 8E0612 800A16 790D15 740D0C 700F09 48110D",
        "2D1010 430F0F 450E11 470E16 4A0E16 4C0D15 4F0C15 500C13 510D14 490F15 461015 431010 41110F 2D1211",
    ];
    private static readonly string[] TexturedCropAt90 =
    [
        "2C343D 32323B 32323A 33333B 34323B 33313B 34323B 34323C 33323C 32323B 32323B",
        "432026 770B06 7F0C04 87100E 8A0E0C 88050A 8A070C 8B0810 860E14 7E0A0E 7A0A06",
        "4A2628 7A0C02 7E0D06 7C0504 7F0604 86080C 84070B 84080F 7D050C 800B0F 7D0B06",
        "4F262C 850D0B 790807 700707 760E0F 700B0F 680C0F 6D0F12 76080E 7E030B 8A070C",
        "4D242A 840C0C 750C0C 6B1010 661310 5F1413 581815 591715 6A0F11 79080F 88080E",
        "4E252C 820A0E 710E10 CAA1A0 D4B5B0 D3BCB5 D2C0B7 D1BFB5 C9A29E 720E10 85070E",
        "51252D 89090E 72080B EDCCC9 FFEAE4 FFEDE4 FFF1E4 FFF2E5 EAD4C8 600F06 7E0D0C",
        "51242C 87060C 770C0F 6C1D1C 67211C 63261D 592B1D 5A2C1D 5B281A 64170C 7F0F0D",
        "4E252B 830B0C 750C0E 700C0E 700D0A 710F0C 6C120A 6E110A 810508 8A0208 910508",
        "4E252B 830B0C 760D0E 700C0F 6F0C09 6E0C09 680E06 6A0E06 820609 8B0309 910507",
        "45262A 740D0B 7A0A14 7F0816 830513 890213 8A020F 8A0310 7C0B15 750D11 710F0A",
    ];

    [Fact]
    public void RealClientIconsAreFound()
    {
        (string[] Crop, StandingColor Color)[] icons = [(NeutralCrop, StandingColor.Grey), (SkullCrop, StandingColor.Red), (TexturedCrop, StandingColor.Red),
            (TexturedCropAt110, StandingColor.Red), (TexturedCropAt90, StandingColor.Red)];
        var frame = Frame(60, 120);
        for (int i = 0; i < icons.Length; i++) Paste(frame, 8, 4 + i * 22, icons[i].Crop);

        Assert.Equal(icons.Select(i => i.Color), StandingDetector.Detect(frame).Icons.Select(i => i.Color));
    }

    // Letters of pilot names that used to pass as neutral icons: flat grey strokes with a bright spot,
    // rendered at 100% UI scale, as hinted text and as a client resampled to the capture scale.
    private static readonly string[][] NeutralLookalikes =
    [
        [
            "4D4D4E ECECEC 4C4C4E 565658 ECECEC",
            "4D4D4E 9F9FA0 BDBDBD BFBFC0 A4A4A5",
            "4D4D4E 949495 989899 8E8E8F 9E9E9F",
            "4D4D4E 949495 222224 202022 9E9E9F",
            "4D4D4E 949495 121214 121214 9E9E9F",
            "4D4D4E 949495 121214 121214 9E9E9F",
        ],
        [
            "121214 888889 888889 FFFFFF 888889 888889 888889 FFFFFF FFFFFF",
            "121214 888889 888889 121214 888889 888889 888889 888889 888889",
            "121214 888889 888889 121214 888889 888889 888889 888889 888889",
            "888889 888889 888889 121214 888889 888889 888889 888889 888889",
            "888889 FFFFFF FFFFFF 888889 888889 888889 888889 888889 888889",
            "888889 FFFFFF FFFFFF 888889 888889 888889 888889 888889 888889",
            "888889 888889 888889 121214 121214 121214 888889 888889 888889",
            "121214 888889 888889 888889 888889 888889 888889 888889 888889",
            "121214 888889 888889 888889 888889 888889 888889 888889 888889",
        ],
        [
            "121214 2D2D2F D4D4D4 E1E1E1 888889 79797A 79797A 79797A 79797A 868687 D4D4D4 E1E1E1",
            "121214 2D2D2F D4D4D4 E1E1E1 888889 7B7B7C 868687 888889 888889 959595 E3E3E3 EEEEEE",
            "121214 222224 868687 888889 2F2F31 2D2D2F 7B7B7C 888889 888889 89898A 959595 89898A",
            "121214 202022 79797A 79797A 202022 202022 79797A 888889 888889 888889 888889 79797A",
            "121214 202022 79797A 79797A 202022 202022 79797A 888889 888889 888889 888889 79797A",
            "121214 202022 79797A 79797A 202022 202022 79797A 888889 888889 888889 888889 79797A",
            "121214 202022 79797A 79797A 202022 202022 79797A 888889 888889 888889 888889 79797A",
            "121214 202022 79797A 79797A 202022 202022 79797A 888889 888889 888889 888889 79797A",
            "121214 202022 79797A 79797A 202022 202022 79797A 888889 888889 888889 888889 79797A",
            "1E1E20 2D2D2F 7B7B7C 7B7B7C 2D2D2F 2D2D2F 7B7B7C 888889 888889 888889 888889 79797A",
            "6C6C6D 7B7B7C 868687 868687 7B7B7C 7B7B7C 868687 888889 888889 888889 888889 79797A",
            "6C6C6D 79797A 79797A 79797A 79797A 79797A 79797A 79797A 79797A 79797A 79797A 6C6C6D",
        ],
        [
            "6A6A6C 6A6A6C C3C3C4 C3C3C4 121214 121214 6A6A6C 6A6A6C C3C3C4 C3C3C4",
            "6A6A6C 6A6A6C C3C3C4 C3C3C4 121214 121214 6A6A6C 6A6A6C C3C3C4 C3C3C4",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 121214 121214 121214 121214 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 121214 121214 121214 121214 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 121214 121214 121214 121214 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 121214 121214 121214 121214 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 121214 121214 121214 121214 6A6A6C 6A6A6C",
            "6A6A6C 6A6A6C 6A6A6C 6A6A6C 121214 121214 121214 121214 6A6A6C 6A6A6C",
        ],
        [
            "3F3F40 474748 515153 4B4B4C 1A1A1C 121214 121214 19191B 454547 4C4C4E 4B4B4D 444446",
            "4A4A4B 555556 6A6A6B 626263 1E1E1F 121214 121214 1C1C1E 5B5B5D 646466 5B5B5D 515152",
            "656566 7D7D7E C7C7C8 BCBCBD 2E2E2F 151517 131315 29292B B3B3B3 C0C0C1 878788 707071",
            "69696A 838384 D7D7D8 CDCDCE 3A3A3C 212123 1D1D1F 343436 C4C4C5 D1D1D1 8F8F8F 757576",
            "69696A 848485 DADADA D7D7D8 737375 616163 58585A 69696B D5D5D5 DADADA 909091 757576",
            "69696A 828283 D0D0D0 D1D1D1 868688 78787A 6F6F71 7C7C7D CFCFCF D1D1D2 8F8F90 757576",
            "69696A 7A7A7B 949495 9E9E9F BFBFBF C3C3C4 BEBEBF B8B8B9 99999A 929293 868687 757576",
            "69696A 787879 89898A 939394 C1C1C2 C9C9C9 C6C6C7 BDBDBE 8D8D8E 848485 848485 757576",
            "69696A 777778 7D7D7E 828283 989899 9D9D9E A6A6A7 A1A1A2 79797B 757576 818182 757576",
            "69696A 767677 7C7C7D 7E7E7F 848485 878788 919192 8E8E8F 747476 727274 818182 757576",
            "69696A 767677 7C7C7D 727273 363637 2D2D2E 2F2F31 38383A 68686A 727274 818182 757576",
            "69696A 767677 7C7C7D 707071 282829 1C1C1D 1E1E1F 28282A 656567 727274 818182 757576",
            "69696A 767677 7C7C7D 6F6F70 202022 131315 131315 1F1F21 646466 727274 818182 757577",
            "69696A 767677 7C7C7D 6F6F70 1F1F21 121214 121214 1D1D1F 646466 727274 818182 757577",
            "69696A 767677 7C7C7D 6F6F70 1F1F21 121214 121214 1D1D1F 646466 727274 818182 757576",
            "5E5E5F 6A6A6B 6E6E6F 636365 1D1D1F 121214 121214 1C1C1E 59595B 666668 737374 69696A",
        ],
    ];

    [Fact]
    public void PilotNameLettersAreNotNeutralIcons()
    {
        foreach (var letter in NeutralLookalikes)
        {
            var frame = Frame(40, 40);
            Paste(frame, 8, 8, letter);
            Assert.Empty(StandingDetector.Detect(frame).Icons);
        }
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
    public void RemindersCanSoundEveryFiveSeconds()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(1, 0), Start, 1);
        var (events, _) = Run(monitor, Seen(1, 0), time, 15.5, reminder: 5);
        Assert.Equal(3, events.Count(e => e == AlertEventKind.Reminder));
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
    public void TurningTheCountOffEndsItsAlarmAndTurningItOnStartsAFreshBaseline()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(0, 1, 2), Start, 2);
        var (events, time2) = Run(monitor, Seen(0, 1, 3), time, 4);
        Assert.Equal([AlertEventKind.UnknownArrived], events);

        // Off: the manager forgets the count and stops reading it.
        monitor.ForgetCount();
        Assert.Equal(0, monitor.Unexplained);
        Assert.Null(monitor.Count);
        (events, time2) = Run(monitor, Seen(0, 1), time2, 11);
        Assert.Equal([AlertEventKind.AllClear], events);

        // On again, reading the same number as before: that is the baseline, and later rises still alarm.
        (events, time2) = Run(monitor, Seen(0, 1, 3), time2, 2);
        Assert.Empty(events);
        Assert.Equal(3, monitor.Count);
        (events, _) = Run(monitor, Seen(0, 1, 4), time2, 4);
        Assert.Equal([AlertEventKind.UnknownArrived], events);
    }

    [Fact]
    public void ARiseWaitingForAnIconIsDroppedWhenTheCountTurnsOff()
    {
        var monitor = new AlertWatchMonitor(Start);
        var (_, time) = Run(monitor, Seen(0, 1, 2), Start, 2);
        (_, time) = Run(monitor, Seen(0, 1, 3), time, 1);
        monitor.ForgetCount();
        var (events, _) = Run(monitor, Seen(0, 1), time, 5);
        Assert.Empty(events);
        Assert.False(monitor.IsActive);
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

            // The count alarm stays on for files saved before it could be turned off.
            Assert.True(loaded.Options.CountAlarm);
            store.Save(folder, document with { Options = document.Options with { CountAlarm = false } });
            Assert.False(store.Load(folder).Options.CountAlarm);
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

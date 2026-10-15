using EveOPreview.Input;
using EveOPreview.Mac;
using EveOPreview.Mac.Native;
using EveOPreview.Services.Implementation;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EveOPreview.Mac.Tests;

public sealed class WindowsProfileImporterTests
{
    private static JObject Profile() => JObject.Parse("""
        {
          "ThumbnailSize": "384, 216",
          "ThumbnailMaximumSize": "960, 540",
          "ThumbnailMinimumSize": "192, 108",
          "LoginThumbnailLocation": "100, -40",
          "FlatLayout": { "EVE - Pilot One": "1500, 800" },
          "PerClientLayout": { "EVE - Pilot One": { "EVE - Pilot Two": "200, 300" } },
          "PerClientThumbnailSizes": { "EVE - Pilot One": "400, 200" },
          "ClientLayout": { "EVE - Pilot One": { "X": 10, "Y": 20, "Width": 1920, "Height": 1080, "IsMaximized": false } },
          "ThumbnailRegions": [ { "Id": "a", "Name": "Left", "X": 0, "Y": 100, "Width": 400, "Height": 800 } ],
          "CycleGroups": [ { "Description": "Main", "ForwardHotkeys": ["Ctrl+F1"] } ]
        }
        """);

    [Fact]
    public void RescaleScalesEveryPersistedGeometry()
    {
        var json = Profile();
        WindowsProfileImporter.Rescale(json, 0.5);
        Assert.Equal("192, 108", json["ThumbnailSize"]!.Value<string>());
        Assert.Equal("480, 270", json["ThumbnailMaximumSize"]!.Value<string>());
        Assert.Equal("50, -20", json["LoginThumbnailLocation"]!.Value<string>());
        Assert.Equal("750, 400", json["FlatLayout"]!["EVE - Pilot One"]!.Value<string>());
        Assert.Equal("100, 150", json["PerClientLayout"]!["EVE - Pilot One"]!["EVE - Pilot Two"]!.Value<string>());
        Assert.Equal("200, 100", json["PerClientThumbnailSizes"]!["EVE - Pilot One"]!.Value<string>());
        Assert.Equal(960, json["ClientLayout"]!["EVE - Pilot One"]!["Width"]!.Value<int>());
        Assert.Equal(50, json["ThumbnailRegions"]![0]!["Y"]!.Value<int>());
    }

    [Fact]
    public void RescaleKeepsHotkeysAndUnitScaleIsIdentity()
    {
        var json = Profile();
        string before = json.ToString();
        WindowsProfileImporter.Rescale(json, 1);
        Assert.Equal(before, json.ToString());
        WindowsProfileImporter.Rescale(json, 0.75);
        Assert.Equal("Ctrl+F1", json["CycleGroups"]![0]!["ForwardHotkeys"]![0]!.Value<string>());
    }

    [Fact]
    public void ImportFindsProfilesAndNeverOverwritesExistingOnes()
    {
        string root = Path.Combine(Path.GetTempPath(), "eveo-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(root, "Windows", "Profiles", "Main");
            string target = Path.Combine(root, "Mac");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(target, "Main"));
            File.WriteAllText(Path.Combine(source, WindowsProfileImporter.ProfileFileName), Profile().ToString());
            var result = WindowsProfileImporter.Import(Path.Combine(root, "Windows"), target, 0.5);
            Assert.Equal(["Main (Windows)"], result.Imported);
            Assert.Empty(result.Skipped);
            var imported = JObject.Parse(File.ReadAllText(Path.Combine(target, "Main (Windows)", WindowsProfileImporter.ProfileFileName)));
            Assert.Equal("192, 108", imported["ThumbnailSize"]!.Value<string>());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed class MacInputTests
{
    [Theory]
    [InlineData((ushort)0x00, 0x41)] // A
    [InlineData((ushort)0x12, 0x31)] // 1
    [InlineData((ushort)0x7A, 0x70)] // F1
    [InlineData((ushort)0x6F, 0x7B)] // F12
    [InlineData((ushort)0x31, 0x20)] // Space
    [InlineData((ushort)0x37, 0xA2)] // Command -> left Ctrl
    [InlineData((ushort)0x3A, 0xA4)] // Option -> left Alt
    [InlineData((ushort)0x3B, 0x5B)] // Control -> left Win
    public void MacKeysMapToPersistedVirtualKeys(ushort macKey, int virtualKey)
    {
        Assert.Equal(virtualKey, MacKeyCodes.ToVirtualKey(macKey));
        Assert.Equal(macKey, MacKeyCodes.ToMac(virtualKey));
    }

    [Fact]
    public void CommandIsTheSharedControlModifier()
    {
        Assert.Equal(ShortcutKeys.Control, MacKeyCodes.Modifiers(0x100008));
        Assert.Equal(ShortcutKeys.Alt | ShortcutKeys.Shift, MacKeyCodes.Modifiers(0x80000 | 0x20000));
        Assert.Equal(ShortcutKeys.None, MacKeyCodes.Modifiers(0x40001)); // Control alone is not a shortcut modifier.
    }

    [Fact]
    public void ModifierTransitionsUseDeviceBits()
    {
        Assert.True(MacKeyCodes.IsModifierDown(0x37, 0x100008));
        Assert.False(MacKeyCodes.IsModifierDown(0x36, 0x100008)); // Right Command not held.
        Assert.True(MacKeyCodes.IsModifierDown(0x3D, 0x80040));
    }
}

public sealed class EveClientDetectionTests
{
    private static MacNative.WindowEntry Window(string title, string executable, string bundle = "", string owner = "") =>
        new(1, 1, title, owner, bundle, executable, 0, 0, 1024, 768, 0, true, false, false);

    [Fact]
    public void DetectsClientsAndExcludesTheLauncher()
    {
        Assert.True(MacProcessMonitor.IsEveClient(Window("EVE - Pilot One", "ExeFile")));
        Assert.True(MacProcessMonitor.IsEveClient(Window("EVE", "EVE", "com.ccpgames.eveonline")));
        Assert.True(MacProcessMonitor.IsEveClient(Window("EVE - Pilot One", "EVE Online", owner: "EVE Online")));
        Assert.False(MacProcessMonitor.IsEveClient(Window("EVE Launcher", "EVE Launcher", "com.ccpgames.evelauncher", "EVE Launcher")));
        Assert.False(MacProcessMonitor.IsEveClient(Window("EVE - notes.txt", "TextEdit", "com.apple.TextEdit", "TextEdit")));
    }
}

public sealed class MirrorStoreTests
{
    [Fact]
    public void MirrorsRoundTripPerProfileWithoutComputedFields()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eveo-mirrors-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EveOPreview.Mac.Mirrors.MirrorStore(Serilog.Core.Logger.None);
            Assert.Empty(store.Load(folder));
            var mirror = new EveOPreview.Mac.Mirrors.MirrorDefinition
            {
                Name = "Local", SourceTitle = "EVE - Pilot One", SourceX = 20, SourceY = 40, SourceWidth = 480, SourceHeight = 160,
                X = 13, Y = 568, Width = 240, Height = 80, Opacity = 0.8, FrameRate = 2,
            };
            store.Save(folder, [mirror]);
            string json = File.ReadAllText(Path.Combine(folder, EveOPreview.Mac.Mirrors.MirrorStore.FileName));
            Assert.DoesNotContain("\"Zoom\"", json);
            Assert.DoesNotContain("\"Character\"", json);
            var loaded = Assert.Single(store.Load(folder));
            Assert.Equal(mirror, loaded);
            Assert.Equal("Pilot One", loaded.Character);
            Assert.Equal(0.5, loaded.Zoom);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void InvalidMirrorsAreIgnored()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eveo-mirrors-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, EveOPreview.Mac.Mirrors.MirrorStore.FileName),
                """[{"SourceTitle":"","SourceWidth":100,"SourceHeight":100},{"SourceTitle":"EVE - A","SourceWidth":2,"SourceHeight":100}]""");
            Assert.Empty(new EveOPreview.Mac.Mirrors.MirrorStore(Serilog.Core.Logger.None).Load(folder));
            File.WriteAllText(Path.Combine(folder, EveOPreview.Mac.Mirrors.MirrorStore.FileName), "not json");
            Assert.Empty(new EveOPreview.Mac.Mirrors.MirrorStore(Serilog.Core.Logger.None).Load(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

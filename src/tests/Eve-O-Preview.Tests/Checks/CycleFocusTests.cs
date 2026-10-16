using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EveOPreview.Configuration.Implementation;
using EveOPreview.Services;
using EveOPreview.Services.Implementation;
using EveOPreview.Services.Interface;
using EveOPreview.Tests.Infrastructure;
using EveOPreview.View;
using MediatR;
using Serilog;
using Xunit;
using Keys = EveOPreview.Input.ShortcutKeys;

namespace EveOPreview.Tests.Checks;

// Platform-neutral: also linked into the macOS tests.
public class CycleFocusTests
{
    [Fact]
    public void ShortcutThatDoesNotApplyLeavesPressAndReleaseToTheFocusedApplication()
    {
        int cycles = 0;
        bool applies = false;
        var matcher = new WindowsHotkeyMatcher(action => action());
        matcher.Configure(new() { [Keys.Control | Keys.F8] = new("Control+F8", () => cycles++, When: () => applies) }, _ => false);
        matcher.Process(0xA2, true);
        Assert.False(matcher.Process(0x77, true));
        Assert.False(matcher.Process(0x77, false));
        applies = true;
        Assert.True(matcher.Process(0x77, true));
        Assert.True(matcher.Process(0x77, false));
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void CycleShortcutsFollowTheSettingAndTheFocusedWindow()
    {
        var configuration = new ThumbnailConfiguration
        {
            CycleLoginClientsHotkey = "Control+F9",
            ToggleHideActiveClientsHotkey = "Control+F10",
        };
        configuration.CycleGroups.Add(new CycleGroup { ForwardHotkeys = ["Control+F8"], BackwardHotkeys = ["Control+Shift+F8"] });
        IReadOnlyList<HotkeyBinding> bindings = null;
        var hotkeys = Stub.Create<IHotkeyService>((method, args) =>
        {
            if (method.Name == nameof(IHotkeyService.Replace)) bindings = (IReadOnlyList<HotkeyBinding>)args[0];
            return Stub.Default(method.ReturnType);
        });
        IntPtr eve = 1, browser = 2;
        var client = Stub.Create<IProcessInfo>((method, _) => method.Name switch
        {
            "get_ProcessName" => "ExeFile",
            "get_IsEveClient" => true,
            _ => Stub.Default(method.ReturnType),
        });
        var processes = Stub.Create<IProcessMonitor>((method, args) =>
            method.Name == nameof(IProcessMonitor.LookupCachedProcessByWindowHandle) && (IntPtr)args[0] == eve ? client : Stub.Default(method.ReturnType));
        var manager = new ThumbnailManager(Stub.Create<IMediator>(), configuration, processes, Stub.Create<IWindowManager>(),
            Stub.Create<IThumbnailViewFactory>(), hotkeys, Stub.Create<IHookService>(), Stub.Create<IGlobalEvents>(), Stub.Create<ILogger>());
        var focus = typeof(ThumbnailManager).GetMethod("ForegroundWindowChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        manager.RegisterAllHotkeys();
        var cycles = bindings.Where(b => b.Shortcut is "Control+F8" or "Control+Shift+F8" or "Control+F9").ToArray();

        Assert.Equal(3, cycles.Length);
        focus.Invoke(manager, [browser]);
        Assert.All(cycles, b => Assert.True(b.When())); // Off by default: cycling works everywhere.

        configuration.CycleHotkeysRequireEveFocus = true; // Applies without re-registering.
        Assert.All(cycles, b => Assert.False(b.When()));
        focus.Invoke(manager, [eve]);
        Assert.All(cycles, b => Assert.True(b.When()));
        focus.Invoke(manager, [browser]);
        Assert.All(cycles, b => Assert.False(b.When()));
        Assert.Null(bindings.Single(b => b.Shortcut == "Control+F10").When);
    }

    [Fact]
    public void FocusRequirementIsOffByDefaultAndRoundTrips()
    {
        var configuration = new ThumbnailConfiguration();
        Assert.False(configuration.CycleHotkeysRequireEveFocus);
        configuration.CycleHotkeysRequireEveFocus = true;
        var reloaded = Newtonsoft.Json.JsonConvert.DeserializeObject<ThumbnailConfiguration>(Newtonsoft.Json.JsonConvert.SerializeObject(configuration));
        Assert.True(reloaded.CycleHotkeysRequireEveFocus);
    }
}

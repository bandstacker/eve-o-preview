using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using EveOPreview.Configuration;
using EveOPreview.Configuration.Implementation;
using EveOPreview.Mediator.Handlers.Thumbnails;
using EveOPreview.Mediator.Messages;
using EveOPreview.Tests.Infrastructure;
using EveOPreview.UI;
using EveOPreview.View;
using MediatR;
using Xunit;

namespace EveOPreview.Tests.Checks;

public class UsePreviewForAllTests
{
    private sealed class Harness
    {
        public ThumbnailConfiguration Configuration { get; } = new();
        public Color? ViewColor { get; private set; }
        public List<object> Messages { get; } = [];
        public CombatLogSettings Logs { get; set; } = new();
        public CombatLogSettings Saved { get; private set; }
        public IMainFormView View { get; }
        public IMediator Mediator { get; }
        public IWorkspaceCombatLogs CombatLogs { get; }

        public Harness()
        {
            Configuration.PerClientActiveClientHighlightColor.Clear(); // Drop the example entries of a new profile.
            View = Stub.Create<IMainFormView>((method, args) =>
            {
                if (method.Name == "set_ActiveClientHighlightColor") ViewColor = (Color)args[0];
                return Stub.Default(method.ReturnType);
            });
            Mediator = Stub.Create<IMediator>((method, args) => { Messages.Add(args[0]); return Stub.Default(method.ReturnType); });
            CombatLogs = Stub.Create<IWorkspaceCombatLogs>((method, args) =>
            {
                if (method.Name == nameof(IWorkspaceCombatLogs.ReadLogSettings)) return Logs;
                if (method.Name == nameof(IWorkspaceCombatLogs.SaveLogSettingsAsync))
                {
                    Saved = (CombatLogSettings)args[0];
                    return Task.FromResult(CommandResult.Ok());
                }
                return Stub.Default(method.ReturnType);
            });
        }

        public Task Run(string title, bool withLogs = true) =>
            new UsePreviewLookForAllHandler(Configuration, View, Mediator, withLogs ? CombatLogs : null).Handle(new UsePreviewLookForAll(title), default);
    }

    [Fact]
    public async Task LookOfOneCharacterBecomesTheSharedDefault()
    {
        var harness = new Harness();
        harness.Configuration.ActiveClientHighlightColor = Color.Orange;
        harness.Configuration.PerClientActiveClientHighlightColor["EVE - Maya Aldard"] = Color.Red;
        harness.Configuration.PerClientActiveClientHighlightColor["EVE - Other"] = Color.Blue;
        var maya = new LogOverlayOptions(Breakdown: true, FontSize: 18);
        harness.Logs = new CombatLogSettings
        {
            DefaultOverlay = new LogOverlayOptions(),
            Overlays = new Dictionary<string, LogOverlayOptions> { ["EVE - Maya Aldard"] = maya, ["EVE - Other"] = new(FontSize: 9) },
        };

        await harness.Run("EVE - Maya Aldard");

        Assert.Equal(Color.Red, harness.Configuration.ActiveClientHighlightColor);
        Assert.Equal(Color.Red, harness.ViewColor);
        Assert.Empty(harness.Configuration.PerClientActiveClientHighlightColor);
        Assert.Contains(harness.Messages, message => message is SaveConfiguration);
        Assert.Equal(maya, harness.Saved.DefaultOverlay);
        Assert.Empty(harness.Saved.Overlays);
    }

    [Fact]
    public async Task CharacterWithoutOverridesResetsEveryoneToTheCurrentDefaults()
    {
        var harness = new Harness();
        harness.Configuration.ActiveClientHighlightColor = Color.Orange;
        harness.Configuration.PerClientActiveClientHighlightColor["EVE - Other"] = Color.Blue;
        var shared = new LogOverlayOptions(FontSize: 15);
        harness.Logs = new CombatLogSettings
        {
            DefaultOverlay = shared,
            Overlays = new Dictionary<string, LogOverlayOptions> { ["EVE - Other"] = new(FontSize: 9) },
        };

        await harness.Run("EVE - Maya Aldard");

        Assert.Equal(Color.Orange, harness.Configuration.ActiveClientHighlightColor);
        Assert.Null(harness.ViewColor);
        Assert.Empty(harness.Configuration.PerClientActiveClientHighlightColor);
        Assert.Equal(shared, harness.Saved.DefaultOverlay);
        Assert.Empty(harness.Saved.Overlays);
    }

    [Fact]
    public async Task WorksWithoutCombatLogsAndIgnoresEmptyTitles()
    {
        var harness = new Harness();
        harness.Configuration.PerClientActiveClientHighlightColor["EVE - Other"] = Color.Blue;
        await harness.Run("");
        Assert.Single(harness.Configuration.PerClientActiveClientHighlightColor);
        await harness.Run("EVE - Maya Aldard", withLogs: false);
        Assert.Empty(harness.Configuration.PerClientActiveClientHighlightColor);
        Assert.Null(harness.Saved);
    }
}

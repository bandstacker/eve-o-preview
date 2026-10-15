using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using EveOPreview.Configuration;
using EveOPreview.Configuration.Implementation;
using EveOPreview.Configuration.Interface;
using EveOPreview.Input;
using EveOPreview.Mac;
using EveOPreview.Mac.Native;
using EveOPreview.Presenters;
using EveOPreview.Services;
using EveOPreview.Services.Implementation;
using EveOPreview.Services.Interface;
using EveOPreview.UI;
using EveOPreview.UI.Previews;
using EveOPreview.View;
using MediatR;
using MediatR.Extensions.Autofac.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace EveOPreview;

/// <summary>macOS entry point. Mirrors upstream Program.CreateApplicationContainerBuilder with macOS services.</summary>
static class MacProgram
{
    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(MacSettings.RootDirectory);
        SetupLogger(args);
        using var logLifetime = Log.Logger as IDisposable;
        var handler = new ExceptionHandler();
        handler.SetupClrExceptionHandler();
        Log.Information("Starting EVE-O Preview for macOS {Version}", typeof(MacProgram).Assembly.GetName().Version);

        using var instance = AcquireInstanceLock();
        if (instance == null)
        {
            Log.Warning("Another instance of EVE-O Preview is already running. Exiting.");
            return 0;
        }

        unsafe { MacNative.SetLogCallback(&NativeLog); }
        ConfigurePlatform();

        AppBuilder.Configure<WorkspaceApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .SetupWithClassicDesktopLifetime(args);
        handler.SetupDispatcherExceptionHandler();
        var lifetime = (ClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        lifetime.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
        MacNative.SetAccessoryPolicy(1);

        using var pointer = new MacGlobalPointerInput(Log.Logger);
        using var container = CreateContainerBuilder(Log.Logger, pointer, lifetime).Build();
        var preferences = container.Resolve<ApplicationPreferences>();
        // DirectComposition is Windows-only; macOS always uses the portable overlay renderer.
        if (preferences.PreviewOverlayRenderer != "Legacy") preferences.SetPreviewOverlayRenderer("Legacy");
        var macSettings = container.Resolve<MacSettings>();
        MacLiveThumbnail.Configure(macSettings.PreviewFrameRate, macSettings.TitleBarCropPoints);
        macSettings.Changed += () => MacLiveThumbnail.Configure(macSettings.PreviewFrameRate, macSettings.TitleBarCropPoints);
        container.Resolve<MainFormPresenter>();
        var workspace = container.Resolve<WorkspaceWindow>();
        container.Resolve<EveOPreview.Mac.Mirrors.MirrorManager>().Start();
        lifetime.MainWindow = workspace;
        workspace.Opened += (_, _) => RequestMissingPermissions(workspace);
        // Opening the app again (Finder, Spotlight, Launchpad) shows settings, as the menu-bar item
        // can be hidden behind the camera housing when the menu bar is crowded.
        if (Application.Current.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) workspace.RestoreWorkspace(); };
        if (args.Contains("--debug-harness"))
            _ = new DebugHarness(container.Resolve<IThumbnailManager>(), container.Resolve<IProcessMonitor>(), workspace,
                container.Resolve<EveOPreview.Mac.Mirrors.MirrorManager>(), Log.Logger);
        int exit = lifetime.Start(args);
        MacNative.AudioUnmuteAll();
        return exit;
    }

    /// <summary>Platform differences for the shared settings catalog and preview geometry.</summary>
    private static void ConfigurePlatform()
    {
        PreviewPixels.AreDips = true;
        string[] unsupported =
        [
            "HotkeyInputMethod", "EnableCompatibilityMode", "PreviewOverlayRenderer",
            "FpsEnabled", "FpsFocused", "FpsBackground", "FpsPredictingFocus",
            "AudioMuteJumpGateTunnel", "AudioMuteLocationBanner", "AudioCustomMutedEventIds",
        ];
        SettingCatalog.ConfigurePlatform(s => !unsupported.Contains(s.Key),
        [
            new("MinimizeToTray", "Close to the menu bar", "Keep EVE-O Preview running in the menu bar when the settings window closes.", "General", SettingKind.Toggle, Aliases: "tray system tray menu bar status close hide"),
            new("MinimizeInactiveClients", "Hide inactive clients", "Hide the previous character's client when switching (macOS Hide).", "General", SettingKind.Toggle, Aliases: "minimize inactive hide"),
            new("EnableAutomaticCpuAffinity", "Efficiency cores for background clients", "Run background EVE clients on efficiency cores at low priority. The active and next clients and login screens are never limited.", "FpsAudio", SettingKind.Toggle, Aliases: "cpu affinity performance cores efficiency cores background priority battery"),
            new("PreviewFrameRate", "Live preview frame rate", "Frames per second for each live preview. Lower values use less battery; this does not change the game's frame rate.", "FpsAudio", SettingKind.Number, 1, 60, Aliases: "fps preview frame rate capture refresh"),
            new("AudioMuteBackgroundClients", "Mute background clients", "Only the active EVE client plays sound. macOS asks for System Audio Recording access the first time.", "FpsAudio", SettingKind.Toggle, Aliases: "audio sound mute silence background inactive"),
            new("ThumbnailMinimumWidth", "Minimum preview width (pt)", "Smallest preview width in points.", "AdvancedPreview", SettingKind.Number, 64, 3840, Aliases: "size limit"),
            new("ThumbnailMinimumHeight", "Minimum preview height (pt)", "Smallest preview height in points.", "AdvancedPreview", SettingKind.Number, 64, 2160, Aliases: "size limit"),
            new("ThumbnailMaximumWidth", "Maximum preview width (pt)", "Largest preview width in points.", "AdvancedPreview", SettingKind.Number, 64, 3840, Aliases: "size limit"),
            new("ThumbnailMaximumHeight", "Maximum preview height (pt)", "Largest preview height in points.", "AdvancedPreview", SettingKind.Number, 64, 2160, Aliases: "size limit"),
            new("MacTitleBarCrop", "Title bar crop (points)", "Crop the top of each client window in live previews.", "MacOS", SettingKind.Number, 0, 64, Aliases: "title bar crop"),
        ]);
    }

    private static void RequestMissingPermissions(WorkspaceWindow workspace)
    {
        bool screen = MacNative.PermissionGranted(0) != 0, accessibility = MacNative.PermissionGranted(1) != 0;
        Log.Information("Permissions: Screen Recording={Screen}, Accessibility={Accessibility}", screen, accessibility);
        if (!screen) MacNative.RequestPermission(0);
        if (!accessibility) MacNative.RequestPermission(1);
        if (!screen || !accessibility) workspace.RestoreWorkspace();
    }

    private static FileStream AcquireInstanceLock()
    {
        try
        {
            return new FileStream(Path.Combine(MacSettings.RootDirectory, ".instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
    }

    private static void SetupLogger(string[] args)
    {
        bool verbose = args.Contains("--verbose") || args.Contains("-v");
        var level = verbose ? LogEventLevel.Verbose : LogEventLevel.Information;
        Directory.CreateDirectory(MacSettings.LogDirectory);
        var file = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.File(Path.Combine(MacSettings.LogDirectory, "EVE-O Preview Log-.txt"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true, restrictedToMinimumLevel: level,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Properties:j} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Is(level).Enrich.FromLogContext()
            .WriteTo.Sink(new EveOPreview.Helper.AsyncLogSink(file)).CreateLogger();
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static unsafe void NativeLog(int level, byte* message)
    {
        string text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)message) ?? "";
        switch (level)
        {
            case 0: Log.Verbose("[native] {Message}", text); break;
            case 1: Log.Information("[native] {Message}", text); break;
            case 2: Log.Warning("[native] {Message}", text); break;
            default: Log.Error("[native] {Message}", text); break;
        }
    }

    internal static ContainerBuilder CreateContainerBuilder(ILogger logger, IGlobalPointerInput inputEvents, IClassicDesktopStyleApplicationLifetime context)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(logger).AsImplementedInterfaces().SingleInstance();
        builder.RegisterInstance(inputEvents).AsImplementedInterfaces().SingleInstance();

        // macOS low-level services
        builder.RegisterType<MacSettings>().AsSelf().SingleInstance()
            .OnActivated(e => WindowsWorkspaceBackend.MacSettings = e.Instance);
        builder.RegisterType<MacHotkeyService>().As<IHotkeyService>().SingleInstance();
        builder.RegisterType<MacWindowManager>().As<IWindowManager>().SingleInstance();
        builder.RegisterType<MacClientPolicyService>().As<IHookService>().As<ICpuAffinityService>().SingleInstance();
        builder.RegisterType<MacProcessMonitor>().AsSelf().As<IProcessMonitor>().SingleInstance();
        builder.RegisterType<MacEveClientUserIdReader>().AsSelf().SingleInstance();
        builder.RegisterType<MacModule>().AsSelf().SingleInstance();
        builder.RegisterType<EveOPreview.Mac.Mirrors.MirrorStore>().AsSelf().SingleInstance();
        builder.RegisterType<EveOPreview.Mac.Mirrors.MirrorManager>().AsSelf().SingleInstance();
        builder.RegisterType<EveOPreview.Mac.Mirrors.MirrorModule>().AsSelf().SingleInstance();

        MediatR.Mediator.LicenseKey = "Community";
        builder.Register(ctx => new AutofacServiceProvider(ctx.Resolve<ILifetimeScope>())).As<IServiceProvider>().InstancePerLifetimeScope();
        builder.RegisterMediatR(MediatR.Extensions.Autofac.DependencyInjection.Builder.MediatRConfigurationBuilder
            .Create("Community", typeof(MacProgram).Assembly).WithAllOpenGenericHandlerTypesRegistered().Build());

        // Configuration services (upstream implementations, macOS storage root)
        builder.Register(ctx => new ProfileManager(logger, ctx.Resolve<IMediator>(), Path.Combine(MacSettings.RootDirectory, "Profiles")))
            .As<IProfileManager>().SingleInstance();
        builder.RegisterType<ConfigurationStorage>().As<IConfigurationStorage>().SingleInstance();
        builder.RegisterType<AppConfig>().As<IAppConfig>().SingleInstance();
        builder.RegisterType<ThumbnailConfiguration>().As<IThumbnailConfiguration>().SingleInstance();
        builder.RegisterType<GlobalEvents>().As<IGlobalEvents>().SingleInstance();
        builder.RegisterType<ApplicationPreferences>().AsSelf().SingleInstance();
        builder.RegisterType<CharacterPortraitCache>().AsSelf().As<IWorkspacePortraitProvider>().SingleInstance();
        builder.Register(ctx => new CharacterIdentityCache(
                Path.Combine(Path.GetDirectoryName(ctx.Resolve<ApplicationPreferences>().FilePath)!, "Cache", "Characters.json"),
                new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, ctx.Resolve<MacEveClientUserIdReader>().Read,
                logger, () => DateTimeOffset.UtcNow))
            .AsSelf().As<IWorkspaceCharacterProvider>().SingleInstance();
        builder.RegisterType<EveOPreview.Services.Logs.CharacterSystemCache>().SingleInstance();
        builder.RegisterType<EveOPreview.Services.StaticData.StaticDataService>().AsSelf().As<IWorkspaceStaticData>().SingleInstance();
        builder.RegisterType<EveOPreview.Services.Logs.CombatLogService>().AsSelf().As<IWorkspaceCombatLogs>().SingleInstance();
        builder.RegisterType<MacWorkspacePreviewCapture>().As<IWorkspacePreviewCapture>().SingleInstance();

        // Application services and views (upstream)
        builder.RegisterType<ThumbnailManager>().As<IThumbnailManager>().SingleInstance();
        builder.RegisterType<ThumbnailViewFactory>().As<IThumbnailViewFactory>().SingleInstance();
        builder.RegisterType<ThumbnailDescription>().As<IThumbnailDescription>().SingleInstance();
        builder.RegisterType<StaticThumbnailView>().AsSelf().InstancePerDependency();
        builder.RegisterType<LiveThumbnailView>().AsSelf().InstancePerDependency();
        builder.RegisterType<WorkspaceWindow>().AsSelf().As<IMainFormView>().SingleInstance();

        builder.RegisterInstance(context).As<IClassicDesktopStyleApplicationLifetime>().SingleInstance();
        builder.RegisterType<MainFormPresenter>().AsSelf().SingleInstance();
        builder.RegisterType<ApplicationController>().As<IApplicationController>().SingleInstance();
        return builder;
    }
}

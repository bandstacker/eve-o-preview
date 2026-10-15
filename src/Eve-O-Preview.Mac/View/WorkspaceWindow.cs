using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using EveOPreview.Configuration;
using EveOPreview.Configuration.Implementation;
using EveOPreview.Configuration.Interface;
using EveOPreview.Configuration.Model;
using EveOPreview.Mac.Native;
using EveOPreview.Mediator.Messages;
using EveOPreview.Services.Implementation;
using EveOPreview.UI;
using MediatR;
using Serilog;
using Size = System.Drawing.Size;

namespace EveOPreview.View;

/// <summary>
/// macOS settings window and menu-bar owner. Mirrors the upstream Windows WorkspaceWindow (same shared
/// WorkspaceView and backend); Windows session, DWM title-bar and window-message handling do not apply.
/// </summary>
public sealed class WorkspaceWindow : Window, IMainFormView, IAsyncSettingsView, IDisposable
{
    private readonly TrayIcon _tray;
    private readonly WorkspaceView _workspace;
    private readonly ApplicationPreferences _preferences;
    private readonly MacProcessMonitor _processes;
    private Action _exitApplication;
    private bool _refreshPending, _started, _disposed;
    private bool? _legacyLayout;
    private Avalonia.Size _modernClientSize = new(1180, 800);
    public WindowsWorkspaceBackend Backend { get; }
    public WorkspaceView Workspace => _workspace;
    internal bool IsDisposed => _disposed;

    public WorkspaceWindow(IClassicDesktopStyleApplicationLifetime lifetime, ILogger logger, IMediator mediator,
        IConfigurationStorage storage, IThumbnailConfiguration configuration, IProfileManager profiles,
        ApplicationPreferences preferences, IWorkspacePortraitProvider portraits, Services.IProcessMonitor processes,
        Mac.MacModule macModule, Mac.Mirrors.MirrorModule mirrorModule, IWorkspacePreviewCapture previewCapture = null,
        CharacterIdentityCache characters = null, Services.IThumbnailManager thumbnails = null,
        IWorkspaceCombatLogs combatLogs = null, IWorkspaceStaticData staticData = null)
    {
        _preferences = preferences;
        _processes = processes as MacProcessMonitor;
        Title = "EVE-O Preview";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = LoadIcon();
        Backend = new WindowsWorkspaceBackend(this, this, mediator, storage, configuration, profiles, preferences, logger, portraits, previewCapture, characters, thumbnails, combatLogs, staticData);
        var modules = new List<WorkspaceModule>();
        if (combatLogs is not null) modules.Add(CombatLogView.CreateModule());
        modules.Add(RegionsView.CreateModule());
        modules.Add(mirrorModule.Create());
        modules.Add(macModule.Create());
        _workspace = new WorkspaceView(Backend, modules);
        Content = _workspace;
        var menu = new NativeMenu();
        AddMenu(menu, "Open EVE-O Preview", RestoreWorkspace);
        AddMenu(menu, "Hide / show previews", () => ToggleHideAllActiveClients?.Invoke());
        AddMenu(menu, "Hide all clients", () => MinimizeAllClients?.Invoke());
        AddMenu(menu, "New UI mirror...", () => { RestoreWorkspace(); _workspace.Navigate(Mac.Mirrors.MirrorModule.Id); });
        menu.Items.Add(new NativeMenuItemSeparator());
        AddMenu(menu, "Quit EVE-O Preview", () => ApplicationExitRequested?.Invoke());
        _tray = new TrayIcon { Icon = Icon, ToolTipText = "EVE-O Preview", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => RestoreWorkspace();
        _preferences.Changed += ApplyNativeTheme;
        ApplyNativeTheme();
        Opened += (_, _) =>
        {
            RegisterMainWindow();
            if (!_started) { _started = true; FormActivated?.Invoke(); }
            RequestRefresh();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible || e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
                Backend.StopRegionEditing();
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized) FormMinimized?.Invoke();
        };
        Closing += (_, e) =>
        {
            if (_disposed) return;
            if (Backend.IsBusy) { e.Cancel = true; return; }
            if (!MinimizeToTray && _workspace.HasUnappliedEdits)
            {
                e.Cancel = true;
                _workspace.RequestDiscardDrafts(Close);
                return;
            }
            var request = new ViewCloseRequest();
            FormCloseRequested?.Invoke(request);
            e.Cancel = !request.Allow;
        };
        Closed += (_, _) => Dispose();
    }

    private void RegisterMainWindow()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero) _processes?.SetMainWindow(MacNative.WindowFromHandle(handle), Title);
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = typeof(WorkspaceWindow).Assembly.GetManifestResourceStream("EveOPreview.Mac.icon.ico");
        return stream is null ? null : new WindowIcon(stream);
    }

    private static void AddMenu(NativeMenu menu, string title, Action action)
    {
        var item = new NativeMenuItem(title);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public void RestoreWorkspace()
    {
        Show();
        WindowState = WindowState.Normal;
        MacNative.ActivateSelf();
        Activate();
    }

    private void ApplyNativeTheme()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(ApplyNativeTheme); return; }
        if (_disposed) return;
        EveOPreview.View.CustomControl.NativeMenuTheme.CurrentTheme = _preferences.Theme;
        EveOPreview.View.CustomControl.NativeMenuTheme.ThumbnailMenuOrder = _preferences.ThumbnailMenuOrder;
        EveOPreview.View.CustomControl.NativeMenuTheme.ThumbnailTheme = _preferences.ThumbnailMenuTheme;
        RequestedThemeVariant = _preferences.Theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        if (Avalonia.Application.Current is { } application) application.RequestedThemeVariant = RequestedThemeVariant;
        bool legacy = _preferences.Theme == "Legacy";
        if (_legacyLayout == legacy) return;
        if (_legacyLayout == false && WindowState == WindowState.Normal) _modernClientSize = ClientSize;
        WindowState = WindowState.Normal;
        _legacyLayout = legacy;
        MinWidth = legacy ? 460 : 800;
        MinHeight = legacy ? 417 : 620;
        CanResize = !legacy;
        CanMaximize = !legacy;
        CanMinimize = !legacy;
        var size = legacy ? new Avalonia.Size(460, 417) : _modernClientSize;
        Width = size.Width; Height = size.Height;
    }

    private void RequestApplicationExit()
    {
        if (Backend.IsBusy) return;
        if (_workspace.HasUnappliedEdits)
        {
            RestoreWorkspace();
            _workspace.RequestDiscardDrafts(() => _exitApplication?.Invoke());
        }
        else _exitApplication?.Invoke();
    }

    private void RequestRefresh()
    {
        if (_disposed || _refreshPending) return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (!_disposed) Backend?.NotifyChanged();
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray?.Dispose();
        _preferences.Changed -= ApplyNativeTheme;
        (_workspace as IDisposable)?.Dispose();
        Backend?.Dispose();
        Close();
    }

    public void Minimize() => Hide();
    public void SetDocumentationUrl(string url) => Backend.DocumentationUrl = url;
    public void SetVersionInfo(string version) { Backend.Version = version; RequestRefresh(); }
    public void SetThumbnailSizeLimitations(Size minimumSize, Size maximumSize)
    {
        Backend.MinimumThumbnailSize = minimumSize;
        Backend.MaximumThumbnailSize = maximumSize;
    }
    public void AddThumbnails(IList<IThumbnailDescription> thumbnails) { Backend.AddClients(thumbnails); RequestRefresh(); }
    public void RemoveThumbnails(IList<IThumbnailDescription> thumbnails) { Backend.RemoveClients(thumbnails); RequestRefresh(); }
    public void RefreshZoomSettings() => RequestRefresh();
    public void UpdateThumbnailToggleHideAllStatus(bool notificationIsHidden) => RequestRefresh();
    public void UpdateProfileList(List<ProfileLocation> profiles) => RequestRefresh();

    public bool MinimizeToTray { get; set { field = value; RequestRefresh(); } } = true;
    public double ThumbnailOpacity { get; set { field = value; RequestRefresh(); } } = 0.5;
    public bool EnableClientLayoutTracking { get; set { field = value; RequestRefresh(); } } = false;
    public bool HideActiveClientThumbnail { get; set { field = value; RequestRefresh(); } } = false;
    public bool MinimizeInactiveClients { get; set { field = value; RequestRefresh(); } } = false;
    public bool ShowThumbnailsAlwaysOnTop { get; set { field = value; RequestRefresh(); } } = true;
    public bool HideThumbnailsOnLostFocus { get; set { field = value; RequestRefresh(); } } = false;
    public bool EnablePerClientThumbnailLayouts { get; set { field = value; RequestRefresh(); } } = false;
    public Size ThumbnailSize { get; set { field = value; RequestRefresh(); } } = new(384, 216);
    public bool EnableThumbnailZoom { get; set { field = value; RequestRefresh(); } } = false;
    public int ThumbnailZoomFactor { get; set { field = value; RequestRefresh(); } } = 2;
    public ViewZoomAnchor ThumbnailZoomAnchor { get; set { field = value; RequestRefresh(); } } = ViewZoomAnchor.NW;
    public bool ShowThumbnailOverlays { get; set { field = value; RequestRefresh(); } } = true;
    public bool ShowThumbnailFrames { get; set { field = value; RequestRefresh(); } } = false;
    public bool EnableActiveClientHighlight { get; set { field = value; RequestRefresh(); } } = false;
    public Color ActiveClientHighlightColor { get; set { field = value; RequestRefresh(); } } = Color.Orange;
    public FontSettings TitleFontSettings { get; set { field = value; RequestRefresh(); } } = new();
    public FpsLimiterSettings FpsLimiterSettings { get; set { field = value; RequestRefresh(); } } = new();
    public AudioMuteSettings AudioMuteSettings { get; set { field = value; RequestRefresh(); } } = new();
    public string ToggleHideAllActiveHotkey { get; set { field = value; RequestRefresh(); } } = "";
    public string MinimizeAllClientsHotkey { get; set { field = value; RequestRefresh(); } } = "";
    public string LoadedProfileName { get; set { field = value; RequestRefresh(); } } = "Default";
    public List<CycleGroup> CycleGroups { get; set { field = value; RequestRefresh(); } } = [];
    public bool EnableAutomaticCpuAffinity { get; set { field = value; RequestRefresh(); } } = true;

    public Action ApplicationExitRequested { get => RequestApplicationExit; set => _exitApplication = value; }
    public Action WindowsSessionEnding { get; set; }
    public Action FormActivated { get; set; }
    public Action FormMinimized { get; set; }
    public Action<ViewCloseRequest> FormCloseRequested { get; set; }
    public Action ApplicationSettingsChanged { get; set; }
    public Action ThumbnailsSizeChanged { get; set; }
    public Action<string> ThumbnailStateChanged { get; set; }
    public Action DocumentationLinkActivated { get; set; }
    public Func<string> GetClientNameFromInput { get; set; }
    public Func<string, CaptureNewHotkeyResponse> CaptureNewHotkey { get; set; }
    public Action FpsLimiterChanged { get; set; }
    public Action FpsLimiterEnabledChanged { get; set; }
    public Action AudioSettingsChanged { get; set; }
    public Action ToggleHideAllActiveClients { get; set; }
    public Action MinimizeAllClients { get; set; }
    public Action CloneCurrentProfile { get; set; }
    public Action DeleteCurrentProfile { get; set; }
    public Action<string> RenameCurrentProfile { get; set; }
    public Action<ProfileLocation> SwitchToProfile { get; set; }
    public Func<Task> CommitSettingsAsync { get; set; }
    public Func<Task> CommitSizeAsync { get; set; }
}

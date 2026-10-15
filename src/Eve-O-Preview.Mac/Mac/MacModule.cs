using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EveOPreview.Configuration.Interface;
using EveOPreview.Mac.Native;
using EveOPreview.Services.Implementation;
using EveOPreview.UI;

namespace EveOPreview.Mac;

/// <summary>
/// The "macOS" workspace module: privacy permissions, Windows profile import and macOS capture options.
/// Registered through the upstream module mechanism, so no shared page changes are needed.
/// </summary>
public sealed class MacModule(IProfileManager profiles, MacSettings settings)
{
    public WorkspaceModule Create() => new("MacOS", "macOS", "Permissions, Windows profile import and macOS options.",
        backend => new MacModuleView(backend, profiles, settings))
    {
        SearchTargets = [new("macos", "macOS", ["mac", "macos", "permission", "permissions", "screen recording", "accessibility",
            "audio recording", "privacy", "import", "windows profile", "migrate", "title bar", "crop"], () => { })]
    };
}

internal sealed class MacModuleView : UserControl, IWorkspaceLiveModule
{
    private readonly IWorkspaceBackend _backend;
    private readonly IProfileManager _profiles;
    private readonly MacSettings _settings;
    private readonly WorkspaceTheme _theme;
    private readonly StackPanel _permissions = new() { Spacing = 8 };
    private readonly TextBlock _importStatus;
    private readonly TextBox _windowsWidth = new() { Text = "1920", Width = 90 };
    private readonly TextBox _windowsHeight = new() { Text = "1080", Width = 90 };
    private readonly NumericUpDown _crop;

    public MacModuleView(IWorkspaceBackend backend, IProfileManager profiles, MacSettings settings)
    {
        _backend = backend; _profiles = profiles; _settings = settings;
        _theme = WorkspaceTheme.Get(backend.Read().Theme);
        _importStatus = Muted("");
        _crop = new NumericUpDown { Minimum = 0, Maximum = 64, Increment = 1, Value = settings.TitleBarCropPoints, Width = 140, FormatString = "0" };
        var root = new StackPanel { Spacing = 16 };

        root.Children.Add(Card("Permissions",
            Muted("macOS asks once for each permission. If a request was dismissed, open System Settings and enable EVE-O Preview."),
            _permissions,
            Button("Check again", RefreshPermissions)));

        root.Children.Add(Card("Live previews",
            Muted("Crop the top of each client window, for example a title bar in windowed mode."),
            Row(Label("Title bar crop (points)"), _crop, Button("Apply", () => Apply("MacTitleBarCrop", ((int)(_crop.Value ?? 0)).ToString(CultureInfo.InvariantCulture))))));

        root.Children.Add(Card("Import Windows profiles",
            Muted("Choose a Windows EVE-O Preview folder, its Profiles folder or one profile folder. Profiles keep their hotkeys (Ctrl becomes Command). Positions and sizes are scaled from the Windows display below to this Mac's main display."),
            Row(Label("Windows display"), _windowsWidth, Label("x"), _windowsHeight, Label("pixels")),
            Row(Button("Choose folder...", ImportAsync)),
            _importStatus));

        root.Children.Add(Card("Not available on macOS",
            Muted("Frame-rate limits and sound-specific muting need code inside the EVE client, which macOS does not allow. " +
                  "Use Mute background clients and Efficiency cores for background clients on the Performance & audio page instead.")));

        Content = root;
        RefreshPermissions();
    }

    public void RefreshWorkspace() => RefreshPermissions();

    private void RefreshPermissions()
    {
        _permissions.Children.Clear();
        _permissions.Children.Add(PermissionRow("Screen Recording", "Live previews and client window titles", MacNative.PermissionGranted(0) != 0, 0));
        _permissions.Children.Add(PermissionRow("Accessibility", "Switching clients, hotkeys, hiding and moving client windows", MacNative.PermissionGranted(1) != 0, 1));
        _permissions.Children.Add(PermissionRow("System Audio Recording", "Muting background clients (macOS asks the first time a client is muted)", null, 2));
    }

    private Control PermissionRow(string name, string purpose, bool? granted, int kind)
    {
        var text = new StackPanel { Spacing = 2, Children = { Text(name, 13, _theme.Text, true), Muted(purpose) } };
        var status = Text(granted switch { true => "Allowed", false => "Not allowed", _ => "Managed by macOS" }, 12,
            granted switch { true => _theme.Positive, false => _theme.Danger, _ => _theme.Muted }, true);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(text);
        Grid.SetColumn(status, 1); status.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(status);
        if (granted != true)
        {
            var button = Button(granted == false ? "Allow..." : "Open Settings", () => { MacNative.RequestPermission(kind); });
            Grid.SetColumn(button, 2); grid.Children.Add(button);
        }
        return grid;
    }

    private async void Apply(string key, string value)
    {
        var result = await _backend.ExecuteAsync(new WorkspaceCommand("setting", key, value));
        _importStatus.Text = result.Success ? "" : result.Message;
    }

    private async Task ImportAsync()
    {
        if (!int.TryParse(_windowsWidth.Text, out int width) || !int.TryParse(_windowsHeight.Text, out int height) || width < 320 || height < 240)
        {
            _importStatus.Text = "Enter the Windows display size in pixels, for example 1920 x 1080.";
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Windows EVE-O Preview profiles", AllowMultiple = false });
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is null) return;
        var main = MacNative.ListScreens().FirstOrDefault(s => s.Primary);
        double scale = main is null ? 1 : Math.Min(main.Width / width, main.Height / height);
        var result = WindowsProfileImporter.Import(folder, _profiles.ProfileRootDirectory, scale);
        _profiles.RefreshProfileLocations();
        _importStatus.Text = result.Imported.Count == 0 && result.Skipped.Count == 0
            ? "No profiles were found in that folder."
            : $"Imported: {(result.Imported.Count > 0 ? string.Join(", ", result.Imported) : "none")}." +
              (result.Skipped.Count > 0 ? $" Skipped: {string.Join("; ", result.Skipped)}." : "") +
              " Select an imported profile from the Profile menu at the top.";
    }

    private Border Card(string title, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text(title, 14, _theme.Text, true));
        foreach (var child in children) panel.Children.Add(child);
        return new Border
        {
            Background = WorkspaceTheme.Brush(_theme.Surface), BorderBrush = WorkspaceTheme.Brush(_theme.Border),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_theme.Radius), Padding = new Thickness(14), Child = panel,
        };
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) { child.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(child); }
        return row;
    }

    private TextBlock Text(string text, double size, string color, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = WorkspaceTheme.Brush(color), TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
    };

    private TextBlock Muted(string text) => Text(text, 11, _theme.Muted);
    private TextBlock Label(string text) => Text(text, 12, _theme.Text);

    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 30 };
        button.Click += (_, _) => action();
        return button;
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinHeight = 30 };
        button.Click += async (_, _) => await action();
        return button;
    }
}

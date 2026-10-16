using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EveOPreview.UI;

/// <summary>
/// The "UI Mirrors" workspace page: choose a running client, draw the area to mirror on it, and manage
/// existing mirrors. Hosts implement <see cref="IWorkspaceMirrors"/>; the page never captures or stores.
/// </summary>
public sealed class MirrorsView : UserControl, IWorkspaceLiveModule, IDisposable
{
    public const string ModuleId = "Mirrors";
    private static readonly int[] ZoomSteps = [50, 75, 100, 125, 150, 200, 300];
    private static readonly int[] OpacitySteps = [100, 80, 60, 40];
    private static readonly int[] FrameRateSteps = [1, 2, 5, 10, 15, 30];
    private readonly IWorkspaceMirrors _mirrors;
    private readonly WorkspaceLocalization _localization;
    private readonly WorkspaceTheme _theme;
    private readonly ComboBox _client = new() { Name = "mirror-client", MinWidth = 260, MinHeight = 32 };
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _message;
    private string _signature = "";
    private bool _busy, _disposed;

    public static WorkspaceModule CreateModule() => new(ModuleId, "UI Mirrors",
        "Mirror an area of a client window, such as Local chat, into its own window.", backend => new MirrorsView(backend))
    {
        SearchTargets = [new("mirrors", "UI Mirrors", ["mirror", "mirrors", "ui mirror", "local", "local chat", "chat", "crop",
            "part of window", "magnify", "zoom", "intel", "extract"], () => { })]
    };

    public MirrorsView(IWorkspaceBackend backend)
    {
        _mirrors = (IWorkspaceMirrors)backend;
        var snapshot = backend.Read();
        _localization = new(snapshot.UiLanguage);
        _theme = WorkspaceTheme.Get(snapshot.Theme);
        _message = new TextBlock { Name = "mirror-message", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
            Foreground = WorkspaceTheme.Brush(_theme.Muted) };
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(Card("New mirror",
            Muted("Choose a character. EVE-O brings that client to the front; drag a box over the part of its window to mirror, for example Local chat. The mirror then works like a preview: drag to move, drag an edge to resize, click to switch to the client, right-click for options."),
            Row(Label("Client"), _client, Button("Select area...", "mirror-create", CreateAsync)),
            _message));
        root.Children.Add(Card("Your mirrors",
            Muted("Mirrors belong to the current profile and show while their client runs. They hide together with the previews."),
            _list));
        Content = root;
        _mirrors.MirrorsChanged += OnMirrorsChanged;
        RefreshWorkspace();
    }

    private void OnMirrorsChanged() => Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshWorkspace(); });

    public void RefreshWorkspace()
    {
        var snapshot = _mirrors.ReadMirrors();
        var selected = _client.SelectedItem as string;
        if (!snapshot.Clients.SequenceEqual(_client.ItemsSource?.Cast<string>() ?? []))
            _client.ItemsSource = snapshot.Clients.ToArray();
        _client.SelectedItem = snapshot.Clients.Contains(selected) ? selected : snapshot.Clients.FirstOrDefault();
        _client.PlaceholderText = snapshot.Clients.Count == 0 ? L("No EVE client is running") : null;
        // Rebuild rows only when mirrors change, so an edited name is not replaced while typing.
        string signature = string.Join("|", snapshot.Mirrors) + snapshot.SupportsFrameRate;
        if (signature == _signature) return;
        _signature = signature;
        _list.Children.Clear();
        if (snapshot.Mirrors.Count == 0) { _list.Children.Add(Muted("No mirrors yet.")); return; }
        foreach (var mirror in snapshot.Mirrors) _list.Children.Add(MirrorRow(mirror, snapshot.SupportsFrameRate));
    }

    private async Task CreateAsync()
    {
        if (_client.SelectedItem is not string title) { Report("Choose a running client first."); return; }
        Report("Drag over the area to mirror on the client window. Esc cancels.");
        var result = await _mirrors.CreateMirrorAsync(title);
        Report(result.Success ? "" : result.Localize(L));
    }

    private Control MirrorRow(MirrorItem mirror, bool frameRate)
    {
        var name = new TextBox { Name = "mirror-name-" + mirror.Id, Text = mirror.Name, MinWidth = 200 };
        void Rename() { if (name.Text != mirror.Name) _ = Edit(mirror.Id, "rename", name.Text ?? ""); }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Rename(); };
        var details = Muted(string.Format(System.Globalization.CultureInfo.CurrentCulture, L("{0} · area {1} x {2} · shown {3} x {4} ({5:0}%)"),
            mirror.Character, mirror.SourceWidth, mirror.SourceHeight, mirror.Width, mirror.Height, mirror.Zoom * 100), raw: true);
        var settings = Row(
            Label("Size"), Choice("mirror-zoom-" + mirror.Id, ZoomSteps, (int)Math.Round(mirror.Zoom * 100), v => $"{v}%", v => Edit(mirror.Id, "zoom", v)),
            Label("Opacity"), Choice("mirror-opacity-" + mirror.Id, OpacitySteps, (int)Math.Round(mirror.Opacity * 100), v => $"{v}%", v => Edit(mirror.Id, "opacity", v)));
        if (frameRate)
        {
            settings.Children.Add(Label("Updates"));
            settings.Children.Add(Choice("mirror-fps-" + mirror.Id, FrameRateSteps, mirror.FrameRate, v => $"{v} FPS", v => Edit(mirror.Id, "fps", v)));
        }
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Row(name,
            Button("Reselect area...", "mirror-reselect-" + mirror.Id, async () =>
            {
                var result = await _mirrors.ReselectMirrorAsync(mirror.Id);
                Report(result.Success ? "" : result.Localize(L));
            }),
            Button("Remove", "mirror-remove-" + mirror.Id, () => Edit(mirror.Id, "remove"))));
        panel.Children.Add(details);
        panel.Children.Add(settings);
        return new Border
        {
            Name = "mirror-" + mirror.Id, Background = WorkspaceTheme.Brush(_theme.Inset),
            CornerRadius = new CornerRadius(_theme.Radius), Padding = new Thickness(10), Child = panel,
        };
    }

    private async Task Edit(string id, string action, object value = null!)
    {
        var result = await _mirrors.EditMirrorAsync(id, action, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        Report(result.Success ? "" : result.Localize(L));
    }

    private ComboBox Choice(string name, int[] steps, int current, Func<int, string> label, Func<int, Task> apply)
    {
        var options = steps.Contains(current) ? steps : steps.Append(current).OrderBy(v => v).ToArray();
        var box = new ComboBox { Name = name, ItemsSource = options.Select(label).ToArray(), SelectedIndex = Array.IndexOf(options, current), MinHeight = 30 };
        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedIndex >= 0 && options[box.SelectedIndex] != current) await apply(options[box.SelectedIndex]);
        };
        return box;
    }

    private Border Card(string title, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = L(title), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = WorkspaceTheme.Brush(_theme.Text) });
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

    private Button Button(string title, string name, Func<Task> action)
    {
        var button = new Button { Name = name, Content = L(title), MinHeight = 30 };
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            try { await action(); }
            finally { _busy = false; }
        };
        return button;
    }

    private string L(string value) => _localization.Get(value);
    private TextBlock Label(string text) => new() { Text = L(text), FontSize = 12, Foreground = WorkspaceTheme.Brush(_theme.Text) };
    private TextBlock Muted(string text, bool raw = false) => new()
    {
        Text = raw ? text : L(text), FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush(_theme.Muted),
    };
    private void Report(string message) { _message.Text = L(message); _message.IsVisible = message.Length > 0; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mirrors.MirrorsChanged -= OnMirrorsChanged;
    }
}

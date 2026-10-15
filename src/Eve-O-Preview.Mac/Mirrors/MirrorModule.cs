using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveOPreview.UI;

namespace EveOPreview.Mac.Mirrors;

/// <summary>The "UI Mirrors" workspace page: create mirrors of client areas and manage them.</summary>
public sealed class MirrorModule(MirrorManager mirrors)
{
    public const string Id = "Mirrors";

    public WorkspaceModule Create() => new(Id, "UI Mirrors", "Mirror an area of a client window, such as Local chat, into its own window.",
        backend => new MirrorModuleView(backend, mirrors))
    {
        SearchTargets = [new("mirrors", "UI Mirrors", ["mirror", "mirrors", "ui mirror", "local", "local chat", "chat", "crop", "region of window",
            "magnify", "zoom", "intel", "overview", "extract"], () => { })]
    };
}

internal sealed class MirrorModuleView : UserControl, IWorkspaceLiveModule, IDisposable
{
    private readonly MirrorManager _mirrors;
    private readonly WorkspaceTheme _theme;
    private readonly ComboBox _client = new() { MinWidth = 260, MinHeight = 32 };
    private readonly TextBlock _status;
    private readonly StackPanel _list = new() { Spacing = 8 };

    public MirrorModuleView(IWorkspaceBackend backend, MirrorManager mirrors)
    {
        _mirrors = mirrors;
        _theme = WorkspaceTheme.Get(backend.Read().Theme);
        _status = Muted("");
        var create = Button("Select area...", async () =>
        {
            if (_client.SelectedItem is not string title) { _status.Text = "Choose a running client first."; return; }
            _status.Text = "Drag over the area to mirror on the client window. Esc cancels.";
            string error = await _mirrors.CreateAsync(title);
            _status.Text = error ?? "";
        });
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(Card("New mirror",
            Muted("Choose a character. EVE-O brings that client to the front; drag a box over the part of its window to mirror, for example Local chat. The mirror then works like a preview: drag to move, drag an edge to resize, click to switch to the client, right-click for options."),
            Row(Label("Client"), _client, create),
            _status));
        root.Children.Add(Card("Your mirrors", Muted("Mirrors belong to the current profile and show while their client runs. Hide all previews hides them too."), _list));
        Content = root;
        _mirrors.Changed += OnChanged;
        RefreshWorkspace();
    }

    private void OnChanged() => Dispatcher.UIThread.Post(RefreshWorkspace);

    public void RefreshWorkspace()
    {
        var clients = _mirrors.AvailableClients();
        var selected = _client.SelectedItem as string;
        _client.ItemsSource = clients;
        _client.SelectedItem = clients.Contains(selected) ? selected : clients.FirstOrDefault();
        _client.PlaceholderText = clients.Count == 0 ? "No EVE client is running" : null;
        _list.Children.Clear();
        if (_mirrors.Mirrors.Count == 0) { _list.Children.Add(Muted("No mirrors yet.")); return; }
        foreach (var mirror in _mirrors.Mirrors) _list.Children.Add(MirrorRow(mirror));
    }

    private Control MirrorRow(MirrorDefinition mirror)
    {
        var name = new TextBox { Text = mirror.Name, MinWidth = 200 };
        name.LostFocus += (_, _) => _mirrors.Rename(mirror.Id, name.Text);
        name.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) _mirrors.Rename(mirror.Id, name.Text); };
        var details = Muted($"{mirror.Character} · area {mirror.SourceWidth:0} x {mirror.SourceHeight:0} pt · shown {mirror.Width} x {mirror.Height} ({mirror.Zoom * 100:0}%)");
        var zoom = Choice(new[] { 50, 75, 100, 125, 150, 200, 300 }, (int)Math.Round(mirror.Zoom * 100), v => $"{v}%", v => _mirrors.Execute(mirror.Id, "zoom:" + v));
        var opacity = Choice(new[] { 100, 80, 60, 40 }, (int)Math.Round(mirror.Opacity * 100), v => $"{v}% opaque", v => _mirrors.Execute(mirror.Id, "opacity:" + v));
        var fps = Choice(new[] { 1, 2, 5, 10, 15, 30 }, mirror.FrameRate, v => $"{v} FPS", v => _mirrors.Execute(mirror.Id, "fps:" + v));
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Row(name, Button("Reselect area...", async () => { _status.Text = await _mirrors.ReselectAsync(mirror.Id) ?? ""; }),
            Button("Remove", () => _mirrors.Execute(mirror.Id, "remove"))));
        panel.Children.Add(details);
        panel.Children.Add(Row(Label("Size"), zoom, Label("Opacity"), opacity, Label("Updates"), fps));
        return new Border
        {
            Background = WorkspaceTheme.Brush(_theme.Inset), CornerRadius = new CornerRadius(_theme.Radius), Padding = new Thickness(10), Child = panel,
        };
    }

    private ComboBox Choice(int[] values, int current, Func<int, string> label, Action<int> apply)
    {
        var options = values.Contains(current) ? values : values.Append(current).OrderBy(v => v).ToArray();
        var box = new ComboBox
        {
            ItemsSource = options.Select(label).ToArray(), SelectedIndex = Array.IndexOf(options, current), MinHeight = 30,
        };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0 && options[box.SelectedIndex] != current) apply(options[box.SelectedIndex]); };
        return box;
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

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 30 };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string text, Func<System.Threading.Tasks.Task> action)
    {
        var button = new Button { Content = text, MinHeight = 30 };
        button.Click += async (_, _) => await action();
        return button;
    }

    public void Dispose() => _mirrors.Changed -= OnChanged;
}

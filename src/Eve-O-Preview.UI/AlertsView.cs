using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace EveOPreview.UI;

/// <summary>
/// The "Local alerts" workspace page: watch characters' Local member lists for non-friendly standings
/// and member-count increases, with sound, preview flash and Discord output. Hosts implement
/// <see cref="IWorkspaceAlerts"/>; the page never captures, detects or stores.
/// </summary>
public sealed class AlertsView : UserControl, IWorkspaceLiveModule, IDisposable
{
    public const string ModuleId = "Alerts";
    private static readonly double[] ScanSteps = [0.5, 1, 2, 3, 5];
    private static readonly int[] ReminderSteps = [0, 15, 30, 60, 120];
    private static readonly int[] VolumeSteps = [25, 50, 80, 100];
    private readonly IWorkspaceAlerts _alerts;
    private readonly WorkspaceLocalization _localization;
    private readonly WorkspaceTheme _theme;
    private readonly ComboBox _client = new() { Name = "alert-client", MinWidth = 260, MinHeight = 32 };
    private readonly CheckBox _paused = new() { Name = "alert-paused" };
    private readonly CheckBox _flash = new() { Name = "alert-flash" };
    private readonly ComboBox _scans = new() { Name = "alert-scans", MinHeight = 30 };
    private readonly ComboBox _reminder = new() { Name = "alert-reminder", MinHeight = 30 };
    private readonly ComboBox _volume = new() { Name = "alert-volume", MinHeight = 30 };
    private readonly ComboBox _sound = new() { Name = "alert-sound", MinHeight = 30, MinWidth = 180 };
    private readonly ComboBox _countSound = new() { Name = "alert-count-sound", MinHeight = 30, MinWidth = 180 };
    private readonly TextBox _webhook = new() { Name = "alert-webhook", MinWidth = 420, PasswordChar = '•', Watermark = "https://discord.com/api/webhooks/..." };
    private readonly CheckBox _webhookEnabled = new() { Name = "alert-webhook-enabled" };
    private readonly TextBlock _webhookStatus;
    private readonly TextBlock _countHint;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _message;
    private readonly Dictionary<string, (TextBlock Status, Ellipse Dot, Image View, CheckBox Show)> _rows = new();
    private readonly DispatcherTimer _viewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AlertOptionsItem _options = new(false, 2, 30, true, "default", "default", 80, "", false, "");
    private string? _signature;
    private bool _busy, _updating, _disposed;

    public static WorkspaceModule CreateModule() => new(ModuleId, "Local alerts",
        "Alarm when a non-friendly pilot appears in a character's Local, even while the client is in the background.", backend => new AlertsView(backend))
    {
        SearchTargets = [new("alerts", "Local alerts", ["alert", "alerts", "alarm", "local", "intel", "hostile", "neutral", "red", "standing",
            "discord", "webhook", "eve alert", "count", "ratting", "warning"], () => { })]
    };

    public AlertsView(IWorkspaceBackend backend)
    {
        _alerts = (IWorkspaceAlerts)backend;
        var snapshot = backend.Read();
        _localization = new(snapshot.UiLanguage);
        _theme = WorkspaceTheme.Get(snapshot.Theme);
        _message = new TextBlock { Name = "alert-message", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
            Foreground = WorkspaceTheme.Brush(_theme.Muted) };
        _webhookStatus = Muted("");
        _countHint = Muted("");
        _paused.Content = L("Pause all alerts");
        _flash.Content = L("Flash the preview of a character with someone in Local");
        _webhookEnabled.Content = L("Post alerts to Discord");
        _scans.ItemsSource = ScanSteps.Select(v => string.Format(CultureInfo.CurrentCulture, L("{0} per second"), v)).ToArray();
        _reminder.ItemsSource = ReminderSteps.Select(v => v == 0 ? L("No reminders") : string.Format(CultureInfo.CurrentCulture, L("Every {0} s"), v)).ToArray();
        _volume.ItemsSource = VolumeSteps.Select(v => $"{v}%").ToArray();
        _paused.IsCheckedChanged += (_, _) => Option("paused", (_paused.IsChecked == true).ToString());
        _flash.IsCheckedChanged += (_, _) => Option("flash", (_flash.IsChecked == true).ToString());
        _webhookEnabled.IsCheckedChanged += (_, _) => Option("webhook-enabled", (_webhookEnabled.IsChecked == true).ToString());
        _scans.SelectionChanged += (_, _) => { if (_scans.SelectedIndex >= 0) Option("scans", ScanSteps[_scans.SelectedIndex].ToString(CultureInfo.InvariantCulture)); };
        _reminder.SelectionChanged += (_, _) => { if (_reminder.SelectedIndex >= 0) Option("reminder", ReminderSteps[_reminder.SelectedIndex].ToString(CultureInfo.InvariantCulture)); };
        _volume.SelectionChanged += (_, _) => { if (_volume.SelectedIndex >= 0) Option("volume", VolumeSteps[_volume.SelectedIndex].ToString(CultureInfo.InvariantCulture)); };
        _sound.SelectionChanged += (_, _) => _ = SoundChosen(_sound, "sound");
        _countSound.SelectionChanged += (_, _) => _ = SoundChosen(_countSound, "count-sound");
        _webhook.LostFocus += (_, _) => CommitWebhook();
        _webhook.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitWebhook(); };
        var reveal = new ToggleButton { Name = "alert-webhook-reveal", Content = L("Show"), MinHeight = 30 };
        reveal.IsCheckedChanged += (_, _) => _webhook.RevealPassword = reveal.IsChecked == true;

        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(Card("New watch",
            Muted("EVE-O watches the Local member list of each character below, also while the client is behind other windows. Every standing icon that is not corp (green), alliance or positive standing (blue) or fleet (purple) raises an alarm, and so does a member count that rises without a friendly icon. In EVE, turn on the icon for neutral standings, so pilots without standings show one. Use the member list without portraits."),
            Muted("Choose a character. EVE-O brings that client to the front: drag a box over the Local member list (the standing icons and names), then a small box over the member count."),
            _countHint,
            Row(Label("Client"), _client, Button("Select areas...", "alert-create", CreateAsync)),
            _message));
        root.Children.Add(Card("Watches",
            Muted("Watches belong to the current profile. A watch pauses while its client is hidden or minimized, because the client then draws nothing."),
            _list));
        root.Children.Add(Card("Alarm",
            Row(_paused),
            Row(_flash),
            Row(Label("Scans"), _scans, Label("Reminder while someone stays"), _reminder),
            Row(Label("Alarm sound"), _sound, Button("Test", "alert-test-sound", () => Test("sound")),
                Label("Count sound"), _countSound, Button("Test", "alert-test-count-sound", () => Test("count-sound"))),
            Row(Label("Volume"), _volume),
            Muted("Switching to a character (click its preview or cycle to it) acknowledges its alarm and stops the reminders until the next arrival. The Acknowledge alerts and Pause alerts shortcuts are under Switching & hotkeys.")));
        root.Children.Add(Card("Discord",
            Muted("Each arrival posts the system, the character and an image of its Local member list; an all-clear follows once Local is clean. Only the selected member-list area is sent. The system name comes from the chat logs read by Augments."),
            Row(_webhook, reveal),
            Row(_webhookEnabled, Button("Send test message", "alert-test-webhook", () => Test("webhook"))),
            _webhookStatus));
        Content = root;
        _alerts.AlertsChanged += OnAlertsChanged;
        _viewTimer.Tick += (_, _) => RefreshViews();
        AttachedToVisualTree += (_, _) => _viewTimer.Start();
        DetachedFromVisualTree += (_, _) => _viewTimer.Stop();
        RefreshWorkspace();
    }

    private void OnAlertsChanged() => Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshWorkspace(); });

    public void RefreshWorkspace()
    {
        var snapshot = _alerts.ReadAlerts();
        var selected = _client.SelectedItem as string;
        if (!snapshot.Clients.SequenceEqual(_client.ItemsSource?.Cast<string>() ?? []))
            _client.ItemsSource = snapshot.Clients.ToArray();
        _client.SelectedItem = snapshot.Clients.Contains(selected) ? selected : snapshot.Clients.FirstOrDefault();
        _client.PlaceholderText = snapshot.Clients.Count == 0 ? L("No EVE client is running") : null;
        _countHint.Text = snapshot.CanReadCount ? L("Press Esc at the second step to skip the count.")
            : L("This system cannot read text, so only the member list is watched.");
        ApplyOptions(snapshot.Options);
        // Rebuild rows only when watches change, so an edited name is not replaced while typing.
        string signature = string.Join("|", snapshot.Watches.Select(w => $"{w.Id}:{w.Name}:{w.Enabled}:{w.HasCount}:{w.SourceTitle}"));
        if (signature != _signature)
        {
            _signature = signature;
            _rows.Clear();
            _list.Children.Clear();
            if (snapshot.Watches.Count == 0) _list.Children.Add(Muted("No watches yet."));
            foreach (var watch in snapshot.Watches) _list.Children.Add(WatchRow(watch));
        }
        foreach (var watch in snapshot.Watches)
            if (_rows.TryGetValue(watch.Id, out var row))
            {
                row.Status.Text = StatusText(watch);
                row.Dot.Fill = WorkspaceTheme.Brush(StatusColor(watch.Status));
            }
    }

    private void ApplyOptions(AlertOptionsItem options)
    {
        _options = options;
        _updating = true;
        try
        {
            _paused.IsChecked = options.Paused;
            _flash.IsChecked = options.FlashPreviews;
            _webhookEnabled.IsChecked = options.WebhookEnabled;
            _scans.SelectedIndex = Nearest(ScanSteps, options.ScansPerSecond);
            _reminder.SelectedIndex = Nearest(ReminderSteps.Select(v => (double)v).ToArray(), options.ReminderSeconds);
            _volume.SelectedIndex = Nearest(VolumeSteps.Select(v => (double)v).ToArray(), options.Volume);
            FillSound(_sound, options.Sound);
            FillSound(_countSound, options.CountSound);
            if (!_webhook.IsFocused && _webhook.Text != options.WebhookUrl) _webhook.Text = options.WebhookUrl;
            _webhookStatus.Text = options.WebhookStatus;
            _webhookStatus.IsVisible = options.WebhookStatus.Length > 0;
        }
        finally { _updating = false; }
    }

    private static int Nearest(double[] steps, double value)
    {
        int best = 0;
        for (int i = 1; i < steps.Length; i++) if (Math.Abs(steps[i] - value) < Math.Abs(steps[best] - value)) best = i;
        return best;
    }

    /// <summary>Items: built-in, none, the current file (if any), then "Choose file...".</summary>
    private void FillSound(ComboBox box, string value)
    {
        var items = new List<string> { L("Built-in alarm"), L("No sound") };
        bool custom = value is not ("default" or "none");
        if (custom) items.Add(System.IO.Path.GetFileName(value));
        items.Add(L("Choose .wav file..."));
        if (!items.SequenceEqual(box.ItemsSource?.Cast<string>() ?? [])) box.ItemsSource = items.ToArray();
        box.SelectedIndex = value == "default" ? 0 : value == "none" ? 1 : 2;
    }

    private async Task SoundChosen(ComboBox box, string key)
    {
        if (_updating || box.SelectedIndex < 0) return;
        string current = key == "sound" ? _options.Sound : _options.CountSound;
        bool custom = current is not ("default" or "none");
        int chooser = custom ? 3 : 2;
        if (box.SelectedIndex == 0) { if (current != "default") Option(key, "default"); return; }
        if (box.SelectedIndex == 1) { if (current != "none") Option(key, "none"); return; }
        if (box.SelectedIndex != chooser) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        string? path = null;
        if (storage is not null)
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Choose an alarm sound"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(L("WAV sound")) { Patterns = ["*.wav"] }],
            });
            path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        if (path is null) { ApplyOptions(_options); return; }
        Option(key, path);
    }

    private void CommitWebhook()
    {
        string value = _webhook.Text?.Trim() ?? "";
        if (value != _options.WebhookUrl) Option("webhook-url", value);
    }

    private async void Option(string key, string value)
    {
        if (_updating) return;
        var result = await _alerts.EditAlertOptionsAsync(key, value);
        Report(result.Success ? "" : result.Localize(L));
        if (!result.Success) RefreshWorkspace();
    }

    private async Task Test(string test)
    {
        var result = await _alerts.TestAlertAsync(test);
        Report(result.Localize(L));
    }

    private async Task CreateAsync()
    {
        if (_client.SelectedItem is not string title) { Report("Choose a running client first."); return; }
        Report("Drag over the Local member list on the client window, then over the member count. Esc cancels.");
        var result = await _alerts.CreateAlertWatchAsync(title);
        Report(result.Success || result.WasCancelled ? "" : result.Localize(L));
    }

    private Control WatchRow(AlertWatchItem watch)
    {
        var name = new TextBox { Name = "alert-name-" + watch.Id, Text = watch.Name, MinWidth = 200 };
        void Rename() { if (name.Text != watch.Name) _ = Edit(watch.Id, "rename", name.Text ?? ""); }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Rename(); };
        var enabled = new CheckBox { Name = "alert-enabled-" + watch.Id, Content = L("On"), IsChecked = watch.Enabled };
        enabled.IsCheckedChanged += (_, _) => _ = Edit(watch.Id, "enabled", (enabled.IsChecked == true).ToString());
        var dot = new Ellipse { Width = 10, Height = 10, Fill = WorkspaceTheme.Brush(StatusColor(watch.Status)) };
        var status = new TextBlock { Name = "alert-status-" + watch.Id, Text = StatusText(watch), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = WorkspaceTheme.Brush(_theme.Text) };
        var view = new Image { Name = "alert-view-" + watch.Id, MaxHeight = 260, MaxWidth = 420, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
        var show = new CheckBox { Name = "alert-show-" + watch.Id, Content = L("Show what EVE-O sees (red outline: alarms, green: ignored)") };
        show.IsCheckedChanged += (_, _) => { view.IsVisible = show.IsChecked == true; RefreshViews(); };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Row(name, enabled,
            Button("Reselect areas...", "alert-reselect-" + watch.Id, async () =>
            {
                var result = await _alerts.EditAlertWatchAsync(watch.Id, "reselect");
                Report(result.Success || result.WasCancelled ? "" : result.Localize(L));
            }),
            Button("Use these areas for all characters", "alert-copy-" + watch.Id, () => Edit(watch.Id, "copy-to-all", "", true)),
            Button("Remove", "alert-remove-" + watch.Id, () => Edit(watch.Id, "remove"))));
        panel.Children.Add(Muted(string.Format(CultureInfo.CurrentCulture, L(watch.HasCount ? "{0} · member list and count" : "{0} · member list"), watch.Character), raw: true));
        panel.Children.Add(Row(dot, status));
        panel.Children.Add(show);
        panel.Children.Add(view);
        _rows[watch.Id] = (status, dot, view, show);
        return new Border
        {
            Name = "alert-" + watch.Id, Background = WorkspaceTheme.Brush(_theme.Inset),
            CornerRadius = new CornerRadius(_theme.Radius), Padding = new Thickness(10), Child = panel,
        };
    }

    private void RefreshViews()
    {
        foreach (var (id, row) in _rows)
        {
            if (row.Show.IsChecked != true) continue;
            var png = _alerts.ReadAlertView(id);
            if (png is null) continue;
            var previous = row.View.Source as IDisposable;
            row.View.Source = new Bitmap(new MemoryStream(png));
            previous?.Dispose();
        }
    }

    private string StatusText(AlertWatchItem watch) => watch.Status switch
    {
        AlertWatchStatus.Alarm => L("Alarm") + " · " + watch.Detail,
        _ => watch.Detail,
    };

    private string StatusColor(AlertWatchStatus status) => status switch
    {
        AlertWatchStatus.Alarm => "#FF3B3B",
        AlertWatchStatus.Clear => "#35D06B",
        AlertWatchStatus.Paused => "#FFB347",
        AlertWatchStatus.Starting => "#6D9FFF",
        _ => _theme.Muted,
    };

    private async Task Edit(string id, string action, string value = "", bool reportSuccess = false)
    {
        var result = await _alerts.EditAlertWatchAsync(id, action, value);
        Report(result.Success && !reportSuccess ? "" : result.Localize(L));
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

    private static WrapPanel Row(params Control[] children)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
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
        _viewTimer.Stop();
        _alerts.AlertsChanged -= OnAlertsChanged;
    }
}

using Avalonia.Controls;
using Avalonia.VisualTree;

namespace EveOPreview.UI.Smoke;

internal static partial class Program
{
    private static int CheckAlerts(string output)
    {
        int renders = 0;
        foreach (string theme in new[] { "Dark", "Light" })
        {
            foreach (bool canReadCount in new[] { true, false })
            {
                var backend = new AlertBackend { CanReadCount = canReadCount };
                backend.ExecuteAsync(new("theme", Value: theme)).GetAwaiter().GetResult();
                using var view = new WorkspaceView(backend, [RegionsView.CreateModule(), AlertsView.CreateModule()]);
                var window = new Window { Width = 1100, Height = 900, Content = view };
                window.Show(); view.Navigate(AlertsView.ModuleId); Flush();
                string suffix = canReadCount ? "" : "-no-text";
                Capture(window, Path.Combine(output, theme.ToLowerInvariant() + "-alerts-empty" + suffix + ".png")); renders++;
                Require(view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "No watches yet."), "An empty watch list must say so.");
                Require(view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == (canReadCount
                    ? "Press Esc at the second step to skip the count." : "This system cannot read text, so only the member list is watched.")),
                    "The count hint must follow the host's text recognition.");
                var client = FindControl<ComboBox>(view, "alert-client");
                Require(client.SelectedItem as string == "EVE - Pilot One", "The first running client must be preselected.");
                client.SelectedItem = "EVE - Pilot Two"; Flush();
                Click(view, "alert-create"); Flush();
                Require(backend.Created.SequenceEqual(["EVE - Pilot Two"]), "Select areas must request the chosen client.");
                var watch = backend.Items[0];
                Require(FindControl<Border>(view, "alert-" + watch.Id) is not null, "A created watch must appear in the list.");
                Require(FindControl<TextBlock>(view, "alert-status-" + watch.Id).Text == "Clear · 2 friendly · Local 3", "A clear watch shows its reading.");

                // Status changes update the row in place, without rebuilding the editable name.
                var name = FindControl<TextBox>(view, "alert-name-" + watch.Id);
                backend.Items[0] = watch with { Status = AlertWatchStatus.Alarm, Hostile = 1, Detail = "1 red · Local 4" };
                backend.Notify(); Flush();
                Require(FindControl<TextBlock>(view, "alert-status-" + watch.Id).Text == "Alarm · 1 red · Local 4", "An alarm must show in the row.");
                Require(ReferenceEquals(name, FindControl<TextBox>(view, "alert-name-" + watch.Id)), "Status updates must not rebuild the row.");
                Capture(window, Path.Combine(output, theme.ToLowerInvariant() + "-alerts-alarm" + suffix + ".png")); renders++;

                FindControl<CheckBox>(view, "alert-paused").IsChecked = true; Flush();
                Require(backend.OptionEdits.Contains(("paused", "True")), "Pause all alerts must be sent.");
                FindControl<ComboBox>(view, "alert-reminder").SelectedIndex = 3; Flush();
                Require(backend.OptionEdits.Contains(("reminder", "60")), "Choosing a reminder must send its seconds.");
                FindControl<ComboBox>(view, "alert-volume").SelectedIndex = 1; Flush();
                Require(backend.OptionEdits.Contains(("volume", "50")), "Choosing a volume must send its percentage.");
                Require(backend.Options.Volume == 50 && FindControl<ComboBox>(view, "alert-volume").SelectedIndex == 1, "The page must follow the saved options.");
                var webhook = FindControl<TextBox>(view, "alert-webhook");
                webhook.Text = "https://discord.com/api/webhooks/1/abc";
                webhook.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Flush();
                Require(backend.OptionEdits.Contains(("webhook-url", "https://discord.com/api/webhooks/1/abc")), "Leaving the webhook field must save it.");
                Require(FindControl<CheckBox>(view, "alert-webhook-enabled").IsChecked == true, "A new webhook turns Discord messages on.");
                Click(view, "alert-test-sound"); Flush();
                Click(view, "alert-test-webhook"); Flush();
                Require(backend.Tests.SequenceEqual(["sound", "webhook"]), "Test buttons must reach the host.");
                Require(FindControl<TextBlock>(view, "alert-message").Text == "Test message sent to Discord", "Test results must be reported.");

                FindControl<CheckBox>(view, "alert-show-" + watch.Id).IsChecked = true; Flush();
                Require(FindControl<Image>(view, "alert-view-" + watch.Id).IsVisible, "Showing what EVE-O sees must reveal the capture.");
                name.Text = "Ratting Local"; name.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Flush();
                Require(backend.Items[0].Name == "Ratting Local", "Leaving the name field must rename the watch.");
                FindControl<CheckBox>(view, "alert-enabled-" + watch.Id).IsChecked = false; Flush();
                Require(backend.WatchEdits.Contains(("enabled", "False")), "Turning a watch off must be sent.");
                Click(view, "alert-copy-" + watch.Id); Flush();
                Require(backend.WatchEdits.Contains(("copy-to-all", "")), "Use for all characters must be sent.");
                Click(view, "alert-reselect-" + watch.Id); Flush();
                Require(backend.WatchEdits.Contains(("reselect", "")), "Reselect must request the watch's areas.");
                Click(view, "alert-remove-" + watch.Id); Flush();
                Require(backend.Items.Count == 0 && view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "No watches yet."),
                    "Removing the last watch must return to the empty list.");
                backend.ClientsRunning = false; backend.Notify(); Flush();
                Click(view, "alert-create"); Flush();
                Require(backend.Created.Count == 1 && FindControl<TextBlock>(view, "alert-message").IsVisible,
                    "Select areas without a client must explain instead of calling the host.");
                window.Close();
            }
        }
        Console.WriteLine("PASS: Local alerts page creation, live status, options, webhook, tests, watch edits, count capability and empty states.");
        return renders;
    }

    private sealed class AlertBackend : IWorkspaceBackend, IWorkspaceAlerts
    {
        private readonly SmokeBackend _inner = new();
        public List<AlertWatchItem> Items { get; } = [];
        public List<string> Created { get; } = [];
        public List<string> Tests { get; } = [];
        public List<(string Key, string Value)> OptionEdits { get; } = [];
        public List<(string Action, string Value)> WatchEdits { get; } = [];
        public AlertOptionsItem Options { get; private set; } = new(false, 2, 30, true, "default", "default", 80, "", false, "");
        public bool CanReadCount { get; init; }
        public bool ClientsRunning { get; set; } = true;
        public event Action? Changed;
        public event Action? AlertsChanged;
        public AlertBackend() => _inner.Changed += Notify;
        public void Notify() { Changed?.Invoke(); AlertsChanged?.Invoke(); }
        public WorkspaceSnapshot Read() => _inner.Read();
        public Task<CommandResult> ExecuteAsync(WorkspaceCommand command) => _inner.ExecuteAsync(command);
        public AlertSnapshot ReadAlerts() => new(Options, Items.ToArray(), ClientsRunning ? ["EVE - Pilot One", "EVE - Pilot Two"] : [], false, CanReadCount);
        public Task<CommandResult> CreateAlertWatchAsync(string sourceTitle)
        {
            Created.Add(sourceTitle);
            Items.Add(new("w" + Items.Count, "Pilot Two Local", sourceTitle, true, CanReadCount, AlertWatchStatus.Clear, 0, 2, 3, 0, "Clear · 2 friendly · Local 3"));
            Notify();
            return Task.FromResult(CommandResult.Ok("Watching Local for Pilot Two"));
        }
        public Task<CommandResult> EditAlertWatchAsync(string id, string action, string value = "")
        {
            WatchEdits.Add((action, value));
            int index = Items.FindIndex(w => w.Id == id);
            if (action == "remove") Items.RemoveAt(index);
            else if (action == "rename") Items[index] = Items[index] with { Name = value };
            else if (action == "enabled") Items[index] = Items[index] with { Enabled = bool.Parse(value) };
            Notify();
            return Task.FromResult(CommandResult.Ok());
        }
        public Task<CommandResult> EditAlertOptionsAsync(string key, string value)
        {
            OptionEdits.Add((key, value));
            Options = key switch
            {
                "paused" => Options with { Paused = bool.Parse(value) },
                "reminder" => Options with { ReminderSeconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
                "volume" => Options with { Volume = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
                "webhook-url" => Options with { WebhookUrl = value, WebhookEnabled = value.Length > 0 },
                "webhook-enabled" => Options with { WebhookEnabled = bool.Parse(value) },
                _ => Options,
            };
            Notify();
            return Task.FromResult(CommandResult.Ok("Alert settings saved"));
        }
        public Task<CommandResult> TestAlertAsync(string test)
        {
            Tests.Add(test);
            return Task.FromResult(CommandResult.Ok(test == "webhook" ? "Test message sent to Discord" : "Test sound played"));
        }
        public byte[]? ReadAlertView(string id) => null;
    }
}

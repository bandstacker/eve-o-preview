using Avalonia.Controls;
using Avalonia.VisualTree;

namespace EveOPreview.UI.Smoke;

internal static partial class Program
{
    private static int CheckMirrors(string output)
    {
        int renders = 0;
        foreach (string theme in new[] { "Dark", "Light" })
        {
            foreach (bool frameRate in new[] { false, true })
            {
                var backend = new MirrorBackend { FrameRate = frameRate };
                backend.ExecuteAsync(new("theme", Value: theme)).GetAwaiter().GetResult();
                using var view = new WorkspaceView(backend, [RegionsView.CreateModule(), MirrorsView.CreateModule()]);
                var window = new Window { Width = 1100, Height = 800, Content = view };
                window.Show(); view.Navigate(MirrorsView.ModuleId); Flush();
                string suffix = frameRate ? "-fps" : "";
                Capture(window, Path.Combine(output, theme.ToLowerInvariant() + "-mirrors-empty" + suffix + ".png")); renders++;
                Require(view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "No mirrors yet."), "An empty mirror list must say so.");
                var client = FindControl<ComboBox>(view, "mirror-client");
                Require(client.SelectedItem as string == "EVE - Pilot One", "The first running client must be preselected.");
                client.SelectedItem = "EVE - Pilot Two"; Flush();
                Click(view, "mirror-create"); Flush();
                Require(backend.Created.SequenceEqual(["EVE - Pilot Two"]), "Select area must request the chosen client.");
                var mirror = backend.Items[0];
                Require(FindControl<Border>(view, "mirror-" + mirror.Id) is not null, "A created mirror must appear in the list.");
                Require(view.GetVisualDescendants().Any(c => c.Name == "mirror-fps-" + mirror.Id) == frameRate,
                    "The update-rate control appears only when the host supports a mirror frame rate.");
                FindControl<ComboBox>(view, "mirror-zoom-" + mirror.Id).SelectedItem = "150%"; Flush();
                Require(backend.Edits.Contains(("zoom", "150")), "Choosing a size must send the zoom percentage.");
                Require(backend.Items[0].Width == 450, "The list must refresh after a zoom edit.");
                var name = FindControl<TextBox>(view, "mirror-name-" + mirror.Id);
                name.Text = "Local"; name.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Flush();
                Require(backend.Items[0].Name == "Local", "Leaving the name field must rename the mirror.");
                Capture(window, Path.Combine(output, theme.ToLowerInvariant() + "-mirrors-list" + suffix + ".png")); renders++;
                Click(view, "mirror-reselect-" + mirror.Id); Flush();
                Require(backend.Reselected.SequenceEqual([mirror.Id]), "Reselect must request the mirror's own area.");
                Click(view, "mirror-remove-" + mirror.Id); Flush();
                Require(backend.Items.Count == 0 && view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "No mirrors yet."),
                    "Removing the last mirror must return to the empty list.");
                backend.ClientsRunning = false; backend.Notify(); Flush();
                Require(FindControl<ComboBox>(view, "mirror-client").SelectedItem is null, "Without running clients nothing is selected.");
                Click(view, "mirror-create"); Flush();
                Require(backend.Created.Count == 1 && FindControl<TextBlock>(view, "mirror-message").IsVisible,
                    "Select area without a client must explain instead of calling the host.");
                window.Close();
            }
        }
        Console.WriteLine("PASS: UI mirrors page creation, list edits, rename, reselect, removal, frame-rate capability and empty states.");
        return renders;
    }

    private sealed class MirrorBackend : IWorkspaceBackend, IWorkspaceMirrors
    {
        private readonly SmokeBackend _inner = new();
        public List<MirrorItem> Items { get; } = [];
        public List<string> Created { get; } = [];
        public List<string> Reselected { get; } = [];
        public List<(string Action, string Value)> Edits { get; } = [];
        public bool FrameRate { get; init; }
        public bool ClientsRunning { get; set; } = true;
        public event Action? Changed;
        public event Action? MirrorsChanged;
        public MirrorBackend() => _inner.Changed += Notify;
        public void Notify() { Changed?.Invoke(); MirrorsChanged?.Invoke(); }
        public WorkspaceSnapshot Read() => _inner.Read();
        public Task<CommandResult> ExecuteAsync(WorkspaceCommand command) => _inner.ExecuteAsync(command);
        public MirrorSnapshot ReadMirrors() => new(Items.ToArray(),
            ClientsRunning ? ["EVE - Pilot One", "EVE - Pilot Two"] : [], false, FrameRate);
        public Task<CommandResult> CreateMirrorAsync(string sourceTitle)
        {
            Created.Add(sourceTitle);
            Items.Add(new("m" + Items.Count, "Pilot Two mirror", sourceTitle, 300, 200, 300, 200, 1, 5));
            Notify();
            return Task.FromResult(CommandResult.Ok("Mirror saved"));
        }
        public Task<CommandResult> ReselectMirrorAsync(string id) { Reselected.Add(id); return Task.FromResult(CommandResult.Ok()); }
        public Task<CommandResult> EditMirrorAsync(string id, string action, string value = "")
        {
            Edits.Add((action, value));
            int index = Items.FindIndex(m => m.Id == id);
            if (action == "remove") Items.RemoveAt(index);
            else if (action == "rename") Items[index] = Items[index] with { Name = value };
            else if (action == "zoom")
            {
                double zoom = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) / 100.0;
                Items[index] = Items[index] with { Width = (int)(Items[index].SourceWidth * zoom), Height = (int)(Items[index].SourceHeight * zoom) };
            }
            Notify();
            return Task.FromResult(CommandResult.Ok());
        }
    }
}

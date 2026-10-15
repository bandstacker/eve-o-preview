namespace EveOPreview.UI;

/// <summary>
/// A UI mirror: a live crop of one client window shown in its own window. Source sizes and the mirror
/// size use the host's preview pixels. The full client title is the persisted identity.
/// </summary>
public sealed record MirrorItem(string Id, string Name, string SourceTitle, int SourceWidth, int SourceHeight,
    int Width, int Height, double Opacity, int FrameRate)
{
    public string Character => SourceTitle.StartsWith("EVE - ", StringComparison.Ordinal) ? SourceTitle[6..] : SourceTitle;
    public double Zoom => SourceWidth > 0 ? (double)Width / SourceWidth : 1;
}

public sealed record MirrorSnapshot(IReadOnlyList<MirrorItem> Mirrors, IReadOnlyList<string> Clients, bool Selecting,
    bool SupportsFrameRate);

/// <summary>Optional platform capability for UI mirrors. Hosts own capture, selection and persistence.</summary>
public interface IWorkspaceMirrors
{
    MirrorSnapshot ReadMirrors();
    event Action? MirrorsChanged;
    /// <summary>Brings the client forward and lets the user draw the mirrored area.</summary>
    Task<CommandResult> CreateMirrorAsync(string sourceTitle);
    Task<CommandResult> ReselectMirrorAsync(string id);
    /// <summary>Actions: rename (Value=name), zoom (percent), opacity (percent), fps, actual-size, remove.</summary>
    Task<CommandResult> EditMirrorAsync(string id, string action, string value = "");
}

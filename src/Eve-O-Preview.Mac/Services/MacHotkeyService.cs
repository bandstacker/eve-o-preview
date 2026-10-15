using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveOPreview.Input;
using EveOPreview.Mac.Native;
using Serilog;
using Keys = EveOPreview.Input.ShortcutKeys;

namespace EveOPreview.Services.Implementation;

/// <summary>
/// Global shortcuts through a suppressing CGEventTap, reusing the upstream matcher semantics.
/// Modifier mapping (persisted grammar is Windows-style): Command = Ctrl, Option = Alt, Shift = Shift,
/// Control = Win (never part of a shortcut). Both hotkey methods use the same tap on macOS.
/// </summary>
public sealed class MacHotkeyService : IHotkeyService
{
    private static MacHotkeyService _current;
    private readonly ILogger _logger;
    private readonly HotkeyDispatchQueue _dispatch;
    private readonly WindowsHotkeyMatcher _matcher;
    private readonly object _gate = new();
    private Dictionary<Keys, HotkeyBinding> _bindings = new();
    private TaskCompletionSource<Keys> _capture;
    private bool _tapRunning;
    private bool _disposed;
    private FormattableString[] _warnings = [];

    public MacHotkeyService(ILogger logger)
    {
        _logger = logger;
        _dispatch = new(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Send), ex => logger.Error(ex, "Hotkey action failed"));
        _matcher = new(_dispatch.Enqueue);
        _current = this;
    }

    public IReadOnlyList<FormattableString> RegistrationWarnings => Volatile.Read(ref _warnings);
    public string RegistrationWarning => string.Join(" ", RegistrationWarnings.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public void Replace(IReadOnlyList<HotkeyBinding> bindings, HotkeyMode mode, bool diagnosticPassthrough = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var parsed = new Dictionary<Keys, HotkeyBinding>();
        var invalid = new List<string>();
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Shortcut)) continue;
            try
            {
                var key = ShortcutText.Parse(binding.Shortcut);
                if (key == Keys.None) continue;
                int code = (int)(key & Keys.KeyCode);
                if (code == 0 || code > 255 || code is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
                    throw new ArgumentException("A shortcut needs a non-modifier key.");
                if (MacKeyCodes.ToMac(code) is null) throw new NotSupportedException("This key does not exist on a Mac keyboard.");
                parsed.TryAdd(key, binding);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException) { invalid.Add(binding.Shortcut); }
        }
        var warnings = new List<FormattableString>();
        if (invalid.Count > 0) warnings.Add($"Invalid shortcuts: {string.Join(", ", invalid)}");
        lock (_gate)
        {
            _dispatch.Invalidate();
            _bindings = parsed;
            if (_capture == null) _matcher.Configure(_bindings, MacKeyCodes.IsVirtualKeyDown);
        }
        if (parsed.Count > 0 && !EnsureTap())
            warnings.Add($"Shortcuts need Accessibility access: System Settings > Privacy & Security > Accessibility > EVE-O Preview.");
        else if (parsed.Count == 0 && _capture == null) StopTap();
        _warnings = warnings.ToArray();
    }

    public async Task<string> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<Keys>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_capture != null) throw new InvalidOperationException("A shortcut is already being recorded.");
            _dispatch.Invalidate();
            _capture = completion;
            _matcher.Configure(new(), MacKeyCodes.IsVirtualKeyDown, key => completion.TrySetResult(key));
        }
        if (!EnsureTap())
        {
            lock (_gate) { _capture = null; _matcher.Configure(_bindings, MacKeyCodes.IsVirtualKeyDown); }
            MacNative.RequestPermission(1);
            throw new InvalidOperationException("Allow Accessibility access for EVE-O Preview, then record the shortcut again.");
        }
        try
        {
            var key = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return (key & Keys.KeyCode) == Keys.Escape ? null : ShortcutText.Format(key);
        }
        finally
        {
            lock (_gate)
            {
                _capture = null;
                _dispatch.Invalidate();
                _matcher.Configure(_bindings, MacKeyCodes.IsVirtualKeyDown);
            }
            if (_bindings.Count == 0) StopTap();
        }
    }

    private unsafe bool EnsureTap()
    {
        if (_tapRunning) return true;
        _tapRunning = MacNative.KeyTapStart(&OnKey) != 0;
        if (!_tapRunning) _logger.Warning("Keyboard event tap unavailable (Accessibility not granted)");
        return _tapRunning;
    }

    private void StopTap()
    {
        if (!_tapRunning) return;
        MacNative.KeyTapStop();
        _tapRunning = false;
    }

    [UnmanagedCallersOnly]
    private static int OnKey(int kind, ushort keyCode, ulong flags, int autorepeat)
    {
        var service = _current;
        if (service == null) return 0;
        try
        {
            int? virtualKey = MacKeyCodes.ToVirtualKey(keyCode);
            if (virtualKey is not int vk) return 0;
            bool down = kind switch { 0 => true, 1 => false, _ => MacKeyCodes.IsModifierDown(keyCode, flags) };
            lock (service._gate) return service._matcher.Process(vk, down) ? 1 : 0;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTap();
        if (_current == this) _current = null;
    }
}

/// <summary>Mac virtual key codes (kVK_*) to Windows virtual keys used by the persisted shortcut grammar.</summary>
internal static class MacKeyCodes
{
    private static readonly Dictionary<ushort, int> ToWindows = new()
    {
        [0x00] = 0x41, [0x0B] = 0x42, [0x08] = 0x43, [0x02] = 0x44, [0x0E] = 0x45, [0x03] = 0x46, [0x05] = 0x47, [0x04] = 0x48,
        [0x22] = 0x49, [0x26] = 0x4A, [0x28] = 0x4B, [0x25] = 0x4C, [0x2E] = 0x4D, [0x2D] = 0x4E, [0x1F] = 0x4F, [0x23] = 0x50,
        [0x0C] = 0x51, [0x0F] = 0x52, [0x01] = 0x53, [0x11] = 0x54, [0x20] = 0x55, [0x09] = 0x56, [0x0D] = 0x57, [0x07] = 0x58,
        [0x10] = 0x59, [0x06] = 0x5A,
        [0x1D] = 0x30, [0x12] = 0x31, [0x13] = 0x32, [0x14] = 0x33, [0x15] = 0x34, [0x17] = 0x35, [0x16] = 0x36, [0x1A] = 0x37,
        [0x1C] = 0x38, [0x19] = 0x39,
        [0x7A] = 0x70, [0x78] = 0x71, [0x63] = 0x72, [0x76] = 0x73, [0x60] = 0x74, [0x61] = 0x75, [0x62] = 0x76, [0x64] = 0x77,
        [0x65] = 0x78, [0x6D] = 0x79, [0x67] = 0x7A, [0x6F] = 0x7B, [0x69] = 0x7C, [0x6B] = 0x7D, [0x71] = 0x7E, [0x6A] = 0x7F,
        [0x40] = 0x80, [0x4F] = 0x81, [0x50] = 0x82, [0x5A] = 0x83,
        [0x52] = 0x60, [0x53] = 0x61, [0x54] = 0x62, [0x55] = 0x63, [0x56] = 0x64, [0x57] = 0x65, [0x58] = 0x66, [0x59] = 0x67,
        [0x5B] = 0x68, [0x5C] = 0x69, [0x43] = 0x6A, [0x45] = 0x6B, [0x4E] = 0x6D, [0x41] = 0x6E, [0x4B] = 0x6F, [0x4C] = 0x0D,
        [0x47] = 0x0C,
        [0x31] = 0x20, [0x24] = 0x0D, [0x30] = 0x09, [0x35] = 0x1B, [0x33] = 0x08, [0x75] = 0x2E, [0x72] = 0x2D,
        [0x73] = 0x24, [0x77] = 0x23, [0x74] = 0x21, [0x79] = 0x22, [0x7B] = 0x25, [0x7E] = 0x26, [0x7C] = 0x27, [0x7D] = 0x28,
        [0x29] = 0xBA, [0x18] = 0xBB, [0x2B] = 0xBC, [0x1B] = 0xBD, [0x2F] = 0xBE, [0x2C] = 0xBF, [0x32] = 0xC0,
        [0x21] = 0xDB, [0x2A] = 0xDC, [0x1E] = 0xDD, [0x27] = 0xDE, [0x39] = 0x14,
        // Modifiers: Shift, Command -> Ctrl, Option -> Alt, Control -> Win (left/right).
        [0x38] = 0xA0, [0x3C] = 0xA1, [0x37] = 0xA2, [0x36] = 0xA3, [0x3A] = 0xA4, [0x3D] = 0xA5, [0x3B] = 0x5B, [0x3E] = 0x5C,
    };
    private static readonly Dictionary<int, ushort> FromWindows = ToWindows
        .GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);

    public static int? ToVirtualKey(ushort keyCode) => ToWindows.TryGetValue(keyCode, out int vk) ? vk : null;
    public static ushort? ToMac(int virtualKey) => FromWindows.TryGetValue(virtualKey, out ushort code) ? code : null;

    // NX device-dependent modifier bits for left/right keys.
    public static bool IsModifierDown(ushort keyCode, ulong flags) => keyCode switch
    {
        0x38 => (flags & 0x2) != 0, 0x3C => (flags & 0x4) != 0,
        0x37 => (flags & 0x8) != 0, 0x36 => (flags & 0x10) != 0,
        0x3A => (flags & 0x20) != 0, 0x3D => (flags & 0x40) != 0,
        0x3B => (flags & 0x1) != 0, 0x3E => (flags & 0x2000) != 0,
        0x39 => (flags & 0x10000) != 0,
        _ => false,
    };

    /// <summary>Seeds held modifiers when bindings change (Windows virtual-key numbering).</summary>
    public static bool IsVirtualKeyDown(int virtualKey)
    {
        ulong flags = MacNative.ModifierFlags();
        return virtualKey switch
        {
            0xA0 => (flags & 0x2) != 0, 0xA1 => (flags & 0x4) != 0,
            0xA2 => (flags & 0x8) != 0, 0xA3 => (flags & 0x10) != 0,
            0xA4 => (flags & 0x20) != 0, 0xA5 => (flags & 0x40) != 0,
            0x5B => (flags & 0x1) != 0, 0x5C => (flags & 0x2000) != 0,
            _ => false,
        };
    }

    /// <summary>Command/Option/Shift as the shared ShortcutKeys modifiers (Command = Control).</summary>
    public static ShortcutKeys Modifiers(ulong flags)
    {
        var keys = ShortcutKeys.None;
        if ((flags & 0x20000) != 0) keys |= ShortcutKeys.Shift;
        if ((flags & 0x100000) != 0) keys |= ShortcutKeys.Control;
        if ((flags & 0x80000) != 0) keys |= ShortcutKeys.Alt;
        return keys;
    }
}

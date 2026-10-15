using System;
using EveOPreview.Mac.Native;

namespace EveOPreview.Services.Interop;

/// <summary>
/// macOS stand-in for the few user32 calls that shared preview views make directly. Handles are either
/// this host's NSWindow pointers (previews, overlays, menus) or CGWindowIDs of client windows; pointers
/// are always above the 32-bit window-id range on arm64.
/// </summary>
static class User32NativeMethods
{
    private static bool IsOwnWindow(IntPtr handle) => (ulong)handle.ToInt64() > uint.MaxValue;

    public static bool IsIconic(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;
        if (IsOwnWindow(handle)) return MacNative.IsOwnWindowMinimized(handle) != 0;
        var client = Services.Implementation.MacProcessMonitor.Current?.LookupCachedProcessByWindowHandle(handle);
        return client is Services.Implementation.MacProcessInfo info
            && (MacNative.IsApplicationHidden(info.ProcessId) != 0 || MacNative.IsWindowMinimized(info.ProcessId, (uint)handle) != 0);
    }

    public static bool ShowWindow(IntPtr handle, int command)
    {
        if (!IsOwnWindow(handle)) return false;
        return MacNative.OrderWindowFront(handle, 0) != 0;
    }

    public static bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
    {
        if (!IsOwnWindow(handle)) return false;
        // Only z-order requests reach this shim (SWP_NOMOVE | SWP_NOSIZE); -1 is HWND_TOPMOST.
        return MacNative.OrderWindowFront(handle, insertAfter == new IntPtr(-1) ? 1 : 0) != 0;
    }

    /// <summary>GW_HWNDFIRST (0) and GW_HWNDNEXT (2) over this application's windows, front to back.</summary>
    public static IntPtr GetWindow(IntPtr handle, uint command)
    {
        var windows = MacNative.OwnWindowsFrontToBack();
        if (windows.Length == 0) return IntPtr.Zero;
        if (command == 0) return windows[0];
        if (command != 2) return IntPtr.Zero;
        var own = MacNative.WindowFromHandle(handle);
        int index = Array.IndexOf(windows, own);
        return index >= 0 && index + 1 < windows.Length ? windows[index + 1] : IntPtr.Zero;
    }

    public static bool GetClientRect(IntPtr handle, out RECT rectangle)
    {
        rectangle = default;
        if (handle == IntPtr.Zero) return false;
        if (IsOwnWindow(handle))
        {
            if (!MacNative.TryGetOwnWindowFrames(handle, out _, out var content, out _)) return false;
            rectangle = new RECT(0, 0, (int)Math.Round(content.Width), (int)Math.Round(content.Height));
            return true;
        }
        if (!MacNative.TryGetWindowBounds((uint)handle, out var bounds)) return false;
        rectangle = new RECT(0, 0, (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
        return true;
    }
}

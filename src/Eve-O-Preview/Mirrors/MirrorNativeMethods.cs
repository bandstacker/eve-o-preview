using System;
using System.Runtime.InteropServices;
using EveOPreview.Services.Interop;

namespace EveOPreview.Mirrors;

/// <summary>Win32 calls used only by UI mirrors (pointer capture, cursors and client-area geometry).</summary>
internal static class MirrorNativeMethods
{
    public const uint WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
        WM_RBUTTONDBLCLK = 0x0206, WM_CAPTURECHANGED = 0x0215, WM_MOUSELEAVE = 0x02A3;
    public const int HTCLIENT = 1, MA_NOACTIVATE = 3;
    public const int IDC_ARROW = 32512, IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643, IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_SIZEALL = 32646;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr GetCapture();
    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);

    public static (int X, int Y) CursorPosition() => GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    /// <summary>The client area of a window in desktop pixels, or null when unavailable.</summary>
    public static System.Drawing.Rectangle? ClientScreenBounds(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || IsIconic(window) || !GetClientRect(window, out var client)) return null;
        var origin = new POINT();
        if (!ClientToScreen(window, ref origin)) return null;
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        return width > 0 && height > 0 ? new(origin.X, origin.Y, width, height) : null;
    }

    public static void SetStandardCursor(int id) => SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(id)));
}

using System;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EveOPreview.Mac.Native;

/// <summary>Bindings for libEveOMacNative (src/Eve-O-Preview.Mac.Native). Geometry is global Quartz points.</summary>
internal static unsafe partial class MacNative
{
    private const string Library = "EveOMacNative";

    [LibraryImport(Library, EntryPoint = "eom_set_log_callback")]
    public static partial void SetLogCallback(delegate* unmanaged<int, byte*, void> callback);
    [LibraryImport(Library, EntryPoint = "eom_free")]
    public static partial void Free(void* pointer);

    // Windows of other processes
    [LibraryImport(Library, EntryPoint = "eom_list_windows")]
    private static partial byte* ListWindowsJson(int includeAccessibilityTitles);
    [LibraryImport(Library, EntryPoint = "eom_list_applications")]
    private static partial byte* ListApplicationsJson();
    [LibraryImport(Library, EntryPoint = "eom_window_pid")]
    public static partial int WindowPid(uint windowId);
    [LibraryImport(Library, EntryPoint = "eom_window_title")]
    private static partial byte* WindowTitleUtf8(int pid, uint windowId);
    [LibraryImport(Library, EntryPoint = "eom_forget_process")]
    public static partial void ForgetProcess(int pid);
    [LibraryImport(Library, EntryPoint = "eom_window_bounds")]
    public static partial int WindowBounds(uint windowId, double* rect);
    [LibraryImport(Library, EntryPoint = "eom_frontmost_pid")]
    public static partial int FrontmostPid();
    [LibraryImport(Library, EntryPoint = "eom_frontmost_window")]
    public static partial uint FrontmostWindow(int* pid);
    [LibraryImport(Library, EntryPoint = "eom_own_key_window")]
    public static partial IntPtr OwnKeyWindow();
    [LibraryImport(Library, EntryPoint = "eom_activate_window")]
    public static partial int ActivateWindow(int pid, uint windowId);
    [LibraryImport(Library, EntryPoint = "eom_hide_application")]
    public static partial int HideApplication(int pid);
    [LibraryImport(Library, EntryPoint = "eom_unhide_application")]
    public static partial int UnhideApplication(int pid);
    [LibraryImport(Library, EntryPoint = "eom_is_application_hidden")]
    public static partial int IsApplicationHidden(int pid);
    [LibraryImport(Library, EntryPoint = "eom_minimize_window")]
    public static partial int MinimizeWindow(int pid, uint windowId, int minimize);
    [LibraryImport(Library, EntryPoint = "eom_is_window_minimized")]
    public static partial int IsWindowMinimized(int pid, uint windowId);
    [LibraryImport(Library, EntryPoint = "eom_set_window_frame")]
    public static partial int SetWindowFrame(int pid, uint windowId, double x, double y, double width, double height);
    [LibraryImport(Library, EntryPoint = "eom_maximize_window")]
    public static partial int MaximizeWindow(int pid, uint windowId);

    // Permissions: 0 screen recording, 1 accessibility, 2 audio capture settings pane
    [LibraryImport(Library, EntryPoint = "eom_permission_granted")]
    public static partial int PermissionGranted(int kind);
    [LibraryImport(Library, EntryPoint = "eom_request_permission")]
    public static partial void RequestPermission(int kind);

    [LibraryImport(Library, EntryPoint = "eom_list_screens")]
    private static partial byte* ListScreensJson();

    // Own windows
    [LibraryImport(Library, EntryPoint = "eom_window_configure")]
    public static partial int ConfigureWindow(IntPtr handle, int clickThrough, int nonactivating);
    [LibraryImport(Library, EntryPoint = "eom_window_set_alpha")]
    public static partial void SetWindowAlpha(IntPtr handle, double alpha);
    [LibraryImport(Library, EntryPoint = "eom_window_order_front")]
    public static partial int OrderWindowFront(IntPtr handle, int topmost);
    [LibraryImport(Library, EntryPoint = "eom_window_order_above")]
    public static partial void OrderWindowAbove(IntPtr handle, IntPtr relativeTo);
    [LibraryImport(Library, EntryPoint = "eom_window_is_minimized")]
    public static partial int IsOwnWindowMinimized(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "eom_window_is_visible")]
    public static partial int IsOwnWindowVisible(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "eom_window_from_handle")]
    public static partial IntPtr WindowFromHandle(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "eom_window_frames")]
    public static partial int WindowFrames(IntPtr handle, double* frame, double* content, double* scale);
    [LibraryImport(Library, EntryPoint = "eom_window_set_content_frame")]
    public static partial int SetWindowContentFrame(IntPtr handle, double x, double y, double width, double height, int setOrigin, int setSize);
    [LibraryImport(Library, EntryPoint = "eom_own_windows_front_to_back")]
    private static partial IntPtr* OwnWindowsFrontToBack(int* count);
    [LibraryImport(Library, EntryPoint = "eom_set_accessory_policy")]
    public static partial void SetAccessoryPolicy(int accessory);
    [LibraryImport(Library, EntryPoint = "eom_activate_self")]
    public static partial void ActivateSelf();

    // Live previews
    [LibraryImport(Library, EntryPoint = "eom_thumbnail_create")]
    public static partial long ThumbnailCreate(IntPtr host, uint sourceWindowId);
    [LibraryImport(Library, EntryPoint = "eom_thumbnail_set_bounds")]
    public static partial void ThumbnailSetBounds(long id, double x, double y, double width, double height);
    [LibraryImport(Library, EntryPoint = "eom_thumbnail_set_frame_rate")]
    public static partial void ThumbnailSetFrameRate(long id, int fps);
    [LibraryImport(Library, EntryPoint = "eom_thumbnail_set_source_rect")]
    public static partial void ThumbnailSetSourceRect(long id, double x, double y, double width, double height);
    [LibraryImport(Library, EntryPoint = "eom_thumbnail_set_crop_top")]
    public static partial void ThumbnailSetCropTop(long id, double points);
    [LibraryImport(Library, EntryPoint = "eom_thumbnail_update")]
    public static partial int ThumbnailUpdate(long id);
    [LibraryImport(Library, EntryPoint = "eom_thumbnail_destroy")]
    public static partial void ThumbnailDestroy(long id);
    [LibraryImport(Library, EntryPoint = "eom_capture_main_display_png")]
    private static partial byte* CaptureMainDisplayPng(int* length);
    [LibraryImport(Library, EntryPoint = "eom_capture_window_png")]
    private static partial byte* CaptureWindowPng(uint windowId, int maxWidth, int maxHeight, int* length);

    // Local alerts
    [LibraryImport(Library, EntryPoint = "eom_sampler_create")]
    public static partial long SamplerCreate(uint windowId, double x, double y, double width, double height);
    [LibraryImport(Library, EntryPoint = "eom_sampler_read")]
    public static partial int SamplerRead(long id, byte* target, int capacity, long known, int* width, int* height, int* stride, long* sequence, double* scale);
    [LibraryImport(Library, EntryPoint = "eom_sampler_destroy")]
    public static partial void SamplerDestroy(long id);
    [LibraryImport(Library, EntryPoint = "eom_recognize_text")]
    private static partial byte* RecognizeTextUtf8(byte* pixels, int width, int height, int stride);
    [LibraryImport(Library, EntryPoint = "eom_play_sound")]
    private static partial void PlaySound(byte* data, int length, double volume);

    public static string RecognizeText(byte[] pixels, int width, int height, int stride)
    {
        fixed (byte* data = pixels)
        {
            byte* text = RecognizeTextUtf8(data, width, height, stride);
            if (text == null) return null;
            try { return System.Text.Encoding.UTF8.GetString(text, NullTerminatedLength(text)); }
            finally { Free(text); }
        }
    }

    public static void PlaySound(byte[] data, double volume)
    {
        if (data is not { Length: > 0 }) return;
        fixed (byte* bytes = data) PlaySound(bytes, data.Length, volume);
    }

    // Input
    [LibraryImport(Library, EntryPoint = "eom_key_tap_start")]
    public static partial int KeyTapStart(delegate* unmanaged<int, ushort, ulong, int, int> callback);
    [LibraryImport(Library, EntryPoint = "eom_key_tap_stop")]
    public static partial void KeyTapStop();
    [LibraryImport(Library, EntryPoint = "eom_pointer_tap_start")]
    public static partial int PointerTapStart(delegate* unmanaged<int, int, double, double, ulong, void> callback);
    [LibraryImport(Library, EntryPoint = "eom_pointer_tap_stop")]
    public static partial void PointerTapStop();
    [LibraryImport(Library, EntryPoint = "eom_pointer_location")]
    public static partial void PointerLocation(double* x, double* y);
    [LibraryImport(Library, EntryPoint = "eom_pointer_warp")]
    public static partial void PointerWarp(double x, double y);
    [LibraryImport(Library, EntryPoint = "eom_pressed_buttons")]
    public static partial int PressedButtons();
    [LibraryImport(Library, EntryPoint = "eom_modifier_flags")]
    public static partial ulong ModifierFlags();
    [LibraryImport(Library, EntryPoint = "eom_post_key")]
    public static partial void PostKey(ushort keyCode, ulong flags);
    [LibraryImport(Library, EntryPoint = "eom_post_mouse")]
    public static partial void PostMouse(int kind, double x, double y, double x2, double y2, ulong flags);
    [LibraryImport(Library, EntryPoint = "eom_debug_monitor_mouse")]
    public static partial void DebugMonitorMouse();
    [LibraryImport(Library, EntryPoint = "eom_debug_describe_windows")]
    private static partial byte* DebugDescribeWindowsUtf8();
    public static string DebugDescribeWindows()
    {
        byte* text = DebugDescribeWindowsUtf8();
        if (text == null) return "";
        try { return System.Text.Encoding.UTF8.GetString(text, NullTerminatedLength(text)); }
        finally { Free(text); }
    }
    [LibraryImport(Library, EntryPoint = "eom_observe_applications")]
    public static partial void ObserveApplications(delegate* unmanaged<int, void> callback);

    // Audio and scheduling policy
    [LibraryImport(Library, EntryPoint = "eom_audio_set_muted")]
    public static partial int AudioSetMuted(int pid, int muted);
    [LibraryImport(Library, EntryPoint = "eom_audio_is_muted")]
    public static partial int AudioIsMuted(int pid);
    [LibraryImport(Library, EntryPoint = "eom_audio_unmute_all")]
    public static partial void AudioUnmuteAll();
    [LibraryImport(Library, EntryPoint = "eom_set_background_policy")]
    public static partial int SetBackgroundPolicy(int pid, int background);

    // Typed wrappers

    public sealed record WindowEntry(uint Id, int Pid, string Title, string Owner, string BundleId, string Executable,
        double X, double Y, double Width, double Height, int Layer, bool OnScreen, bool Hidden, bool Minimized);
    public sealed record ApplicationEntry(int Pid, string Name, string BundleId, string Executable, bool Regular);
    public sealed record ScreenEntry(uint Id, string Name, double X, double Y, double Width, double Height,
        double VisibleX, double VisibleY, double VisibleWidth, double VisibleHeight, double Scale, bool Primary);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static T TakeJson<T>(byte* text, T fallback)
    {
        if (text == null) return fallback;
        try { return JsonSerializer.Deserialize<T>(new ReadOnlySpan<byte>(text, NullTerminatedLength(text)), Json) ?? fallback; }
        finally { Free(text); }
    }

    private static int NullTerminatedLength(byte* text) { int n = 0; while (text[n] != 0) n++; return n; }

    public static WindowEntry[] ListWindows(bool accessibilityTitles) => TakeJson(ListWindowsJson(accessibilityTitles ? 1 : 0), Array.Empty<WindowEntry>());
    public static ApplicationEntry[] ListApplications() => TakeJson(ListApplicationsJson(), Array.Empty<ApplicationEntry>());
    public static ScreenEntry[] ListScreens() => TakeJson(ListScreensJson(), Array.Empty<ScreenEntry>());

    public static string WindowTitle(int pid, uint windowId)
    {
        byte* text = WindowTitleUtf8(pid, windowId);
        if (text == null) return null;
        try { return System.Text.Encoding.UTF8.GetString(text, NullTerminatedLength(text)); }
        finally { Free(text); }
    }

    public static bool TryGetWindowBounds(uint windowId, out (double X, double Y, double Width, double Height) bounds)
    {
        double* rect = stackalloc double[4];
        bool ok = WindowBounds(windowId, rect) != 0;
        bounds = (rect[0], rect[1], rect[2], rect[3]);
        return ok;
    }

    public static bool TryGetOwnWindowFrames(IntPtr handle, out (double X, double Y, double Width, double Height) frame,
        out (double X, double Y, double Width, double Height) content, out double scale)
    {
        double* outer = stackalloc double[4];
        double* inner = stackalloc double[4];
        double factor = 1;
        bool ok = handle != IntPtr.Zero && WindowFrames(handle, outer, inner, &factor) != 0;
        frame = (outer[0], outer[1], outer[2], outer[3]);
        content = (inner[0], inner[1], inner[2], inner[3]);
        scale = ok ? factor : 1;
        return ok;
    }

    public static IntPtr[] OwnWindowsFrontToBack()
    {
        int count = 0;
        IntPtr* list = OwnWindowsFrontToBack(&count);
        if (list == null) return Array.Empty<IntPtr>();
        try { return new ReadOnlySpan<IntPtr>(list, count).ToArray(); }
        finally { Free(list); }
    }

    public static byte[] CaptureWindow(uint windowId, int maxWidth, int maxHeight)
    {
        int length = 0;
        byte* data = CaptureWindowPng(windowId, maxWidth, maxHeight, &length);
        if (data == null || length <= 0) return null;
        try { return new ReadOnlySpan<byte>(data, length).ToArray(); }
        finally { Free(data); }
    }

    public static byte[] CaptureMainDisplay()
    {
        int length = 0;
        byte* data = CaptureMainDisplayPng(&length);
        if (data == null || length <= 0) return null;
        try { return new ReadOnlySpan<byte>(data, length).ToArray(); }
        finally { Free(data); }
    }

    public static (double X, double Y) PointerLocation()
    {
        double x = 0, y = 0;
        PointerLocation(&x, &y);
        return (x, y);
    }

    public static (uint WindowId, int Pid) FrontmostWindow()
    {
        int pid = 0;
        uint id = FrontmostWindow(&pid);
        return (id, pid);
    }
}

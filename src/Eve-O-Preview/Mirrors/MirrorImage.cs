using System;
using System.Runtime.InteropServices;
using EveOPreview.Services.Interop;
using Serilog;

namespace EveOPreview.Mirrors;

/// <summary>
/// A DWM thumbnail relationship showing one rectangle of the source client area. DWM composes the
/// image live, so mirrors cost no capture or copying; rcSource selects the mirrored area.
/// </summary>
internal sealed class MirrorImage : IDisposable
{
    private readonly ILogger _logger;
    private readonly DWM_THUMBNAIL_PROPERTIES _properties = new()
    {
        dwFlags = DWM_TNP_CONSTANTS.DWM_TNP_VISIBLE | DWM_TNP_CONSTANTS.DWM_TNP_OPACITY | DWM_TNP_CONSTANTS.DWM_TNP_RECTDESTINATION
            | DWM_TNP_CONSTANTS.DWM_TNP_RECTSOURCE | DWM_TNP_CONSTANTS.DWM_TNP_SOURCECLIENTAREAONLY,
        opacity = 255, fVisible = true, fSourceClientAreaOnly = true,
    };
    private IntPtr _handle;

    public MirrorImage(ILogger logger) { _logger = logger; }

    public bool IsRegistered => _handle != IntPtr.Zero;

    public bool Register(IntPtr destination, IntPtr source)
    {
        Unregister();
        try { _handle = DwmNativeMethods.DwmRegisterThumbnail(destination, source); }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            _logger.Debug(ex, "UI mirror could not register its image");
            _handle = IntPtr.Zero;
        }
        return _handle != IntPtr.Zero && Update();
    }

    public void SetSource(double x, double y, double width, double height) => _properties.rcSource = new RECT(
        (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(x + width), (int)Math.Round(y + height));

    public void SetDestination(int left, int top, int right, int bottom) => _properties.rcDestination = new RECT(left, top, right, bottom);

    /// <summary>Applies the properties; false when the source window closed or DWM restarted.</summary>
    public bool Update()
    {
        if (_handle == IntPtr.Zero) return false;
        try { DwmNativeMethods.DwmUpdateThumbnailProperties(_handle, _properties); return true; }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            _logger.Verbose(ex, "UI mirror image update failed");
            return false;
        }
    }

    public void Unregister()
    {
        if (_handle == IntPtr.Zero) return;
        try { DwmNativeMethods.DwmUnregisterThumbnail(_handle); }
        catch (Exception ex) when (ex is ArgumentException or COMException) { _logger.Verbose(ex, "UI mirror image release failed"); }
        _handle = IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}

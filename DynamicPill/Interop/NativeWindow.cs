namespace DynamicPill.Interop;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static DynamicPill.Interop.NativeMethods;

public sealed class NativeWindow
{
    private readonly Window _window;
    private IntPtr _hwnd;
    private bool _isClickThrough;

    public NativeWindow(Window window)
    {
        _window = window;
    }

    public IntPtr Handle => _hwnd;

    public void Initialize()
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        _isClickThrough = true;
    }

    public void SetClickThrough(bool enable)
    {
        if (_hwnd == IntPtr.Zero || _isClickThrough == enable)
            return;

        long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        if (enable)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;

        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        _isClickThrough = enable;
    }

    public void PositionTopCenter(double windowWidthDip = 480, double windowHeightDip = 130, double topOffsetDip = 0)
    {
        GetCursorPos(out POINT cursorPos);
        IntPtr hMonitor = MonitorFromPoint(cursorPos, MONITOR_DEFAULTTONEAREST);

        MONITORINFO mi = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref mi);

        // Get DPI for active monitor (0 = MDT_EFFECTIVE_DPI)
        int hr = GetDpiForMonitor(hMonitor, 0, out uint dpiX, out uint _);
        double monitorScale = (hr == 0 && dpiX > 0) ? (dpiX / 96.0) : 1.0;

        double monitorLeft = mi.rcMonitor.Left;
        double monitorTop = mi.rcMonitor.Top;
        double monitorWidth = mi.rcMonitor.Right - mi.rcMonitor.Left;

        // Calculate physical pixels
        double physicalWidth = windowWidthDip * monitorScale;
        double leftPhysical = monitorLeft + (monitorWidth - physicalWidth) / 2.0;
        double topPhysical = monitorTop + (topOffsetDip * monitorScale);

        // Convert to WPF device-independent coordinates based on composition target
        var source = PresentationSource.FromVisual(_window);
        double wpfScaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? (1.0 / monitorScale);
        double wpfScaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? (1.0 / monitorScale);

        _window.Left = leftPhysical * wpfScaleX;
        _window.Top = topPhysical * wpfScaleY;
        _window.Width = windowWidthDip;
        _window.Height = windowHeightDip;
    }

    public bool EnableAcrylic(uint tintColorAABBGGRR = 0x011C1C20)
    {
        // On Windows 10 & 11, SetWindowCompositionAttribute blurs the ENTIRE rectangular HWND,
        // which creates an unsightly hard-edged rectangular box around non-rectangular (pill) windows.
        // Modern WPF per-pixel layered window alpha renders the clean rounded pill without any bounding rectangle.
        return true;
    }

    public static bool ShouldSuppressNotifications(bool isExplicitTrigger = false)
    {
        if (isExplicitTrigger)
            return false;

        int hr = SHQueryUserNotificationState(out var state);
        if (hr != 0) return false;

        App.Log($"[NativeWindow] SHQueryUserNotificationState = {state}");

        return state switch
        {
            QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN => true,
            QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE => true,
            _ => false
        };
    }
}

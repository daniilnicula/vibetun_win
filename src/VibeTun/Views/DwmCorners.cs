using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VibeTun.Views;

/// <summary>Системные скруглённые углы Windows 11 (DWMWA_WINDOW_CORNER_PREFERENCE).</summary>
internal static class DwmCorners
{
    public static bool IsWindows11 { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE = ROUND. Вне Windows 11 молча ничего не делает.</summary>
    public static void ApplyRound(Window window)
    {
        if (!IsWindows11) return;
        try
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* DWM недоступен — оставляем системное оформление */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

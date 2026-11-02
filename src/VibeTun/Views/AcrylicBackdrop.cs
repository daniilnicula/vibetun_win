using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VibeTun.Views;

/// <summary>
/// Системный акриловый фон окна (размытие того, что находится за окном). Работает на Windows 10 1803+
/// и на Windows 11. Если система не поддерживает — возвращает false, и окно остаётся непрозрачным.
/// </summary>
internal static class AcrylicBackdrop
{
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;

    public static bool Apply(Window window)
    {
        if (Environment.OSVersion.Version < new Version(10, 0, 17134)) return false;
        // GradientColor в формате AABBGGRR; почти прозрачный — цвет задаёт тема поверх
        return SetAccent(window, AccentEnableAcrylicBlurBehind, 2, 0x01000000);
    }

    private static bool SetAccent(Window window, int state, int flags, uint gradient)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var accent = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = flags,
                GradientColor = gradient,
            };
            var size = Marshal.SizeOf<AccentPolicy>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = ptr,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    /// <summary>
    /// Скругление углов через регион окна: акриловый фон не обрезается WPF-клипом, поэтому углы
    /// окна задаём самим HWND. radius = 0 — снять регион (развёрнутое окно).
    /// </summary>
    public static void SetCornerRadius(Window window, double radiusDip)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (radiusDip <= 0)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }
            // размер ещё не известен (окно не разложено) — не создаём регион 1×1
            if (window.ActualWidth <= 0 || window.ActualHeight <= 0) return;

            var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            var width = (int)Math.Round(window.ActualWidth * scale) + 1;
            var height = (int)Math.Round(window.ActualHeight * scale) + 1;
            var diameter = (int)Math.Round(radiusDip * 2 * scale);
            var region = CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
            // после SetWindowRgn система сама владеет регионом, удалять его не нужно
            SetWindowRgn(hwnd, region, true);
        }
        catch
        {
            // без региона окно остаётся прямоугольным — это не ломает работу
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}

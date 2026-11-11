using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VibeTun.Views;

/// <summary>
/// Системный бэкдроп окна (материал за прозрачными участками окна).
/// Windows 11 получает Mica через DWMWA_SYSTEMBACKDROP_TYPE — тёмную или светлую по теме
/// приложения (вариант выбирается атрибутом DWMWA_USE_IMMERSIVE_DARK_MODE; без него mica
/// всегда светлая). Windows 10 получает accent acrylic: он даёт живое тёмное размытие,
/// но только в layered-окне (AllowsTransparency, см. MainWindow) — в обычном окне рисует
/// сплошную светлую заливку. Запасной вариант на обеих — то, что поддерживает система;
/// если ничего не применилось, возвращает false и окно остаётся непрозрачным.
/// </summary>
public static class AcrylicBackdrop
{
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;

    private const int DwmwaUseImmersiveDarkMode = 19;  // вариант mica: 1 — тёмная, 0 — светлая
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;            // mica: тонируется от обоев (в тёмной теме — тёмная)
    private const int DwmsbtTransientWindow = 3;       // acrylic: официальный бэкдроп Windows 11 (светлый)

    public static bool Apply(Window window)
    {
        if (Environment.OSVersion.Version < new Version(10, 0, 17134)) return false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return ApplyWin11(window);
        // GradientColor в формате AABBGGRR; почти прозрачный — цвет задаёт тема поверх.
        // Важно: живой акрил рисует размытие только в layered-окне (см. MainWindow).
        return SetAccent(window, AccentEnableAcrylicBlurBehind, 2, 0x01000000);
    }

    /// <summary>
    /// Windows 10: включить или снять живой акрил у окна, которому он уже назначался в <see cref="Apply"/>.
    /// Композитор пересчитывает такое размытие на каждый сдвиг окна, поэтому акрил включён, только
    /// пока он действительно виден: главное окно снимает его при выключенном «Размытии левой панели»
    /// (см. MainWindow.RenderGlass). При перетаскивании размытие остаётся — отставание окна снимает
    /// синхронизация кадра с DWM (MainWindow.SynchronizeFrameWithDwm), а не снятие акрила.
    /// На Windows 11 ничего не делает: там бэкдроп (mica) рисует сам DWM.
    /// </summary>
    public static bool SetLegacyAcrylic(Window window, bool enabled)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        return enabled
            ? SetAccent(window, AccentEnableAcrylicBlurBehind, 2, 0x01000000)
            : SetAccent(window, AccentDisabled, 0, 0);
    }

    /// <summary>Переключение темы во время работы: синхронизируем вариант mica (тёмный/светлый).</summary>
    public static void SyncTheme()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            if (Application.Current?.MainWindow is not { } window) return;
            SetDarkMode(window, IsDarkTheme());
        }
        catch { /* вид окна от этого не зависит */ }
    }

    private static bool ApplyWin11(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var dark = IsDarkTheme();
            SetDarkMode(hwnd, dark);

            if (SetSystemBackdrop(window, DwmsbtMainWindow)) return true;

            // запасные варианты: официальный acrylic (светлый по рецепту), затем старый accent API
            if (SetSystemBackdrop(window, DwmsbtTransientWindow)) return true;
            return SetAccent(window, AccentEnableAcrylicBlurBehind, 2, 0x01000000);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Тёмная тема приложения (Dark/Black/System+тёмная система): для неё mica тёмная.</summary>
    private static bool IsDarkTheme() => Services.ThemeService.EffectiveMode != Models.ThemeMode.Light;

    private static int SetDarkMode(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return -1;
        return SetDarkMode(hwnd, dark);
    }

    private static int SetDarkMode(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }


    private static bool SetSystemBackdrop(Window window, int backdropType)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
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

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

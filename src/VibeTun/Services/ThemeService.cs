using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using VibeTun.Views.Controls;
using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>Переключение светлой/тёмной темы и акцентного цвета во время работы приложения.</summary>
public static class ThemeService
{
    public static ThemeMode Current { get; private set; } = ThemeMode.Dark;

    // Словарь акцента лежит в конце MergedDictionaries и перекрывает кисти темы.
    private static ResourceDictionary? _accentDict;
    private static string? _accentHex;

    public static ThemeMode EffectiveMode { get; private set; } = ThemeMode.Dark;
    private static ResourceDictionary? _palette;
    private static bool _watchingSystem;

    public static void Apply(ThemeMode mode)
    {
        Current = mode;
        var app = Application.Current;
        if (app is null) return;
        if (!_watchingSystem)
        {
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            _watchingSystem = true;
        }
        EffectiveMode = mode == ThemeMode.System ? DeviceTheme() : mode;
        var name = EffectiveMode == ThemeMode.Light ? "Light" : EffectiveMode == ThemeMode.Black ? "Black" : "Dark";
        var target = new ResourceDictionary { Source = new Uri($"Themes/{name}.xaml", UriKind.Relative) };
        var replacement = new ResourceDictionary();
        foreach (var key in target.Keys)
        {
            var value = target[key];
            if (value is Brush brush)
            {
                var copy = brush.CloneCurrentValue();
                if (_palette is not null && _palette[key] is Brush previous)
                    AnimateBrush(copy, previous);
                value = copy;
            }
            replacement[key] = value;
        }
        var dicts = app.Resources.MergedDictionaries;
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (dicts[i] == _palette || src.Contains("Themes/Dark.xaml") ||
                src.Contains("Themes/Light.xaml") || src.Contains("Themes/Black.xaml"))
                dicts.RemoveAt(i);
        }
        _palette = replacement;
        dicts.Insert(0, replacement);
        ReapplyAccent(app);
    }

    private static void AnimateBrush(Brush target, Brush previous)
    {
        if (target is SolidColorBrush solid && previous is SolidColorBrush old)
            solid.BeginAnimation(SolidColorBrush.ColorProperty, Transition(old.Color, solid.Color));
        else if (target is GradientBrush gradient && previous is GradientBrush oldGradient)
            for (var i = 0; i < Math.Min(gradient.GradientStops.Count, oldGradient.GradientStops.Count); i++)
                gradient.GradientStops[i].BeginAnimation(GradientStop.ColorProperty,
                    Transition(oldGradient.GradientStops[i].Color, gradient.GradientStops[i].Color));
    }

    private static ColorAnimation Transition(Color from, Color to) => new(from, to, Motion.Slide)
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        FillBehavior = FillBehavior.Stop
    };

    private static ThemeMode DeviceTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch { return ThemeMode.Light; }
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Current == ThemeMode.System) Apply(Current);
        }));
    }

    public static void Shutdown()
    {
        if (_watchingSystem) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _watchingSystem = false;
    }

    /// <summary>Цвет акцента по умолчанию: #FF758AAC (для кнопки «по умолчанию» в пикере).</summary>
    public static Color DefaultAccent => Color.FromRgb(0x75, 0x8A, 0xAC);

    /// <summary>
    /// Применить пользовательский акцент поверх палитры темы (или убрать override, если
    /// <paramref name="hex"/> пуст). Прозрачность (AA) сохраняется как есть.
    /// </summary>
    public static void ApplyAccent(string? hex)
    {
        _accentHex = string.IsNullOrWhiteSpace(hex) ? null : hex;
        if (Application.Current is { } app) ReapplyAccent(app);
    }

    private static void ReapplyAccent(Application app)
    {
        var dicts = app.Resources.MergedDictionaries;

        if (_accentDict is not null)
        {
            dicts.Remove(_accentDict);
            _accentDict = null;
        }
        if (_accentHex is null) return;

        if (!TryParseColor(_accentHex, out var color)) return;

        var hover = Lighten(color, 0.14f);
        var pressed = Darken(color, 0.12f);
        var soft = Color.FromArgb(0x38, color.R, color.G, color.B);

        var dict = new ResourceDictionary
        {
            ["ColAccent"] = color,
            ["ColAccentHover"] = hover,
            ["ColAccentPressed"] = pressed,
            ["Accent"] = Freeze(new SolidColorBrush(color)),
            ["AccentHover"] = Freeze(new SolidColorBrush(hover)),
            ["AccentPressed"] = Freeze(new SolidColorBrush(pressed)),
            ["AccentSoft"] = Freeze(new SolidColorBrush(soft)),
            ["LogoGradient"] = MakeGradient(hover, pressed)
        };

        _accentDict = dict;
        dicts.Add(dict);   // в конце — перекрывает палитру темы
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush MakeGradient(Color start, Color end)
    {
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>#RRGGBB или #AARRGGBB (формат Color.ToString(), в нём цвет и хранится).</summary>
    public static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString("#" + s);
            if (s.Length == 6) color.A = 255;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Color Lighten(Color c, float amount) => Adjust(c, amount, lighter: true);
    private static Color Darken(Color c, float amount) => Adjust(c, amount, lighter: false);

    private static Color Adjust(Color c, float amount, bool lighter)
    {
        static byte Mix(byte ch, float k, bool lighter) => (byte)Math.Clamp((int)(lighter ? ch + (255 - ch) * k : ch * (1 - k)), 0, 255);
        return Color.FromArgb(c.A, Mix(c.R, amount, lighter), Mix(c.G, amount, lighter), Mix(c.B, amount, lighter));
    }
}

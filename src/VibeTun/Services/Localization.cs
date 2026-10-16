using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;
using VibeTun.ViewModels;

namespace VibeTun.Services;

/// <summary>Runtime UI language. Catalog keys are stable and never used as persisted model values.</summary>
public sealed class Localization : INotifyPropertyChanged
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalog = LoadCatalog();
    private static readonly Dictionary<string, string> TextKeys = BuildTextKeys();
    private static readonly List<WeakReference<ObservableObject>> Observers = new();
    public static Localization Instance { get; } = new();
    public static string Language { get; private set; } = "ru";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "ru-RU");
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? Changed;
    public string this[string key] => T(key);

    public static string T(string key) => Catalog.TryGetValue(key, out var values) ? values[Language] : key;
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    // Only for built-in, cached labels. Never apply to user names, addresses, paths or configuration.
    public static string Text(string text) => TextKeys.TryGetValue(text, out var key) ? T(key) : text;

    public static void Observe(ObservableObject observer)
    {
        lock (Observers)
        {
            if (Observers.Count % 256 == 0) Observers.RemoveAll(reference => !reference.TryGetTarget(out _));
            Observers.Add(new WeakReference<ObservableObject>(observer));
        }
    }

    public static void SetLanguage(string language)
    {
        language = language == "en" ? "en" : "ru";
        var changed = Language != language;
        Language = language;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        if (!changed) return;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        List<ObservableObject> live = new();
        lock (Observers)
        {
            Observers.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in Observers)
                if (reference.TryGetTarget(out var observer)) live.Add(observer);
        }
        foreach (var observer in live) observer.RefreshLanguage();
        Changed?.Invoke();
    }

    private static Dictionary<string, Dictionary<string, string>> LoadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VibeTun.Localization.strings.json")
            ?? throw new InvalidOperationException("Missing UI translation catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
    }

    private static Dictionary<string, string> BuildTextKeys()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Catalog)
            foreach (var text in entry.Value.Values) result.TryAdd(text, entry.Key);
        return result;
    }
}

/// <summary>Live translation binding, including inside templates, styles and dialogs.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension(string key) => Key = key;
    public string Key { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"[{Key}]")
    {
        Source = Localization.Instance,
        Mode = BindingMode.OneWay
    }.ProvideValue(serviceProvider);
}

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace ExternalDrivePanel;

/// <summary>App-owned text only. Paths, volume labels and Windows shell text are never translated.</summary>
public sealed class Localization : INotifyPropertyChanged
{
    public sealed record Translation(string Id, string Korean, string English);
    private static readonly Translation[] Catalog = Load();
    private static readonly IReadOnlyDictionary<string, Translation> BySource = Catalog.ToDictionary(item => item.Korean, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, Translation> ById = Catalog.ToDictionary(item => item.Id, StringComparer.Ordinal);
    public static Localization Instance { get; } = new();
    public static IReadOnlyList<Translation> Translations => Catalog;
    public static string Language { get; private set; } = "ko";
    public string Code => Language;
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event EventHandler? LanguageChanged;
    public string this[string id] => ById.TryGetValue(id, out var item) ? Select(item) : T(id);
    public static string Normalize(string? language) => language == "en" ? "en" : "ko";
    public static string T(string source) => BySource.TryGetValue(source, out var item) ? Select(item) : source;
    public static string Format(string source, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, T(source), arguments);
    private static string Select(Translation item) => Language == "en" ? item.English : item.Korean;
    public static void SetLanguage(string? language)
    {
        language = Normalize(language);
        if (Language == language) return;
        Language = language;
        // Keep the user's date/number formats. Shell and UAC remain Windows-owned.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language == "en" ? "en-US" : "ko-KR");
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(null));
        LanguageChanged?.Invoke(Instance, EventArgs.Empty);
    }
    public static void Attach(Window window, Action refresh)
    {
        EventHandler changed = (_, _) => refresh();
        LanguageChanged += changed;
        window.Closed += (_, _) => LanguageChanged -= changed;
    }
    public static void Bind(DependencyObject target, DependencyProperty property, Func<string> text) =>
        BindingOperations.SetBinding(target, property, new Binding(nameof(Code))
        { Source = Instance, Mode = BindingMode.OneWay, Converter = new TextConverter(text) });
    private sealed class TextConverter(Func<string> text) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => text();
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private static Translation[] Load()
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("ExternalDrivePanel.Languages.strings.json")
            ?? throw new InvalidDataException("Missing app language catalog.");
        return JsonSerializer.Deserialize<Translation[]>(stream) ?? throw new InvalidDataException("Invalid app language catalog.");
    }
}

/// <summary>Explicit source avoids visual-tree lookups, including DataGrid columns and shared styles.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TranslateExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = Localization.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

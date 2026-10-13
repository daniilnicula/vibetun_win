using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GoreBox.Utils;

/// <summary>Строка Markdown → готовые блоки (TextBlock'и) для ItemsControl в описании релиза.</summary>
public sealed class MarkdownToBlocksConv : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string md ? MarkdownBlocks.Render(md) : Array.Empty<FrameworkElement>();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

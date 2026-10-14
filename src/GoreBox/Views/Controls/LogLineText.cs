using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using GoreBox.Services;

namespace GoreBox.Views.Controls;

/// <summary>
/// Одна строка журнала из цветных фрагментов (<see cref="LogSegment"/>): ключевые данные — цветом акцента.
/// Цвета — ключи кистей темы, поэтому при смене темы строки перекрашиваются сами.
/// </summary>
public sealed class LogLineText : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable<LogSegment>), typeof(LogLineText),
        new PropertyMetadata(null, (d, _) => ((LogLineText)d).Rebuild()));

    public IEnumerable<LogSegment>? Segments
    {
        get => (IEnumerable<LogSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        if (Segments is null) return;

        foreach (var segment in Segments)
        {
            var run = new Run(segment.Text);
            run.SetResourceReference(TextElement.ForegroundProperty, BrushKeyOf(segment.Tone));
            Inlines.Add(run);
        }
    }

    private static string BrushKeyOf(LogTone tone) => tone switch
    {
        LogTone.Key => "Accent",
        LogTone.Warn => "Warning",
        LogTone.Danger => "Danger",
        LogTone.Muted => "TextMuted",
        _ => "TextPrimary",
    };
}

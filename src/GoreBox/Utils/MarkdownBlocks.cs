using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GoreBox.Utils;

/// <summary>
/// Минимальный рендерер Markdown для описаний релизов с GitHub.
/// Поддерживает: заголовки #/##/###, списки (-, *, •, 1.), жирный **/__,
/// курсив */_, зачёркивание ~~, инлайн-код `, ссылки [текст](url) и &lt;url&gt;.
/// Возвращает готовые TextBlock'и; цвета привязаны к ресурсам тем через
/// SetResourceReference, поэтому переключение тёмной/светлой темы работает.
/// </summary>
public static class MarkdownBlocks
{
    private sealed class Seg
    {
        public string Text = "";
        public bool Bold, Italic, Code, Strike;
        public string? Url;          // не null — сегмент является ссылкой
        public bool UrlAuto;         // ссылка вида <http://...> — текст = url
    }

    public static IReadOnlyList<FrameworkElement> Render(string? markdown)
    {
        var result = new List<FrameworkElement>();
        if (string.IsNullOrWhiteSpace(markdown)) return result;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var firstBlock = true;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var t = line.TrimStart();

            if (t.Length == 0) continue;

            // заголовки
            int level = 0;
            if (t.StartsWith("### ", StringComparison.Ordinal)) level = 3;
            else if (t.StartsWith("## ", StringComparison.Ordinal)) level = 2;
            else if (t.StartsWith("# ", StringComparison.Ordinal)) level = 1;

            if (level > 0)
            {
                var tb = BaseBlock(firstBlock, 12, 4);
                tb.FontSize = level == 3 ? 13 : 14.5;
                tb.FontWeight = FontWeights.SemiBold;
                tb.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                AddInlines(tb, t[(level + 1)..]);
                result.Add(tb);
            }
            // маркированный список
            else if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)
                     || t.StartsWith("• ", StringComparison.Ordinal))
            {
                var tb = ListItemBlock(firstBlock);
                var bullet = new Run("•  ") { FontWeight = FontWeights.SemiBold };
                bullet.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
                tb.Inlines.Add(bullet);
                AddInlines(tb, t[2..]);
                result.Add(tb);
            }
            // нумерованный список
            else if (char.IsDigit(t[0]))
            {
                var dot = t.IndexOf(". ", StringComparison.Ordinal);
                if (dot > 0 && dot <= 4 && int.TryParse(t[..dot], out var n))
                {
                    var tb = ListItemBlock(firstBlock);
                    var bullet = new Run($"{n}.  ") { FontWeight = FontWeights.SemiBold };
                    bullet.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
                    tb.Inlines.Add(bullet);
                    AddInlines(tb, t[(dot + 2)..]);
                    result.Add(tb);
                    continue;
                }
                result.Add(ParagraphBlock(firstBlock, t));
            }
            else
            {
                result.Add(ParagraphBlock(firstBlock, t));
            }

            firstBlock = false;
        }

        return result;
    }

    private static TextBlock BaseBlock(bool first, double top, double bottom)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19,
            Margin = new Thickness(0, first ? 0 : top, 0, bottom),
        };
        tb.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        return tb;
    }

    private static TextBlock ParagraphBlock(bool first, string text)
    {
        var tb = BaseBlock(first, 5, 0);
        AddInlines(tb, text);
        return tb;
    }

    private static TextBlock ListItemBlock(bool first)
    {
        var tb = BaseBlock(first, 3, 0);
        tb.Margin = new Thickness(14, first ? 0 : 3, 0, 0);
        return tb;
    }

    // ======================= inline-разбор =======================

    private static void AddInlines(TextBlock tb, string text)
    {
        foreach (var seg in Tokenize(text, new Seg()))
        {
            if (seg.Url is not null)
            {
                var link = new Hyperlink(new Run(seg.UrlAuto ? seg.Url : seg.Text))
                {
                    Cursor = Cursors.Hand,
                    TextDecorations = TextDecorations.Underline,
                };
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                var url = seg.Url;
                link.Click += (_, _) => OpenUrl(url);
                link.RequestNavigate += (_, _) => OpenUrl(url);
                tb.Inlines.Add(link);
                continue;
            }

            if (seg.Code)
            {
                var run = new Run(seg.Text)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                    FontSize = tb.FontSize - 0.5,
                };
                run.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
                tb.Inlines.Add(run);
                continue;
            }

            var r = new Run(seg.Text)
            {
                FontWeight = seg.Bold ? FontWeights.SemiBold : tb.FontWeight,
                FontStyle = seg.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = seg.Strike ? TextDecorations.Strikethrough : null,
            };
            r.SetResourceReference(TextElement.ForegroundProperty,
                seg.Strike ? "TextMuted" : "TextPrimary");
            tb.Inlines.Add(r);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* нет ассоциации браузера — молча игнорируем */ }
    }

    /// <summary>Рекурсивная токенизация строки: разбивает markdown-разметку на сегменты.</summary>
    private static List<Seg> Tokenize(string s, Seg ctx)
    {
        var segs = new List<Seg>();
        var buf = new StringBuilder();

        void Flush()
        {
            if (buf.Length == 0) return;
            segs.Add(new Seg
            {
                Text = buf.ToString(), Bold = ctx.Bold, Italic = ctx.Italic,
                Code = ctx.Code, Strike = ctx.Strike, Url = ctx.Url, UrlAuto = ctx.UrlAuto,
            });
            buf.Clear();
        }

        var i = 0;
        while (i < s.Length)
        {
            // ---- ссылка [текст](url) ----
            if (s[i] == '[' && !ctx.Code)
            {
                var close = s.IndexOf("](", i + 1, StringComparison.Ordinal);
                if (close > 0)
                {
                    var end = s.IndexOf(')', close + 2);
                    if (end > 0)
                    {
                        var label = s[(i + 1)..close];
                        var url = s[(close + 2)..end];
                        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            Flush();
                            var sub = Tokenize(label, new Seg { Url = url });
                            segs.AddRange(sub.Count > 0 ? sub : new List<Seg> { new() { Text = label, Url = url } });
                            i = end + 1;
                            continue;
                        }
                    }
                }
            }

            // ---- автоссылка <http://...> ----
            if (s[i] == '<' && !ctx.Code && s.AsSpan(i + 1).StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var end = s.IndexOf('>', i + 1);
                if (end > 0)
                {
                    var url = s[(i + 1)..end];
                    Flush();
                    segs.Add(new Seg { Text = url, Url = url, UrlAuto = true });
                    i = end + 1;
                    continue;
                }
            }

            // ---- инлайн-код `...` ----
            if (s[i] == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i + 0)
                {
                    Flush();
                    segs.AddRange(Tokenize(s[(i + 1)..end],
                        new Seg { Code = true }));
                    i = end + 1;
                    continue;
                }
            }

            // ---- жирный **...** / __...__ ----
            if (i + 1 < s.Length &&
                ((s[i] == '*' && s[i + 1] == '*') || (s[i] == '_' && s[i + 1] == '_')) && !ctx.Code)
            {
                var m = s.Substring(i, 2);
                var end = s.IndexOf(m, i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    segs.AddRange(Tokenize(s[(i + 2)..end],
                        new Seg { Bold = !ctx.Bold, Italic = ctx.Italic, Strike = ctx.Strike, Url = ctx.Url }));
                    i = end + 2;
                    continue;
                }
            }

            // ---- зачёркивание ~~...~~ ----
            if (i + 1 < s.Length && s[i] == '~' && s[i + 1] == '~' && !ctx.Code)
            {
                var end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    segs.AddRange(Tokenize(s[(i + 2)..end],
                        new Seg { Bold = ctx.Bold, Italic = ctx.Italic, Strike = !ctx.Strike, Url = ctx.Url }));
                    i = end + 2;
                    continue;
                }
            }

            // ---- курсив *...* (не путать с **) ----
            if (s[i] == '*' && !ctx.Code && (i + 1 >= s.Length || s[i + 1] != '*'))
            {
                var end = IndexOfSingle(s, '*', i + 1);
                if (end > 0 && (end + 1 >= s.Length || s[end + 1] != '*'))
                {
                    Flush();
                    segs.AddRange(Tokenize(s[(i + 1)..end],
                        new Seg { Bold = ctx.Bold, Italic = !ctx.Italic, Strike = ctx.Strike, Url = ctx.Url }));
                    i = end + 1;
                    continue;
                }
            }

            // ---- курсив _..._ (только на границах слов) ----
            if (s[i] == '_' && !ctx.Code && (i + 1 >= s.Length || s[i + 1] != '_')
                && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
            {
                var end = IndexOfSingle(s, '_', i + 1);
                if (end > 0 && (end + 1 >= s.Length || !char.IsLetterOrDigit(s[end + 1])))
                {
                    Flush();
                    segs.AddRange(Tokenize(s[(i + 1)..end],
                        new Seg { Bold = ctx.Bold, Italic = !ctx.Italic, Strike = ctx.Strike, Url = ctx.Url }));
                    i = end + 1;
                    continue;
                }
            }

            buf.Append(s[i]);
            i++;
        }

        Flush();
        return segs;
    }

    /// <summary>Ищет символ, который не является частью пары (для * и _).</summary>
    private static int IndexOfSingle(string s, char c, int from)
    {
        var idx = from;
        while ((idx = s.IndexOf(c, idx)) >= 0)
        {
            var prev = idx > 0 ? s[idx - 1] : ' ';
            if (prev != c) return idx;
            idx++;
        }
        return -1;
    }
}

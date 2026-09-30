using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MuxTerminal.App.Services;

namespace MuxTerminal.App.Controls;

/// <summary>Участок текста вывода определённой категории (смещения — в документе).</summary>
public struct StyledSpan
{
    public int Start;
    public int Length;
    public TextCategory Category;
    /// <summary>Префикс строки (время, направление) — правила поиска применяются к тексту после него.</summary>
    public bool IsPrefix;

    public int End => Start + Length;
}

/// <summary>
/// Раскрашивает видимые строки: сначала по категориям (принятое/отправленное/время/системное/ошибки),
/// затем поверх — совпадения правил поиска. Работает только для строк на экране, поэтому не зависит от объёма лога.
/// </summary>
public sealed class OutputColorizer : DocumentColorizingTransformer
{
    private readonly TerminalPane _owner;

    public OutputColorizer(TerminalPane owner) => _owner = owner;

    protected override void ColorizeLine(DocumentLine line)
    {
        var display = _owner.Display;
        var spans = _owner.Spans;
        int lineStart = line.Offset, lineEnd = line.EndOffset;
        if (lineEnd <= lineStart)
            return;

        for (int i = FindFirst(spans, lineStart); i < spans.Count && spans[i].Start < lineEnd; i++)
        {
            var span = spans[i];
            var style = display.Get(span.Category);
            if (!style.Enabled)
                continue;
            int s = Math.Max(lineStart, span.Start), e = Math.Min(lineEnd, span.End);
            if (s >= e)
                continue;
            var fg = Brushes2.Parse(style.Color);
            var bg = Brushes2.Parse(style.Background);
            ChangeLinePart(s, e, el => Apply(el, fg, bg, style.Bold, style.Italic, style.FontFamily));
        }

        var rules = _owner.Rules;
        if (rules.Count == 0)
            return;
        int contentStart = _owner.GetContentStart(line);
        if (contentStart >= lineEnd)
            return;
        string text = CurrentContext.Document.GetText(contentStart, lineEnd - contentStart);
        // Применяем в обратном порядке: при пересечении побеждает правило, стоящее выше в списке.
        for (int r = rules.Count - 1; r >= 0; r--)
        {
            var rule = rules[r];
            try
            {
                for (var m = rule.Regex.Match(text); m.Success; m = m.NextMatch())
                {
                    if (m.Length == 0)
                        continue;
                    ChangeLinePart(contentStart + m.Index, contentStart + m.Index + m.Length,
                        el => Apply(el, rule.Foreground, rule.Background, rule.Rule.Bold, false, null));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Слишком тяжёлое выражение на длинной строке — просто не подсвечиваем.
            }
        }
    }

    /// <summary>Индекс первого участка, который может пересекаться с offset (участки отсортированы и не пересекаются).</summary>
    internal static int FindFirst(List<StyledSpan> spans, int offset)
    {
        int lo = 0, hi = spans.Count - 1, result = spans.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (spans[mid].End > offset)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return result;
    }

    private static void Apply(VisualLineElement el, Brush? fg, Brush? bg, bool bold, bool italic, string? fontFamily)
    {
        var props = el.TextRunProperties;
        if (fg is not null)
            props.SetForegroundBrush(fg);
        if (bg is not null)
            props.SetBackgroundBrush(bg);
        if (bold || italic || !string.IsNullOrEmpty(fontFamily))
        {
            var tf = props.Typeface;
            props.SetTypeface(new Typeface(
                string.IsNullOrEmpty(fontFamily) ? tf.FontFamily : new FontFamily(fontFamily),
                italic ? FontStyles.Italic : tf.Style,
                bold ? FontWeights.Bold : tf.Weight,
                tf.Stretch));
        }
    }
}

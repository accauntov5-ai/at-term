using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace MuxTerminal.App.Services;

/// <summary>Категории текста в окнах терминала, для каждой — своё оформление.</summary>
public enum TextCategory
{
    /// <summary>Принятые данные (ответы модема, эхо команд).</summary>
    Rx,
    /// <summary>Отправленные команды/данные (локальное эхо, TX в логе и HEX).</summary>
    Tx,
    /// <summary>Дата/время в начале строки.</summary>
    Timestamp,
    /// <summary>Системные сообщения терминала: подключение, открытие каналов, служебный лог.</summary>
    System,
    /// <summary>Ошибки терминала и протокола.</summary>
    Error,
}

/// <summary>Оформление категории текста.</summary>
public sealed class CategoryStyle
{
    public TextCategory Category { get; set; }
    /// <summary>Выделять (false — выводится основным цветом и шрифтом).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Показывать (для времени и системных сообщений; false — скрыть совсем).</summary>
    public bool Visible { get; set; } = true;
    public string Color { get; set; } = "#DCDCDC";
    public string? Background { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    /// <summary>Моноширинный шрифт категории; null — основной шрифт.</summary>
    public string? FontFamily { get; set; }

    [JsonIgnore]
    public string DisplayName => Category switch
    {
        TextCategory.Rx => "Принятые данные",
        TextCategory.Tx => "Отправленные команды",
        TextCategory.Timestamp => "Дата / время",
        TextCategory.System => "Системные сообщения",
        TextCategory.Error => "Ошибки",
        _ => Category.ToString(),
    };

    /// <summary>Скрыть можно только время и системные сообщения.</summary>
    [JsonIgnore]
    public bool CanHide => Category is TextCategory.Timestamp or TextCategory.System;
}

/// <summary>Правило поиска: подсветка совпадений и (опционально) сбор строк в «Копилку».</summary>
public sealed class HighlightRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    /// <summary>Шаблон — регулярное выражение (иначе — простой текст).</summary>
    public bool IsRegex { get; set; }
    public bool MatchCase { get; set; }
    public string Color { get; set; } = "#FFD700";
    public string? Background { get; set; }
    public bool Bold { get; set; }
    /// <summary>Складывать строки с совпадением в «Копилку».</summary>
    public bool Collect { get; set; }

    /// <summary>Возвращает скомпилированное выражение или null (пустой/некорректный шаблон).</summary>
    public Regex? BuildRegex(out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(Pattern))
            return null;
        try
        {
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (!MatchCase)
                options |= RegexOptions.IgnoreCase;
            return new Regex(IsRegex ? Pattern : Regex.Escape(Pattern), options, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

/// <summary>Правило, готовое к применению (кисти и выражение созданы один раз).</summary>
public sealed class CompiledRule
{
    public CompiledRule(HighlightRule rule, Regex regex)
    {
        Rule = rule;
        Regex = regex;
        Foreground = Brushes2.Parse(rule.Color);
        Background = Brushes2.Parse(rule.Background);
    }

    public HighlightRule Rule { get; }
    public Regex Regex { get; }
    public Brush? Foreground { get; }
    public Brush? Background { get; }
}

/// <summary>Настройки внешнего вида окон терминала.</summary>
public sealed class DisplaySettings
{
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 13;
    public string Background { get; set; } = "#1E1E1E";
    public string Foreground { get; set; } = "#DCDCDC";
    /// <summary>Формат метки времени (.NET), например HH:mm:ss.fff или dd.MM.yyyy HH:mm:ss.</summary>
    public string TimestampFormat { get; set; } = "HH:mm:ss.fff";
    public List<CategoryStyle> Categories { get; set; } = new();
    public List<HighlightRule> Rules { get; set; } = new();

    public static readonly string[] TimestampFormats =
    {
        "HH:mm:ss.fff",
        "HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.fff",
    };

    public static DisplaySettings CreateDefault()
    {
        var s = new DisplaySettings();
        s.Normalize();
        s.Rules = DefaultRules();
        return s;
    }

    public static List<HighlightRule> DefaultRules() => new()
    {
        new() { Name = "Ошибка", Pattern = @"^(ERROR|\+CME ERROR.*|\+CMS ERROR.*)$", IsRegex = true, Color = "#FF5555", Bold = true, Collect = true },
        new() { Name = "Входящий звонок", Pattern = @"^(RING|\+CLIP:.*)$", IsRegex = true, Color = "#FFD700", Bold = true, Collect = true },
        new() { Name = "Новая SMS", Pattern = "+CMTI:", Color = "#50FA7B", Bold = true, Collect = true },
        new() { Name = "Связь потеряна", Pattern = @"^(NO CARRIER|BUSY|NO ANSWER|NO DIALTONE)$", IsRegex = true, Color = "#FFB86C", Bold = true, Collect = true },
        new() { Name = "Эхо AT-команд", Pattern = @"^AT.*$", IsRegex = true, Color = "#8BE9FD" },
        new() { Name = "Ответ OK", Pattern = "^OK$", IsRegex = true, MatchCase = true, Color = "#6272A4" },
        new() { Name = "URC (+XXX:)", Pattern = @"^\+[A-Z]+:", IsRegex = true, MatchCase = true, Color = "#BD93F9" },
    };

    private static CategoryStyle DefaultStyle(TextCategory c) => c switch
    {
        TextCategory.Rx => new CategoryStyle { Category = c, Color = "#DCDCDC" },
        TextCategory.Tx => new CategoryStyle { Category = c, Color = "#4FC1FF", Bold = true },
        TextCategory.Timestamp => new CategoryStyle { Category = c, Color = "#808080" },
        TextCategory.System => new CategoryStyle { Category = c, Color = "#6A9955", Italic = true },
        TextCategory.Error => new CategoryStyle { Category = c, Color = "#F44747", Bold = true },
        _ => new CategoryStyle { Category = c },
    };

    /// <summary>Досоздаёт недостающие категории (после загрузки старого файла настроек) и чинит значения.</summary>
    public void Normalize()
    {
        foreach (TextCategory c in Enum.GetValues(typeof(TextCategory)))
            if (Categories.All(x => x.Category != c))
                Categories.Add(DefaultStyle(c));
        Categories = Categories.GroupBy(c => c.Category).Select(g => g.First()).OrderBy(c => c.Category).ToList();
        if (FontSize is < 6 or > 48)
            FontSize = 13;
        if (!IsValidTimestampFormat(TimestampFormat))
            TimestampFormat = "HH:mm:ss.fff";
    }

    public static bool IsValidTimestampFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return false;
        try
        {
            DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public CategoryStyle Get(TextCategory c) => Categories.FirstOrDefault(x => x.Category == c) ?? DefaultStyle(c);

    public List<CompiledRule> CompileRules()
    {
        var list = new List<CompiledRule>();
        foreach (var r in Rules.Where(r => r.Enabled))
            if (r.BuildRegex(out _) is { } regex)
                list.Add(new CompiledRule(r, regex));
        return list;
    }

    public DisplaySettings Clone()
        => System.Text.Json.JsonSerializer.Deserialize<DisplaySettings>(System.Text.Json.JsonSerializer.Serialize(this))!;
}

/// <summary>Разбор цветов "#RRGGBB" / имён в замороженные кисти (с кэшем).</summary>
public static class Brushes2
{
    private static readonly Dictionary<string, Brush?> Cache = new();

    public static Brush? Parse(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(color!, out var cached))
                return cached;
            Brush? brush = null;
            try
            {
                if (ColorConverter.ConvertFromString(color) is Color c)
                {
                    brush = new SolidColorBrush(c);
                    brush.Freeze();
                }
            }
            catch (FormatException)
            {
            }
            Cache[color!] = brush;
            return brush;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>
/// Моноширинные шрифты, реально установленные в системе. Используем только их: настоящий WPF (.NET Framework)
/// аварийно завершает процесс (FailFast в FontFamily.FirstFontFamily), если ни одно имя из FontFamily не найдено, —
/// так бывает в Wine без шрифтов Windows.
/// </summary>
public static class MonoFonts
{
    private static readonly string[] Preferred = { "Consolas", "Cascadia Mono", "Lucida Console", "Courier New", "DejaVu Sans Mono", "Liberation Mono" };
    private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static List<string>? _mono;
    private static HashSet<string>? _installed;

    private static void Scan()
    {
        if (_mono is not null)
            return;
        var mono = new List<string>();
        _installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!typeface.TryGetGlyphTypeface(out var glyph))
                    continue;
                _installed.Add(family.Source);
                Families[family.Source] = family;
                if (IsMonospace(glyph))
                    mono.Add(family.Source);
            }
            catch
            {
                // Битый шрифт — пропускаем.
            }
        }
        _mono = mono.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => Array.IndexOf(Preferred, n) is var i && i >= 0 ? i : 100)
            .ThenBy(n => n)
            .ToList();
    }

    /// <summary>Установленные моноширинные шрифты (для выбора в настройках).</summary>
    public static IReadOnlyList<string> All()
    {
        Scan();
        return _mono!.Count > 0 ? _mono : new List<string> { Resolve(null) };
    }

    public static bool IsInstalled(string? name)
    {
        Scan();
        return !string.IsNullOrWhiteSpace(name) && _installed!.Contains(name!);
    }

    /// <summary>
    /// Имя установленного шрифта: нужный, если он есть; иначе первый установленный моноширинный;
    /// иначе системный шрифт интерфейса (лучше не моноширинный, чем падение программы).
    /// </summary>
    public static string Resolve(string? wanted)
    {
        Scan();
        if (IsInstalled(wanted))
            return wanted!;
        if (_mono!.Count > 0)
            return _mono[0];
        var ui = SystemFonts.MessageFontFamily.Source;
        return IsInstalled(ui) ? ui : _installed!.FirstOrDefault() ?? ui;
    }

    /// <summary>Объект шрифта для установленного имени (с кэшем).</summary>
    public static FontFamily Get(string? wanted)
    {
        string name = Resolve(wanted);
        lock (Families)
        {
            if (!Families.TryGetValue(name, out var family))
                Families[name] = family = new FontFamily(name);
            return family;
        }
    }

    private static bool IsMonospace(GlyphTypeface glyph)
    {
        double? width = null;
        foreach (char ch in "iW.m0")
        {
            if (!glyph.CharacterToGlyphMap.TryGetValue(ch, out var index))
                return false;
            double w = glyph.AdvanceWidths[index];
            if (width is null)
                width = w;
            else if (Math.Abs(width.Value - w) > 0.001)
                return false;
        }
        return true;
    }
}

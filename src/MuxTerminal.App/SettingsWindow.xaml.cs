using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MuxTerminal.App.Services;

namespace MuxTerminal.App;

/// <summary>Настройки отображения: шрифт, фон, оформление категорий текста, правила подсветки и «Копилки».</summary>
public partial class SettingsWindow : Window
{
    public const string DefaultFontLabel = "(основной)";

    private readonly Action<DisplaySettings> _apply;
    private DisplaySettings _settings;
    private ObservableCollection<HighlightRule> _rules = new();
    private ObservableCollection<QuickCommand> _macros = new();

    public SettingsWindow(DisplaySettings current, Action<DisplaySettings> apply)
    {
        InitializeComponent();
        _apply = apply;
        _settings = current.Clone();
        _settings.Normalize();

        foreach (var f in MonoFonts.All())
            FontCombo.Items.Add(f);
        foreach (var size in new[] { 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 24 })
            SizeCombo.Items.Add(size.ToString(CultureInfo.InvariantCulture));
        foreach (var f in DisplaySettings.TimestampFormats)
            TimeFormatCombo.Items.Add(f);

        Load();
    }

    /// <summary>Шрифты для категорий: «основной» + моноширинные шрифты системы.</summary>
    public static IReadOnlyList<string> CategoryFonts { get; } = new[] { DefaultFontLabel }.Concat(MonoFonts.All()).ToList();

    private void Load()
    {
        FontCombo.SelectedItem = FontCombo.Items.Contains(_settings.FontFamily) ? _settings.FontFamily : FontCombo.Items[0];
        SizeCombo.Text = _settings.FontSize.ToString(CultureInfo.InvariantCulture);
        TimeFormatCombo.Text = _settings.TimestampFormat;
        BackgroundBox.Text = _settings.Background;
        ForegroundBox.Text = _settings.Foreground;
        CategoryGrid.ItemsSource = _settings.Categories;
        _rules = new ObservableCollection<HighlightRule>(_settings.Rules);
        RuleGrid.ItemsSource = _rules;
        _macros = new ObservableCollection<QuickCommand>(_settings.Macros ?? DisplaySettings.DefaultMacros());
        MacroList.ItemsSource = _macros;
        MacroTextBox.FontFamily = MonoFonts.Get(null);
        if (_macros.Count > 0)
            MacroList.SelectedIndex = 0;
        UpdateTest();
    }

    /// <summary>Собирает значения с формы. Возвращает текст ошибки или null.</summary>
    private string? Collect()
    {
        CategoryGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);

        _settings.FontFamily = FontCombo.SelectedItem as string ?? "Consolas";
        if (!double.TryParse(SizeCombo.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double size) || size is < 6 or > 48)
            return "Размер шрифта: число от 6 до 48.";
        _settings.FontSize = size;
        if (!DisplaySettings.IsValidTimestampFormat(TimeFormatCombo.Text))
            return "Некорректный формат даты/времени.";
        _settings.TimestampFormat = TimeFormatCombo.Text;
        if (Brushes2.Parse(BackgroundBox.Text) is null || Brushes2.Parse(ForegroundBox.Text) is null)
            return "Цвет фона и основного текста: #RRGGBB или имя цвета (Black, White…).";
        _settings.Background = BackgroundBox.Text.Trim();
        _settings.Foreground = ForegroundBox.Text.Trim();

        foreach (var c in _settings.Categories)
        {
            if (c.FontFamily == DefaultFontLabel)
                c.FontFamily = null;
            if (Brushes2.Parse(c.Color) is null)
                return $"«{c.DisplayName}»: некорректный цвет «{c.Color}».";
            if (!string.IsNullOrWhiteSpace(c.Background) && Brushes2.Parse(c.Background) is null)
                return $"«{c.DisplayName}»: некорректный цвет фона «{c.Background}».";
        }

        for (int i = 0; i < _rules.Count; i++)
        {
            var r = _rules[i];
            string name = string.IsNullOrWhiteSpace(r.Name) ? $"правило {i + 1}" : r.Name;
            if (string.IsNullOrEmpty(r.Pattern))
                return $"«{name}»: пустой шаблон.";
            if (r.BuildRegex(out var error) is null)
                return $"«{name}»: ошибка в регулярном выражении — {error}";
            if (Brushes2.Parse(r.Color) is null)
                return $"«{name}»: некорректный цвет «{r.Color}».";
            if (!string.IsNullOrWhiteSpace(r.Background) && Brushes2.Parse(r.Background) is null)
                return $"«{name}»: некорректный цвет фона «{r.Background}».";
            if (string.IsNullOrWhiteSpace(r.Name))
                r.Name = name;
        }
        _settings.Rules = _rules.ToList();

        foreach (var m in _macros)
        {
            if (m.DelayMs < 0 || m.TimeoutMs < 100)
                return $"Быстрая команда «{m}»: пауза не может быть отрицательной, таймаут — не меньше 100 мс.";
        }
        _settings.Macros = _macros.ToList();
        return null;
    }

    private bool ApplyChanges()
    {
        var error = Collect();
        ErrorText.Text = error ?? "";
        if (error is not null)
            return false;
        _apply(_settings.Clone());
        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyChanges();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ApplyChanges())
            DialogResult = true;
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Вернуть все настройки отображения и правила поиска к значениям по умолчанию?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _settings = DisplaySettings.CreateDefault();
        Load();
    }

    // ───────────── Цвета ─────────────

    /// <summary>Стандартный диалог Windows. Возвращает #RRGGBB или null при отмене.</summary>
    private static string? PickColor(string? current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (Brushes2.Parse(current) is SolidColorBrush b)
            dialog.Color = System.Drawing.Color.FromArgb(b.Color.R, b.Color.G, b.Color.B);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return null;
        return Brushes2.ToHex(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        bool background = (string)button.Tag == "Background";
        switch (button.DataContext)
        {
            case CategoryStyle c:
                if (PickColor(background ? c.Background : c.Color) is { } color1)
                {
                    if (background) c.Background = color1; else c.Color = color1;
                    RefreshGrid(CategoryGrid);
                }
                break;
            case HighlightRule r:
                if (PickColor(background ? r.Background : r.Color) is { } color2)
                {
                    if (background) r.Background = color2; else r.Color = color2;
                    RefreshGrid(RuleGrid);
                }
                break;
        }
    }

    private static void RefreshGrid(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        grid.Items.Refresh();
    }

    private void PickGlobalColor_Click(object sender, RoutedEventArgs e)
    {
        var box = (string)((Button)sender).Tag == "Background" ? BackgroundBox : ForegroundBox;
        if (PickColor(box.Text) is { } color)
            box.Text = color;
    }

    private void GlobalColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        var swatch = (string)box.Tag == "Background" ? BackgroundSwatch : ForegroundSwatch;
        swatch.Background = Brushes2.Parse(box.Text) ?? Brushes.Transparent;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((Button)sender).Tag)
        {
            case "light":
                BackgroundBox.Text = "#FFFFFF";
                ForegroundBox.Text = "#1E1E1E";
                SetCategoryColors("#1E1E1E", "#0055CC", "#888888", "#2E7D32", "#C62828");
                AdaptRuleColors(light: true);
                break;
            case "green":
                BackgroundBox.Text = "#000000";
                ForegroundBox.Text = "#33FF33";
                SetCategoryColors("#33FF33", "#FFFFFF", "#1E8F1E", "#AAAA00", "#FF4444");
                AdaptRuleColors(light: false);
                break;
            default:
                BackgroundBox.Text = "#1E1E1E";
                ForegroundBox.Text = "#DCDCDC";
                SetCategoryColors("#DCDCDC", "#4FC1FF", "#808080", "#6A9955", "#F44747");
                AdaptRuleColors(light: false);
                break;
        }
    }

    private void SetCategoryColors(string rx, string tx, string time, string system, string error)
    {
        foreach (var c in _settings.Categories)
        {
            c.Color = c.Category switch
            {
                TextCategory.Rx => rx,
                TextCategory.Tx => tx,
                TextCategory.Timestamp => time,
                TextCategory.System => system,
                _ => error,
            };
        }
        RefreshGrid(CategoryGrid);
    }

    /// <summary>Пары цветов правил по умолчанию: для тёмного и для светлого фона.</summary>
    private static readonly (string Dark, string Light)[] RulePalette =
    {
        ("#FF5555", "#C62828"),
        ("#FFD700", "#9A7B00"),
        ("#50FA7B", "#2E7D32"),
        ("#FFB86C", "#E65100"),
        ("#8BE9FD", "#0277BD"),
        ("#6272A4", "#3949AB"),
        ("#BD93F9", "#6A1B9A"),
    };

    /// <summary>Перекрашивает правила, у которых стоит стандартный цвет, под светлый или тёмный фон.</summary>
    private void AdaptRuleColors(bool light)
    {
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var r in _rules)
            foreach (var (dark, lightColor) in RulePalette)
                if (string.Equals(r.Color, light ? dark : lightColor, StringComparison.OrdinalIgnoreCase))
                {
                    r.Color = light ? lightColor : dark;
                    break;
                }
        RuleGrid.Items.Refresh();
    }

    // ───────────── Быстрые команды ─────────────

    private void MacroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MacroEditor.DataContext = MacroList.SelectedItem;
        MacroEditor.IsEnabled = MacroList.SelectedItem is not null;
    }

    private void MacroName_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Название в списке слева обновляем на лету (QuickCommand без уведомлений об изменениях).
        if (MacroList.SelectedItem is { } selected && MacroNameBox.IsKeyboardFocused)
        {
            MacroList.Items.Refresh();
            MacroList.SelectedItem = selected;
        }
    }

    private void AddMacro_Click(object sender, RoutedEventArgs e)
    {
        var macro = new QuickCommand { Name = "Новая команда", Text = "AT" };
        _macros.Add(macro);
        MacroList.SelectedItem = macro;
        MacroNameBox.Focus();
        MacroNameBox.SelectAll();
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is QuickCommand m)
            _macros.Remove(m);
    }

    private void MoveMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not QuickCommand m)
            return;
        int from = _macros.IndexOf(m);
        int to = from + int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        if (to < 0 || to >= _macros.Count)
            return;
        _macros.Move(from, to);
        MacroList.SelectedItem = m;
    }

    private void ResetMacros_Click(object sender, RoutedEventArgs e)
    {
        _macros.Clear();
        foreach (var m in DisplaySettings.DefaultMacros())
            _macros.Add(m);
        MacroList.SelectedIndex = 0;
    }

    // ───────────── Правила ─────────────

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
        bool lightBackground = Brushes2.Parse(BackgroundBox.Text) is SolidColorBrush bg
                               && 0.299 * bg.Color.R + 0.587 * bg.Color.G + 0.114 * bg.Color.B > 128;
        var rule = new HighlightRule
        {
            Name = "Новое правило",
            Pattern = "",
            Color = lightBackground ? "#9A7B00" : "#FFD700",
            Bold = true,
            Collect = true,
        };
        // Новое правило — наверху: у него наивысший приоритет, иначе его перекроют общие правила (эхо AT, URC).
        _rules.Insert(0, rule);
        RuleGrid.SelectedItem = rule;
        RuleGrid.ScrollIntoView(rule);
        RuleGrid.CurrentCell = new DataGridCellInfo(rule, RuleGrid.Columns[2]);
        RuleGrid.BeginEdit();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is HighlightRule r)
        {
            RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
            _rules.Remove(r);
        }
    }

    private void MoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not HighlightRule r)
            return;
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
        int from = _rules.IndexOf(r);
        int to = from + int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        if (to < 0 || to >= _rules.Count)
            return;
        _rules.Move(from, to);
        RuleGrid.SelectedItem = r;
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e)
    {
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _rules.Clear();
        foreach (var r in DisplaySettings.DefaultRules())
            _rules.Add(r);
    }

    private void RuleGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(UpdateTest);

    private void RuleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTest();

    private void TestBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTest();

    /// <summary>Показывает, какое правило сработает на тестовой строке.</summary>
    private void UpdateTest()
    {
        if (TestResult is null || TestBox is null)
            return;
        string line = TestBox.Text;
        foreach (var r in _rules.Where(r => r.Enabled))
        {
            var regex = r.BuildRegex(out var error);
            if (regex is null)
            {
                if (error is not null && r == RuleGrid.SelectedItem)
                {
                    TestResult.Text = "Ошибка в шаблоне: " + error;
                    return;
                }
                continue;
            }
            if (regex.IsMatch(line))
            {
                TestResult.Text = $"Сработает: «{r.Name}»" + (r.Collect ? ", строка попадёт в копилку" : "");
                return;
            }
        }
        TestResult.Text = "Ни одно правило не сработало";
    }
}

/// <summary>"#RRGGBB" → кисть для образца цвета.</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Brushes2.Parse(value as string) ?? (Brush)Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

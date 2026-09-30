using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MuxTerminal.App.Services;

namespace MuxTerminal.App.Controls;

/// <summary>
/// «Копилка»: все строки, совпавшие с правилами поиска (флажок «В копилку»), из всех вкладок в одном месте.
/// </summary>
public partial class CollectorPane : UserControl
{
    private const int MaxEntries = 20_000;
    private const string AllRules = "(все правила)";

    private readonly ObservableCollection<CollectedEntry> _entries = new();
    private readonly ICollectionView _view;

    public CollectorPane()
    {
        InitializeComponent();
        List.ItemsSource = _entries;
        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = o => RuleFilter.SelectedItem is not string rule || rule == AllRules || ((CollectedEntry)o).RuleName == rule;
        SetRuleNames(Array.Empty<string>());
    }

    public event Action<CollectedEntry>? NavigateRequested;
    public event Action<CollectorPane, string>? LayoutCommandRequested;

    public int Count => _entries.Count;

    public void Add(CollectedEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
        CountText.Text = $"Записей: {_entries.Count}";
        if (AutoScrollCheck.IsChecked == true && List.Items.Count > 0)
            List.ScrollIntoView(List.Items[List.Items.Count - 1]);
    }

    public void ApplyDisplay(DisplaySettings display)
    {
        List.Background = Brushes2.Parse(display.Background) ?? Brushes.Black;
        List.Foreground = Brushes2.Parse(display.Foreground) ?? Brushes.Gainsboro;
        List.FontFamily = new FontFamily(display.FontFamily);
        List.FontSize = display.FontSize;
        SetRuleNames(display.Rules.Where(r => r.Collect).Select(r => r.Name));
    }

    private void SetRuleNames(IEnumerable<string> names)
    {
        var selected = RuleFilter.SelectedItem as string;
        RuleFilter.Items.Clear();
        RuleFilter.Items.Add(AllRules);
        foreach (var n in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            RuleFilter.Items.Add(n);
        RuleFilter.SelectedItem = selected is not null && RuleFilter.Items.Contains(selected) ? selected : AllRules;
    }

    private void RuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => _view?.Refresh();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
        CountText.Text = "Записей: 0";
    }

    private static string Format(CollectedEntry x) => $"{x.TimeText}\t{x.Source}\t{x.RuleName}\t{x.Line}";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить копилку",
            FileName = $"kopilka-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Текст (*.txt)|*.txt|Все файлы (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        var sb = new StringBuilder();
        foreach (CollectedEntry x in _view)
            sb.AppendLine(Format(x));
        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
    }

    private void Copy()
    {
        var selected = List.SelectedItems.Cast<CollectedEntry>().ToList();
        if (selected.Count == 0)
            return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(Format)));
        }
        catch
        {
            // Буфер обмена занят другим приложением.
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Copy();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Navigate();
            e.Handled = true;
        }
    }

    private void Navigate()
    {
        if (List.SelectedItem is CollectedEntry entry)
            NavigateRequested?.Invoke(entry);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Navigate();

    private void Layout_Click(object sender, RoutedEventArgs e)
        => LayoutCommandRequested?.Invoke(this, (string)((FrameworkElement)sender).Tag);
}

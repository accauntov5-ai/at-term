using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MuxTerminal.App.Services;

namespace MuxTerminal.App.Controls;

/// <summary>Быстрые команды, отправка файла и многострочная вставка, контекстное меню вывода.</summary>
public partial class TerminalPane
{
    private const long MaxFileSize = 10 * 1024 * 1024;

    // ───────────── Быстрые команды ─────────────

    private void BuildMacroBar(IEnumerable<QuickCommand> macros)
    {
        MacroBar.Children.Clear();
        foreach (var macro in macros.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Text)))
        {
            var button = new Button
            {
                Content = macro.ToString(),
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(0, 0, 4, 3),
                ToolTip = macro.Text.Replace("\r", ""),
                Tag = macro,
            };
            button.Click += async (_, _) => await RunMacroAsync(macro.Text, macro.DelayMs, macro.WaitForResult, macro.TimeoutMs);
            MacroBar.Children.Add(button);
        }
        if (IsLogMode || MacroBar.Children.Count == 0)
            MacroPanel.Visibility = Visibility.Collapsed;
        else
            MacroPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Раскрывает ^Z, \xHH, \r, \n, \t, \\ в строке команды.</summary>
    internal static byte[] Expand(string line, out bool hasTerminator)
    {
        var bytes = new List<byte>();
        hasTerminator = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '^' && i + 1 < line.Length && char.ToUpperInvariant(line[i + 1]) == 'Z')
            {
                bytes.Add(0x1A);
                hasTerminator = true;
                i++;
                continue;
            }
            if (c == '\\' && i + 1 < line.Length)
            {
                char n = line[i + 1];
                switch (n)
                {
                    case 'r': bytes.Add(0x0D); i++; continue;
                    case 'n': bytes.Add(0x0A); i++; continue;
                    case 't': bytes.Add(0x09); i++; continue;
                    case '\\': bytes.Add((byte)'\\'); i++; continue;
                    case 'x' when i + 3 < line.Length
                                  && byte.TryParse(line.Substring(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b):
                        bytes.Add(b);
                        if (b is 0x1A or 0x1B)
                            hasTerminator = true;
                        i += 3;
                        continue;
                }
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }
        return bytes.ToArray();
    }

    private string LineEnding => (LineEndingCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "\r";

    /// <summary>
    /// Отправляет строки по одной: окончание строки добавляется, если строка не заканчивается на ^Z / \x1A;
    /// «#wait N» — пауза. При waitForResult перед следующей строкой ждём ответ модема.
    /// </summary>
    private async Task RunMacroAsync(string text, int delayMs, bool waitForResult, int timeoutMs)
    {
        if (_macroCts is not null)
        {
            _macroCts.Cancel(); // повторное нажатие во время выполнения — остановить
            return;
        }
        var cts = _macroCts = new CancellationTokenSource();
        MacroBar.Opacity = 0.6;
        try
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length && !cts.IsCancellationRequested; i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                    continue;
                if (line.TrimStart().StartsWith("#wait", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(line.Trim().Substring(5).Trim(), out int ms))
                        await Task.Delay(Math.Max(0, ms), cts.Token);
                    continue;
                }

                var payload = Expand(line, out bool terminated);
                var data = terminated ? payload : payload.Concat(Encoding.ASCII.GetBytes(LineEnding)).ToArray();
                var waiter = waitForResult ? new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                _resultWaiter = waiter;
                if (!await SendAsync(data))
                    break;
                if (waiter is not null)
                {
                    var done = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, cts.Token));
                    if (done != waiter.Task)
                    {
                        AppendInfo($"Нет ответа на «{line.Trim()}» за {timeoutMs} мс — последовательность остановлена", ChunkKind.Error);
                        break;
                    }
                }
                if (i < lines.Length - 1 && delayMs > 0)
                    await Task.Delay(delayMs, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppendInfo("Последовательность команд остановлена");
        }
        finally
        {
            _resultWaiter = null;
            _macroCts = null;
            cts.Dispose();
            MacroBar.Opacity = 1;
        }
    }

    // ───────────── Файл и многострочная вставка ─────────────

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Отправить файл в канал", Filter = "Все файлы (*.*)|*.*|Текст (*.txt)|*.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length > MaxFileSize)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Файл больше 10 МБ.", "Отправка файла", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{info.Name} ({info.Length} байт)\n\n" +
            "Да — как команды: построчно, с окончанием строки и ожиданием ответа модема\n" +
            "Нет — как двоичные данные, без изменений\n" +
            "Отмена — не отправлять",
            "Отправка файла", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            await RunMacroAsync(File.ReadAllText(info.FullName), 100, true, 10000);
        }
        else if (answer == MessageBoxResult.No)
        {
            var data = File.ReadAllBytes(info.FullName);
            if (await SendAsync(data))
                AppendInfo($"Отправлен файл {info.Name}: {data.Length} байт");
        }
    }

    private void Input_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text || !text.Contains('\n'))
            return;
        e.CancelCommand();
        var lines = text.Replace("\r\n", "\n").Split('\n').Count(l => l.Trim().Length > 0);
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"Вставлено строк: {lines}. Отправить их по очереди как команды (с ожиданием ответа модема)?",
            "Многострочная вставка", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            _ = Dispatcher.BeginInvoke(async () => await RunMacroAsync(text, 100, true, 5000));
    }

    // ───────────── Контекстное меню вывода ─────────────

    private void OutputMenu_Opened(object sender, RoutedEventArgs e)
        => SaveSelectionMenu.IsEnabled = Output.SelectionLength > 0;

    private void Copy_Click(object sender, RoutedEventArgs e) => Output.Copy();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Output.SelectAll();

    // SearchPanel (установлен в конструкторе) обрабатывает стандартную команду «Найти» — как Ctrl+F.
    private void Find_Click(object sender, RoutedEventArgs e)
        => System.Windows.Input.ApplicationCommands.Find.Execute(null, Output.TextArea);

    private void SaveSelection_Click(object sender, RoutedEventArgs e) => SaveText(Output.SelectedText, "выделение");

    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveText(GetPlainText(), "вкладка");

    private void SaveText(string text, string what)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить " + what,
            FileName = $"{FileNameBase}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Текст (*.txt)|*.txt|Все файлы (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => RenameRequested?.Invoke(this);
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;
using MuxTerminal.App.Services;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Util;

namespace MuxTerminal.App.Controls;

public enum ChunkKind
{
    Rx,
    Tx,
    Info,
    Error,
}

/// <summary>Порция данных в истории вкладки. Храним байты, чтобы перерисовывать Текст ↔ HEX без потерь.</summary>
/// <param name="IsDataFrame">Кадр данных канала 1..61 в системном логе — может скрываться фильтром.</param>
public sealed record TerminalChunk(DateTime Time, ChunkKind Kind, byte[] Data, string? Text = null, bool IsDataFrame = false);

/// <summary>Строка, попавшая в «Копилку» по правилу поиска.</summary>
public sealed record CollectedEntry(DateTime Time, string TimeText, string Source, int Dlci, string RuleName, Brush? Brush, string Line);

/// <summary>
/// Окно терминала одного канала (или системного лога). Данные поступают из любого потока через
/// <see cref="Append"/> и выводятся пачками по таймеру — так UI не захлёбывается на потоке NMEA/бинарных данных.
/// Текст раскрашивается по категориям (<see cref="StyledSpan"/>) и правилам поиска (<see cref="OutputColorizer"/>).
/// </summary>
public partial class TerminalPane : UserControl
{
    private const int MaxChunks = 50_000;
    private const int MaxChars = 1_000_000;
    private const int MaxCollectLine = 4096;

    private readonly ConcurrentQueue<TerminalChunk> _incoming = new();
    private readonly List<TerminalChunk> _history = new();
    private readonly List<string> _commandHistory = new();
    private readonly DispatcherTimer _flushTimer;
    private List<StyledSpan> _spans = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _atLineStart = true;
    private bool _pendingCr;
    private int _historyIndex = -1;
    private int _newLines;
    private TaskCompletionSource<string>? _resultWaiter;
    private CancellationTokenSource? _macroCts;
    private ChannelState _state = ChannelState.Closed;

    // Сбор строк для «Копилки» идёт по принятому тексту независимо от режима отображения.
    private readonly Decoder _collectDecoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _collectLine = new();

    public TerminalPane()
    {
        InitializeComponent();
        var group = Guid.NewGuid().ToString("N");
        TextModeRadio.GroupName = group;
        HexModeRadio.GroupName = group;

        // Шрифт — только установленный (см. MonoFonts): до применения настроек ставим безопасный.
        Output.FontFamily = MonoFonts.Get(null);
        Input.FontFamily = Output.FontFamily;
        Output.Document.UndoStack.SizeLimit = 0;
        Output.Options.EnableHyperlinks = false;
        Output.Options.EnableEmailHyperlinks = false;
        Output.Options.EnableRectangularSelection = true;
        Output.TextArea.Caret.CaretBrush = Brushes.Transparent;
        Output.TextArea.TextView.LineTransformers.Add(new OutputColorizer(this));
        SearchPanel.Install(Output); // Ctrl+F — поиск по выводу

        // Таймер работает и когда вкладка скрыта/перемещается между окнами — иначе очередь росла бы без ограничений.
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _flushTimer.Start();
        SetChannelState(ChannelState.Closed, running: false);
    }

    /// <summary>Номер DLC; 0 — системный лог.</summary>
    public int Dlci { get; init; }

    /// <summary>Имя вкладки для «Копилки».</summary>
    public string SourceName { get; set; } = "";

    /// <summary>Режим лога: каждая запись — отдельная строка со временем и направлением, ввода нет.</summary>
    public bool IsLogMode
    {
        get => InputPanel.Visibility != Visibility.Visible;
        init
        {
            InputPanel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            var channelOnly = value ? Visibility.Collapsed : Visibility.Visible;
            OpenChannelButton.Visibility = channelOnly;
            CloseChannelButton.Visibility = channelOnly;
            EchoCheck.Visibility = channelOnly;
            StatePanel.Visibility = channelOnly;
            MacroPanel.Visibility = channelOnly;
            RenameMenu.Visibility = channelOnly;
            RenameSeparator.Visibility = channelOnly;
            DataFramesCheck.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
                TimestampCheck.IsChecked = true;
        }
    }

    public bool IsHexMode
    {
        get => HexModeRadio.IsChecked == true;
        set
        {
            if (value) HexModeRadio.IsChecked = true;
            else TextModeRadio.IsChecked = true;
        }
    }

    /// <summary>Отправка данных в канал. Назначается главным окном.</summary>
    public Func<byte[], Task>? SendHandler { get; set; }

    internal DisplaySettings Display { get; private set; } = DisplaySettings.CreateDefault();
    internal List<CompiledRule> Rules { get; private set; } = new();
    internal List<StyledSpan> Spans => _spans;

    public event Action<TerminalPane, string>? LayoutCommandRequested;
    public event Action<TerminalPane>? OpenChannelRequested;
    public event Action<TerminalPane>? CloseChannelRequested;
    public event Action<CollectedEntry>? Collected;
    public event Action<TerminalPane>? RenameRequested;
    /// <summary>Приняты новые строки (для счётчика непрочитанного на неактивной вкладке).</summary>
    public event Action<TerminalPane, int>? NewLines;

    /// <summary>Применяет оформление и правила поиска; вывод перерисовывается целиком.</summary>
    public void ApplyDisplay(DisplaySettings display, List<CompiledRule> rules)
    {
        Display = display;
        Rules = rules;
        var font = MonoFonts.Get(display.FontFamily);
        Output.FontFamily = font;
        Output.FontSize = display.FontSize;
        Output.Background = Brushes2.Parse(display.Background) ?? Brushes.Black;
        Output.Foreground = Brushes2.Parse(display.Foreground) ?? Brushes.Gainsboro;
        Input.FontFamily = font;
        BuildMacroBar(display.Macros ?? new List<QuickCommand>());
        RenderAll();
    }

    // ───────────── Приём данных (из любого потока) ─────────────

    public void Append(ChunkKind kind, byte[] data, string? text = null, bool isDataFrame = false)
        => _incoming.Enqueue(new TerminalChunk(DateTime.Now, kind, data, text, isDataFrame));

    public void AppendInfo(string text, ChunkKind kind = ChunkKind.Info)
        => Append(kind, Array.Empty<byte>(), text);

    /// <summary>Текст и участки категорий, накопленные за одну пачку вывода.</summary>
    private sealed class RenderBuffer
    {
        public RenderBuffer(int baseOffset) => BaseOffset = baseOffset;

        public int BaseOffset { get; }
        public StringBuilder Text { get; } = new();
        public List<StyledSpan> Spans { get; } = new();

        public void Append(string s, TextCategory category, bool prefix = false)
        {
            if (s.Length == 0)
                return;
            AddSpan(s.Length, category, prefix);
            Text.Append(s);
        }

        public void Append(char c, TextCategory category)
        {
            AddSpan(1, category, false);
            Text.Append(c);
        }

        public void NewLine() => Text.Append('\n');

        private void AddSpan(int length, TextCategory category, bool prefix)
        {
            int start = BaseOffset + Text.Length;
            if (!prefix && Spans.Count > 0)
            {
                var last = Spans[Spans.Count - 1];
                if (last.End == start && last.Category == category && !last.IsPrefix)
                {
                    last.Length += length;
                    Spans[Spans.Count - 1] = last;
                    return;
                }
            }
            Spans.Add(new StyledSpan { Start = start, Length = length, Category = category, IsPrefix = prefix });
        }
    }

    private void Flush()
    {
        if (_incoming.IsEmpty)
            return;

        var doc = Output.Document;
        var buf = new RenderBuffer(doc.TextLength);
        while (_incoming.TryDequeue(out var chunk))
        {
            _history.Add(chunk);
            CollectFrom(chunk);
            Render(chunk, buf);
        }
        if (_newLines > 0)
        {
            int n = _newLines;
            _newLines = 0;
            NewLines?.Invoke(this, n);
        }
        if (_history.Count > MaxChunks)
            _history.RemoveRange(0, _history.Count - MaxChunks * 4 / 5);

        if (buf.Text.Length == 0)
            return;

        _spans.AddRange(buf.Spans);
        doc.Insert(doc.TextLength, buf.Text.ToString());
        TrimIfNeeded();
        if (AutoScrollCheck.IsChecked == true)
            Output.ScrollToEnd();
    }

    /// <summary>Держим в окне не больше MaxChars символов: срезаем старые строки целиком.</summary>
    private void TrimIfNeeded()
    {
        var doc = Output.Document;
        if (doc.TextLength <= MaxChars)
            return;
        var line = doc.GetLineByOffset(doc.TextLength - MaxChars / 2);
        int cut = line.NextLine?.Offset ?? line.EndOffset;
        doc.Remove(0, cut);

        int first = OutputColorizer.FindFirst(_spans, cut);
        _spans.RemoveRange(0, first);
        for (int i = 0; i < _spans.Count; i++)
        {
            var s = _spans[i];
            s.Start -= cut;
            if (s.Start < 0)
            {
                s.Length += s.Start;
                s.Start = 0;
            }
            _spans[i] = s;
        }
    }

    private void RenderAll()
    {
        _decoder = Encoding.UTF8.GetDecoder();
        _atLineStart = true;
        _pendingCr = false;
        var buf = new RenderBuffer(0);
        int start = Math.Max(0, _history.Count - MaxChunks);
        for (int i = start; i < _history.Count; i++)
            Render(_history[i], buf);
        _spans = buf.Spans;
        Output.Document.Text = buf.Text.ToString();
        TrimIfNeeded();
        Output.ScrollToEnd();
    }

    private static TextCategory CategoryOf(ChunkKind kind) => kind switch
    {
        ChunkKind.Rx => TextCategory.Rx,
        ChunkKind.Tx => TextCategory.Tx,
        ChunkKind.Error => TextCategory.Error,
        _ => TextCategory.System,
    };

    private string Timestamp(DateTime time)
    {
        try
        {
            return time.ToString(Display.TimestampFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }

    private void Render(TerminalChunk chunk, RenderBuffer buf)
    {
        bool hex = IsHexMode;
        bool time = TimestampCheck.IsChecked == true && Display.Get(TextCategory.Timestamp).Visible;
        if (chunk.IsDataFrame && DataFramesCheck.IsChecked != true)
            return;
        if (chunk.Kind == ChunkKind.Info && !Display.Get(TextCategory.System).Visible)
            return;

        if (IsLogMode || chunk.Kind is ChunkKind.Info or ChunkKind.Error || hex)
        {
            if (!IsLogMode && chunk.Kind == ChunkKind.Tx && !hex && EchoCheck.IsChecked != true)
                return;
            EnsureLineStart(buf);
            var category = CategoryOf(chunk.Kind);
            if (time)
                buf.Append(Timestamp(chunk.Time) + " ", TextCategory.Timestamp, prefix: true);
            buf.Append(chunk.Kind switch
            {
                ChunkKind.Rx => "RX  ",
                ChunkKind.Tx => "TX  ",
                ChunkKind.Error => "ERR ",
                _ => IsLogMode ? "    " : "--- ",
            }, category, prefix: true);
            if (chunk.Text is not null && (!hex || chunk.Data.Length == 0))
                buf.Append(chunk.Text, category);
            else if (hex)
                buf.Append(Hex.Format(chunk.Data) + "   |" + Hex.ToPrintable(chunk.Data) + "|", category);
            else
                buf.Append(Hex.ToPrintable(chunk.Data), category);
            buf.NewLine();
            _atLineStart = true;
            return;
        }

        // Текстовый режим канала: непрерывный поток символов.
        if (chunk.Kind == ChunkKind.Tx && EchoCheck.IsChecked != true)
            return;

        var dataCategory = CategoryOf(chunk.Kind);
        var chars = new char[_decoder.GetCharCount(chunk.Data, 0, chunk.Data.Length)];
        _decoder.GetChars(chunk.Data, 0, chunk.Data.Length, chars, 0);
        foreach (char c in chars)
        {
            if (c == '\n')
            {
                if (_pendingCr)
                {
                    _pendingCr = false;
                    continue; // \r\n — один перевод строки
                }
                buf.NewLine();
                _atLineStart = true;
                continue;
            }
            _pendingCr = false;
            if (c == '\r')
            {
                buf.NewLine();
                _atLineStart = true;
                _pendingCr = true;
                continue;
            }
            if (_atLineStart && time)
                buf.Append("[" + Timestamp(chunk.Time) + "] ", TextCategory.Timestamp, prefix: true);
            _atLineStart = false;
            // Управляющие символы показываем как Unicode Control Pictures (␚ для Ctrl+Z и т.п.).
            buf.Append(c < 0x20 && c != '\t' ? (char)(0x2400 + c) : c, dataCategory);
        }
    }

    private void EnsureLineStart(RenderBuffer buf)
    {
        if (_atLineStart)
            return;
        buf.NewLine();
        _atLineStart = true;
        _pendingCr = false;
    }

    /// <summary>Начало «содержимого» строки — после метки времени и направления (к нему применяются правила поиска).</summary>
    internal int GetContentStart(DocumentLine line)
    {
        int pos = line.Offset;
        for (int i = OutputColorizer.FindFirst(_spans, pos); i < _spans.Count && _spans[i].Start == pos && _spans[i].IsPrefix; i++)
            pos = _spans[i].End;
        return Math.Min(pos, line.EndOffset);
    }

    // ───────────── Копилка ─────────────

    /// <summary>
    /// Разбор принятого текста на строки независимо от режима отображения: «Копилка», счётчик новых строк
    /// и ожидание ответа модема для быстрых команд.
    /// </summary>
    private void CollectFrom(TerminalChunk chunk)
    {
        bool collect = Collected is not null && Rules.Any(r => r.Rule.Collect);

        if (IsLogMode)
        {
            // В логе — только сообщения терминала и ошибки (данные каналов собираются в их вкладках).
            if (collect && chunk.Kind is ChunkKind.Info or ChunkKind.Error && chunk.Text is not null)
                foreach (var line in chunk.Text.Split('\n'))
                    TryCollect(line, chunk.Time);
            return;
        }

        if (chunk.Kind != ChunkKind.Rx)
            return;
        var chars = new char[_collectDecoder.GetCharCount(chunk.Data, 0, chunk.Data.Length)];
        _collectDecoder.GetChars(chunk.Data, 0, chunk.Data.Length, chars, 0);
        foreach (char c in chars)
        {
            if (c is '\r' or '\n')
            {
                if (_collectLine.Length > 0)
                {
                    var line = _collectLine.ToString();
                    _newLines++;
                    if (collect)
                        TryCollect(line, chunk.Time);
                    if (IsFinalResult(line.Trim()))
                        _resultWaiter?.TrySetResult(line.Trim());
                }
                _collectLine.Clear();
            }
            else if (_collectLine.Length < MaxCollectLine)
            {
                _collectLine.Append(c);
            }
        }
        // Приглашение «> » после AT+CMGS приходит без перевода строки.
        if (_collectLine.Length > 0 && _collectLine.ToString().Trim() == ">")
            _resultWaiter?.TrySetResult(">");
    }

    private static bool IsFinalResult(string line)
        => line is "OK" or "ERROR" or "NO CARRIER" or "BUSY" or "NO ANSWER" or "NO DIALTONE" or "CONNECT"
           || line.StartsWith("+CME ERROR", StringComparison.Ordinal)
           || line.StartsWith("+CMS ERROR", StringComparison.Ordinal)
           || line.StartsWith("CONNECT ", StringComparison.Ordinal);

    private void TryCollect(string line, DateTime time)
    {
        line = line.Trim();
        if (line.Length == 0)
            return;
        foreach (var rule in Rules)
        {
            if (!rule.Rule.Collect)
                continue;
            try
            {
                if (!rule.Regex.IsMatch(line))
                    continue;
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            Collected?.Invoke(new CollectedEntry(time, Timestamp(time), SourceName, Dlci, rule.Rule.Name, rule.Foreground ?? Brushes2.Parse(Display.Foreground), line));
            return; // одна строка — одна запись (первое подходящее правило)
        }
    }

    public string GetPlainText()
    {
        Flush();
        return Output.Text;
    }

    // ───────────── Состояние канала ─────────────

    public void SetChannelState(ChannelState state, bool running)
    {
        _state = state;
        (Brush brush, string text) = state switch
        {
            ChannelState.Open => ((Brush)Brushes.LimeGreen, "открыт"),
            ChannelState.Opening => (Brushes.Gold, "открытие…"),
            ChannelState.Closing => (Brushes.Gold, "закрытие…"),
            ChannelState.Failed => (Brushes.Red, "ошибка"),
            _ => (Brushes.Gray, "закрыт"),
        };
        StateDot.Fill = brush;
        StateText.Text = $"DLC{Dlci}: {text}";
        bool open = state == ChannelState.Open;
        Input.IsEnabled = open;
        SendButton.IsEnabled = open;
        MacroBar.IsEnabled = open;
        SendFileButton.IsEnabled = open;
        OpenChannelButton.IsEnabled = running && state is ChannelState.Closed or ChannelState.Failed;
        CloseChannelButton.IsEnabled = running && open;
    }

    public ChannelState ChannelState => _state;

    // ───────────── Отправка ─────────────

    private async Task<bool> SendAsync(byte[] data)
    {
        if (SendHandler is null || data.Length == 0)
            return false;
        try
        {
            await SendHandler(data);
            Append(ChunkKind.Tx, data);
            return true;
        }
        catch (Exception ex)
        {
            AppendInfo("Ошибка отправки: " + ex.Message, ChunkKind.Error);
            return false;
        }
    }

    /// <param name="terminator">
    /// null — добавить выбранное окончание строки (CR/CRLF/LF); иначе — этот байт (0x1A для завершения SMS, 0x1B для отмены).
    /// </param>
    private async Task SendInputAsync(byte? terminator = null)
    {
        string line = Input.Text;
        byte[] data;
        if (HexInputCheck.IsChecked == true)
        {
            if (line.Trim().Length == 0)
                data = Array.Empty<byte>();
            else if (!Hex.TryParse(line, out data))
            {
                AppendInfo("Некорректная HEX-строка: " + line, ChunkKind.Error);
                return;
            }
            if (terminator is null && data.Length == 0)
                return;
        }
        else
        {
            var ending = terminator is null ? (LineEndingCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "\r" : "";
            data = Encoding.UTF8.GetBytes(line + ending);
        }
        if (terminator is { } t)
            data = data.Concat(new[] { t }).ToArray();

        if (line.Length > 0 && (_commandHistory.Count == 0 || _commandHistory[_commandHistory.Count - 1] != line))
            _commandHistory.Add(line);
        _historyIndex = -1;
        Input.Clear();
        await SendAsync(data);
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendInputAsync();

    // Текст SMS из строки ввода уходит вместе с Ctrl+Z одной посылкой.
    private async void CtrlZ_Click(object sender, RoutedEventArgs e) => await SendInputAsync(0x1A);

    private async void Esc_Click(object sender, RoutedEventArgs e) => await SendInputAsync(0x1B);

    private async void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await SendInputAsync();
                break;
            case Key.Up when _commandHistory.Count > 0:
                e.Handled = true;
                _historyIndex = _historyIndex < 0 ? _commandHistory.Count - 1 : Math.Max(0, _historyIndex - 1);
                SetInputFromHistory();
                break;
            case Key.Down when _historyIndex >= 0:
                e.Handled = true;
                _historyIndex++;
                if (_historyIndex >= _commandHistory.Count)
                {
                    _historyIndex = -1;
                    Input.Clear();
                }
                else
                {
                    SetInputFromHistory();
                }
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                await SendInputAsync(0x1A);
                break;
        }
    }

    private void SetInputFromHistory()
    {
        Input.Text = _commandHistory[_historyIndex];
        Input.CaretIndex = Input.Text.Length;
    }

    public void FocusInput()
    {
        if (Input.IsVisible && Input.IsEnabled)
            Input.Focus();
    }

    // ───────────── Панель инструментов ─────────────

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        while (_incoming.TryDequeue(out _)) { }
        _history.Clear();
        _spans = new List<StyledSpan>();
        Output.Document.Text = "";
        _atLineStart = true;
        _pendingCr = false;
        _decoder = Encoding.UTF8.GetDecoder();
    }

    private void ViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            RenderAll();
    }

    private void Wrap_Changed(object sender, RoutedEventArgs e)
    {
        if (Output is not null)
            Output.WordWrap = WrapCheck.IsChecked == true;
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
        => LayoutCommandRequested?.Invoke(this, (string)((FrameworkElement)sender).Tag);

    private void OpenChannel_Click(object sender, RoutedEventArgs e) => OpenChannelRequested?.Invoke(this);

    private void CloseChannel_Click(object sender, RoutedEventArgs e) => CloseChannelRequested?.Invoke(this);
}

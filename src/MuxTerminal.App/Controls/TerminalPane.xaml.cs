using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
public sealed record TerminalChunk(DateTime Time, ChunkKind Kind, byte[] Data, string? Text = null);

/// <summary>
/// Окно терминала одного канала (или системного лога). Данные поступают из любого потока через
/// <see cref="Append"/> и выводятся пачками по таймеру — так UI не захлёбывается на потоке NMEA/бинарных данных.
/// </summary>
public partial class TerminalPane : UserControl
{
    private const int MaxChunks = 50_000;
    private const int MaxChars = 1_000_000;

    private readonly ConcurrentQueue<TerminalChunk> _incoming = new();
    private readonly List<TerminalChunk> _history = new();
    private readonly List<string> _commandHistory = new();
    private readonly DispatcherTimer _flushTimer;
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _atLineStart = true;
    private int _outputLength; // TextBox.Text копирует весь текст — длину считаем сами
    private bool _pendingCr;
    private int _historyIndex = -1;
    private ChannelState _state = ChannelState.Closed;

    public TerminalPane()
    {
        InitializeComponent();
        var group = Guid.NewGuid().ToString("N");
        TextModeRadio.GroupName = group;
        HexModeRadio.GroupName = group;
        // Таймер работает и когда вкладка скрыта/перемещается между окнами — иначе очередь росла бы без ограничений.
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _flushTimer.Start();
        SetChannelState(ChannelState.Closed, running: false);
    }

    /// <summary>Номер DLC; 0 — системный лог.</summary>
    public int Dlci { get; init; }

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

    public event Action<TerminalPane, string>? LayoutCommandRequested;
    public event Action<TerminalPane>? OpenChannelRequested;
    public event Action<TerminalPane>? CloseChannelRequested;

    // ───────────── Приём данных (из любого потока) ─────────────

    public void Append(ChunkKind kind, byte[] data, string? text = null)
        => _incoming.Enqueue(new TerminalChunk(DateTime.Now, kind, data, text));

    public void AppendInfo(string text, ChunkKind kind = ChunkKind.Info)
        => Append(kind, Array.Empty<byte>(), text);

    private void Flush()
    {
        if (_incoming.IsEmpty)
            return;

        var sb = new StringBuilder();
        while (_incoming.TryDequeue(out var chunk))
        {
            _history.Add(chunk);
            Render(chunk, sb);
        }
        if (_history.Count > MaxChunks)
            _history.RemoveRange(0, _history.Count - MaxChunks * 4 / 5);

        if (sb.Length == 0)
            return;

        bool scroll = AutoScrollCheck.IsChecked == true;
        if (_outputLength + sb.Length > MaxChars)
        {
            var combined = Output.Text + sb;
            SetOutput(combined[^(MaxChars / 2)..]);
        }
        else
        {
            Output.AppendText(sb.ToString());
            _outputLength += sb.Length;
        }
        if (scroll)
            Output.ScrollToEnd();
    }

    private void RenderAll()
    {
        _decoder = Encoding.UTF8.GetDecoder();
        _atLineStart = true;
        _pendingCr = false;
        var sb = new StringBuilder();
        int start = Math.Max(0, _history.Count - MaxChunks);
        for (int i = start; i < _history.Count; i++)
            Render(_history[i], sb);
        var text = sb.ToString();
        SetOutput(text.Length > MaxChars ? text[^(MaxChars / 2)..] : text);
        Output.ScrollToEnd();
    }

    private void SetOutput(string text)
    {
        Output.Text = text;
        _outputLength = text.Length;
    }

    private void Render(TerminalChunk chunk, StringBuilder sb)
    {
        bool hex = IsHexMode;
        bool time = TimestampCheck.IsChecked == true;

        if (IsLogMode || chunk.Kind is ChunkKind.Info or ChunkKind.Error || hex)
        {
            if (!IsLogMode && chunk.Kind == ChunkKind.Tx && !hex && EchoCheck.IsChecked != true)
                return;
            EnsureLineStart(sb);
            if (time)
                sb.Append(chunk.Time.ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture));
            sb.Append(chunk.Kind switch
            {
                ChunkKind.Rx => "RX  ",
                ChunkKind.Tx => "TX  ",
                ChunkKind.Error => "ERR ",
                _ => IsLogMode ? "    " : "--- ",
            });
            if (chunk.Text is not null && (!hex || chunk.Data.Length == 0))
                sb.Append(chunk.Text);
            else if (hex)
                sb.Append(Hex.Format(chunk.Data)).Append("   |").Append(Hex.ToPrintable(chunk.Data)).Append('|');
            else
                sb.Append(Hex.ToPrintable(chunk.Data));
            sb.Append('\n');
            _atLineStart = true;
            return;
        }

        // Текстовый режим канала: непрерывный поток символов.
        if (chunk.Kind == ChunkKind.Tx && EchoCheck.IsChecked != true)
            return;

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
                sb.Append('\n');
                _atLineStart = true;
                continue;
            }
            _pendingCr = false;
            if (c == '\r')
            {
                sb.Append('\n');
                _atLineStart = true;
                _pendingCr = true;
                continue;
            }
            if (_atLineStart && time)
                sb.Append(chunk.Time.ToString("[HH:mm:ss.fff] ", CultureInfo.InvariantCulture));
            _atLineStart = false;
            // Управляющие символы показываем как Unicode Control Pictures (␚ для Ctrl+Z и т.п.).
            sb.Append(c < 0x20 && c != '\t' ? (char)(0x2400 + c) : c);
        }
    }

    private void EnsureLineStart(StringBuilder sb)
    {
        if (_atLineStart)
            return;
        sb.Append('\n');
        _atLineStart = true;
        _pendingCr = false;
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
        OpenChannelButton.IsEnabled = running && state is ChannelState.Closed or ChannelState.Failed;
        CloseChannelButton.IsEnabled = running && open;
    }

    public ChannelState ChannelState => _state;

    // ───────────── Отправка ─────────────

    private async Task SendAsync(byte[] data)
    {
        if (SendHandler is null || data.Length == 0)
            return;
        try
        {
            await SendHandler(data);
            Append(ChunkKind.Tx, data);
        }
        catch (Exception ex)
        {
            AppendInfo("Ошибка отправки: " + ex.Message, ChunkKind.Error);
        }
    }

    private async Task SendInputAsync()
    {
        string line = Input.Text;
        byte[] data;
        if (HexInputCheck.IsChecked == true)
        {
            if (!Hex.TryParse(line, out data))
            {
                AppendInfo("Некорректная HEX-строка: " + line, ChunkKind.Error);
                return;
            }
        }
        else
        {
            var ending = (LineEndingCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "\r";
            data = Encoding.UTF8.GetBytes(line + ending);
        }

        if (line.Length > 0 && (_commandHistory.Count == 0 || _commandHistory[^1] != line))
            _commandHistory.Add(line);
        _historyIndex = -1;
        Input.Clear();
        await SendAsync(data);
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendInputAsync();

    private async void CtrlZ_Click(object sender, RoutedEventArgs e) => await SendAsync(new byte[] { 0x1A });

    private async void Esc_Click(object sender, RoutedEventArgs e) => await SendAsync(new byte[] { 0x1B });

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
                await SendAsync(new byte[] { 0x1A });
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
        SetOutput("");
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
            Output.TextWrapping = WrapCheck.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
        => LayoutCommandRequested?.Invoke(this, (string)((FrameworkElement)sender).Tag);

    private void OpenChannel_Click(object sender, RoutedEventArgs e) => OpenChannelRequested?.Invoke(this);

    private void CloseChannel_Click(object sender, RoutedEventArgs e) => CloseChannelRequested?.Invoke(this);
}

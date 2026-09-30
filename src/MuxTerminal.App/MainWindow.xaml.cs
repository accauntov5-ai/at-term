using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using Microsoft.Win32;
using MuxTerminal.App.Controls;
using MuxTerminal.App.Services;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Transport;
using MuxTerminal.Core.Util;
using IOHandshake = System.IO.Ports.Handshake;

namespace MuxTerminal.App;

public partial class MainWindow : Window
{
    private const string EmulatorPort = "Эмулятор модема";
    private static readonly int[] BaudRates = { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    private sealed record ChannelView(int Dlci, LayoutDocument Doc, TerminalPane Pane);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TerminalPane _logPane;
    private readonly LayoutDocument _logDoc;
    // Доступ из фонового потока сессии (маршрутизация данных) — поэтому ConcurrentDictionary.
    private readonly ConcurrentDictionary<int, ChannelView> _channels = new();
    private readonly DispatcherTimer _statusTimer;

    private IMuxTransport? _transport;
    private MuxSession? _session;
    private CancellationTokenSource? _startCts;
    private readonly CollectorPane _collector = new();
    private readonly LayoutDocument _collectorDoc;
    private List<CompiledRule> _rules = new();
    private LayoutDocument? _lastActiveDoc;
    private bool _starting;
    private bool _stopping;
    private bool _closeRequested;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.Title;
        _logPane = new TerminalPane { Dlci = 0, IsLogMode = true, IsHexMode = false };
        _logPane.SourceName = "System Log";
        _logDoc = CreateDocument("System Log · DLC0", "log", _logPane,
            "Служебный лог: сырые MUX-кадры (HEX), канал управления DLC0, ошибки FCS");
        _logPane.LayoutCommandRequested += (_, cmd) => ExecuteLayoutCommand(_logDoc, cmd);
        _logPane.Collected += OnCollected;

        _collectorDoc = CreateDocument("Копилка", "collector", _collector,
            "Все строки, совпавшие с правилами поиска (меню «Настройки», «Отображение и подсветка»)");
        _collector.LayoutCommandRequested += (_, cmd) => ExecuteLayoutCommand(_collectorDoc, cmd);
        _collector.NavigateRequested += NavigateTo;
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => UpdateCounters(), Dispatcher);
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    // ───────────────────────────── Инициализация ─────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshPorts();
        foreach (var b in BaudRates)
            BaudCombo.Items.Add(b.ToString());
        BaudCombo.Text = _settings.BaudRate.ToString();
        RtsCtsCheck.IsChecked = _settings.HardwareFlowControl;
        DtrCheck.IsChecked = _settings.Dtr;
        CmuxCombo.Text = _settings.CmuxCommand;
        SkipCmuxCheck.IsChecked = _settings.SkipCmux;
        ChannelsBox.Text = _settings.Channels;

        var pane = MainDocumentPane();
        pane.Children.Add(_logDoc);
        pane.Children.Add(_collectorDoc);
        ApplyDisplay(_settings.Display);
        if (TryParseChannels(_settings.Channels, out var channels, out _))
            foreach (var dlci in channels)
                EnsureChannel(dlci);
        (_channels.Values.OrderBy(c => c.Dlci).FirstOrDefault()?.Doc ?? _logDoc).IsSelected = true;

        _logPane.AppendInfo("Выберите порт (или «Эмулятор модема» для проверки без железа) и нажмите «Старт MUX».");
        _statusTimer.Start();
        UpdateUi();
    }

    private void RefreshPorts()
    {
        string current = string.IsNullOrEmpty(PortCombo.Text) ? _settings.PortName : PortCombo.Text;
        PortCombo.Items.Clear();
        foreach (var p in SerialPortTransport.GetPortNames())
            PortCombo.Items.Add(p);
        PortCombo.Items.Add(EmulatorPort);
        PortCombo.Text = !string.IsNullOrEmpty(current) ? current : (string)PortCombo.Items[0]!;
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    // ───────────────────────────── Документы / окна ─────────────────────────────

    private static LayoutDocument CreateDocument(string title, string contentId, object content, string toolTip)
        => new()
        {
            Title = title,
            ContentId = contentId,
            Content = content,
            CanClose = false, // вкладку нельзя потерять; её можно только переместить
            CanFloat = true,
            ToolTip = toolTip,
        };

    private ChannelView EnsureChannel(int dlci)
    {
        if (_channels.TryGetValue(dlci, out var existing))
            return existing;

        var pane = new TerminalPane { Dlci = dlci };
        pane.SendHandler = data => SendToChannelAsync(dlci, data);
        pane.OpenChannelRequested += p => _ = OpenChannelAsync(p.Dlci);
        pane.CloseChannelRequested += p => _ = CloseChannelAsync(p.Dlci);
        var doc = CreateDocument(ChannelTitle(dlci, ChannelState.Closed), $"dlc{dlci}", pane, $"Логический канал DLC {dlci}");
        pane.LayoutCommandRequested += (_, cmd) => ExecuteLayoutCommand(doc, cmd);
        pane.SourceName = ChannelTitle(dlci, ChannelState.Open);
        pane.Collected += OnCollected;
        pane.ApplyDisplay(_settings.Display, _rules);
        var view = new ChannelView(dlci, doc, pane);
        _channels[dlci] = view;

        // Сохраняем порядок вкладок по номеру DLC в основной группе.
        var target = MainDocumentPane();
        int index = target.Children.Count;
        for (int i = 0; i < target.Children.Count; i++)
        {
            if (target.Children[i].Content is CollectorPane || target.Children[i].Content is TerminalPane p && p.Dlci > dlci)
            {
                index = i;
                break;
            }
        }
        target.Children.Insert(index, doc);
        pane.SetChannelState(ChannelState.Closed, _session?.State == MuxSessionState.Running);
        return view;
    }

    private string ChannelName(int dlci)
        => _settings.ChannelNames.TryGetValue(dlci, out var name) && !string.IsNullOrWhiteSpace(name) ? name : "";

    private string ChannelTitle(int dlci, ChannelState state)
    {
        string name = ChannelName(dlci);
        string title = name.Length > 0 ? $"DLC {dlci} · {name}" : $"DLC {dlci}";
        return state == ChannelState.Open ? title : $"{title} ({StateName(state)})";
    }

    private static string StateName(ChannelState s) => s switch
    {
        ChannelState.Open => "открыт",
        ChannelState.Opening => "открытие",
        ChannelState.Closing => "закрытие",
        ChannelState.Failed => "ошибка",
        _ => "закрыт",
    };

    private IEnumerable<LayoutDocument> AllDocuments()
        => new[] { _logDoc }.Concat(_channels.Values.OrderBy(c => c.Dlci).Select(c => c.Doc)).Concat(new[] { _collectorDoc });

    private IEnumerable<TerminalPane> AllPanes()
        => new[] { _logPane }.Concat(_channels.Values.Select(c => c.Pane));

    // ───────────────────────────── Оформление и «Копилка» ─────────────────────────────

    private void ApplyDisplay(DisplaySettings display)
    {
        _settings.Display = display;
        _rules = display.CompileRules();
        foreach (var pane in AllPanes())
            pane.ApplyDisplay(display, _rules);
        _collector.ApplyDisplay(display);
    }

    private void DisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings.Display, d =>
        {
            ApplyDisplay(d);
            _settings.Save();
        }) { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowCollector_Click(object sender, RoutedEventArgs e) => _collectorDoc.IsActive = true;

    private void OnCollected(CollectedEntry entry)
    {
        _collector.Add(entry);
        _collectorDoc.Title = $"Копилка ({_collector.Count})";
    }

    private void NavigateTo(CollectedEntry entry)
    {
        var doc = entry.Dlci == 0 ? _logDoc : _channels.TryGetValue(entry.Dlci, out var view) ? view.Doc : null;
        if (doc is not null)
            doc.IsActive = true;
    }

    private static bool IsInFloatingWindow(ILayoutElement element)
    {
        for (var e = element.Parent as ILayoutElement; e is not null; e = e.Parent)
            if (e is LayoutFloatingWindow)
                return true;
        return false;
    }

    /// <summary>Первая группа вкладок главного окна (создаётся, если все вкладки вынесены).</summary>
    private LayoutDocumentPane MainDocumentPane()
    {
        var pane = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault(p => !IsInFloatingWindow(p));
        if (pane is not null)
            return pane;
        pane = new LayoutDocumentPane();
        Dock.Layout.RootPanel.Children.Add(new LayoutDocumentPaneGroup(pane));
        return pane;
    }

    private static void Detach(LayoutDocument doc) => doc.Parent?.RemoveChild(doc);

    private void MoveToMainPane(LayoutDocument doc)
    {
        var target = MainDocumentPane();
        if (doc.Parent == target)
            return;
        Detach(doc);
        target.Children.Add(doc);
    }

    private LayoutDocument? ActiveDocument()
    {
        if (Dock.ActiveContent is { } content && AllDocuments().FirstOrDefault(d => d.Content == content) is { } doc)
            return doc;
        return _lastActiveDoc ?? AllDocuments().FirstOrDefault(d => d.IsSelected);
    }

    private void Dock_ActiveContentChanged(object? sender, EventArgs e)
    {
        if (Dock.ActiveContent is { } content && AllDocuments().FirstOrDefault(d => d.Content == content) is { } doc)
            _lastActiveDoc = doc;
        if (Dock.ActiveContent is TerminalPane p)
            p.Dispatcher.BeginInvoke(p.FocusInput, DispatcherPriority.Input);
    }

    /// <summary>
    /// Команды раскладки: вынести в отдельное окно, вернуть во вкладку, разделить (новая группа справа/снизу),
    /// переместить в соседнюю группу. Используются штатные команды AvalonDock — те же, что в контекстном меню вкладки.
    /// </summary>
    private void ExecuteLayoutCommand(LayoutDocument doc, string command)
    {
        switch (command)
        {
            case "float":
                if (!doc.IsFloating)
                    doc.Float();
                break;

            case "dock":
                if (doc.IsFloating)
                    doc.DockAsDocument();
                MoveToMainPane(doc);
                break;

            case "splitV":
            case "splitH":
            case "next":
                if (doc.IsFloating)
                {
                    doc.DockAsDocument();
                    MoveToMainPane(doc);
                }
                // Даём AvalonDock построить визуальные элементы после перемещения.
                Dispatcher.BeginInvoke(() => RunItemCommand(doc, command), DispatcherPriority.Loaded);
                return;
        }
        doc.IsActive = true;
    }

    private void RunItemCommand(LayoutDocument doc, string command)
    {
        var item = Dock.GetLayoutItemFromModel(doc);
        if (item is null)
            return;
        ICommand cmd = command switch
        {
            "splitV" => item.NewVerticalTabGroupCommand,
            "splitH" => item.NewHorizontalTabGroupCommand,
            _ => item.MoveToNextTabGroupCommand,
        };
        if (cmd.CanExecute(null))
        {
            cmd.Execute(null);
            doc.IsActive = true;
        }
        else
        {
            ShowHint(command == "next"
                ? "Нет соседней группы вкладок — сначала разделите окно."
                : "Для разделения в группе должно быть минимум две вкладки.");
        }
    }

    private void LayoutMenu_Click(object sender, RoutedEventArgs e)
    {
        var doc = ActiveDocument();
        if (doc is not null)
            ExecuteLayoutCommand(doc, (string)((FrameworkElement)sender).Tag);
    }

    /// <summary>Пересоздаёт раскладку: все окна (включая вынесенные) — вкладками одной группы.</summary>
    private void ResetLayout_Click(object sender, RoutedEventArgs e)
        => ApplyLayout(docs => new LayoutPanel(new LayoutDocumentPaneGroup(Pane(docs))));

    private void PresetLayout_Click(object sender, RoutedEventArgs e)
        => ApplyLayout(docs =>
        {
            var channels = docs.Where(d => d != _logDoc).ToList();
            var group = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
            group.Children.Add(Pane(new[] { _logDoc }, 1));
            if (channels.Count > 0)
                group.Children.Add(Pane(channels, 2));
            return new LayoutPanel(group);
        });

    private static LayoutDocumentPane Pane(IEnumerable<LayoutDocument> docs, double weight = 1)
    {
        var pane = new LayoutDocumentPane { DockWidth = new GridLength(weight, GridUnitType.Star) };
        foreach (var d in docs)
            pane.Children.Add(d);
        return pane;
    }

    private void ApplyLayout(Func<IReadOnlyList<LayoutDocument>, LayoutPanel> build)
    {
        var active = ActiveDocument();
        var docs = AllDocuments().ToList();
        foreach (var d in docs)
            Detach(d);
        Dock.Layout = new LayoutRoot { RootPanel = build(docs) };
        foreach (var d in Dock.Layout.Descendents().OfType<LayoutDocumentPane>())
            if (d.Children.Count > 0)
                d.SelectedContentIndex = 0;
        if (active is not null)
            active.IsActive = true;
    }

    private void WindowsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        ShowWindowMenu.Items.Clear();
        foreach (var doc in AllDocuments())
        {
            var item = new MenuItem { Header = doc.Title + (doc.IsFloating ? "  [отдельное окно]" : "") };
            var target = doc;
            item.Click += (_, _) => target.IsActive = true;
            ShowWindowMenu.Items.Add(item);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
            return;
        if (e.Key == Key.S)
        {
            e.Handled = true;
            DisplaySettings_Click(this, e);
            return;
        }
        var doc = ActiveDocument();
        if (doc is null)
            return;
        if (e.Key == Key.O)
        {
            ExecuteLayoutCommand(doc, "float");
            e.Handled = true;
        }
        else if (e.Key == Key.T)
        {
            ExecuteLayoutCommand(doc, "dock");
            e.Handled = true;
        }
    }

    // ───────────────────────────── Подключение ─────────────────────────────

    private static bool TryParseChannels(string text, out List<int> channels, out string error)
    {
        channels = new List<int>();
        error = "";
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out int dlci) || dlci is < 1 or > FrameConstants.MaxDlci)
            {
                error = $"Некорректный номер канала «{part}». Допустимо 1..63.";
                return false;
            }
            if (!channels.Contains(dlci))
                channels.Add(dlci);
        }
        return true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
            return;

        string port = PortCombo.Text.Trim();
        if (port.Length == 0)
        {
            MessageBox.Show(this, "Выберите COM-порт.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(BaudCombo.Text, out int baud) || baud <= 0)
        {
            MessageBox.Show(this, "Некорректная скорость порта.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryParseChannels(ChannelsBox.Text, out var channels, out var error))
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string cmux = CmuxCombo.Text.Trim();
        if (SkipCmuxCheck.IsChecked != true && CmuxParameters.Parse(cmux).Mode != 0)
        {
            MessageBox.Show(this, "Поддерживается только базовый режим: AT+CMUX=0,…", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings();
        foreach (var dlci in channels)
            EnsureChannel(dlci);

        try
        {
            _transport = port == EmulatorPort
                ? new EmulatorTransport()
                : SerialPortTransport.Open(new SerialPortSettings
                {
                    PortName = port,
                    BaudRate = baud,
                    Handshake = RtsCtsCheck.IsChecked == true ? IOHandshake.RequestToSend : IOHandshake.None,
                    DtrEnable = DtrCheck.IsChecked == true,
                });
        }
        catch (Exception ex)
        {
            _logPane.AppendInfo($"Не удалось открыть {port}: {ex.Message}", ChunkKind.Error);
            MessageBox.Show(this, $"Не удалось открыть {port}:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _logPane.AppendInfo($"==== Подключение: {_transport.Name} ====");
        var session = new MuxSession(_transport.Stream, new MuxSessionOptions
        {
            CmuxCommand = cmux,
            SkipCmuxCommand = SkipCmuxCheck.IsChecked == true,
            Channels = channels,
        });
        _session = session;
        Wire(session);
        _startCts = new CancellationTokenSource();
        UpdateUi();

        _starting = true;
        try
        {
            await session.StartAsync(_startCts.Token);
            _logPane.AppendInfo($"MUX запущен. Открыто каналов: {channels.Count(d => session.GetChannelState(d) == ChannelState.Open)} из {channels.Count}");
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(session);
        }
        catch (Exception ex)
        {
            await CleanupAsync(session);
            MessageBox.Show(this, ex.Message, "Ошибка запуска MUX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _starting = false;
        }
        // Модем мог закрыть сессию прямо во время запуска.
        if (_session is { State: MuxSessionState.Stopped or MuxSessionState.Faulted } ended)
            await CleanupAsync(ended);
        UpdateUi();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        var session = _session;
        if (session is null || _stopping)
            return;
        _stopping = true;
        UpdateUi();
        try
        {
            if (session.State == MuxSessionState.Initializing)
                _startCts?.Cancel();
            await session.StopAsync();
        }
        finally
        {
            await CleanupAsync(session);
            _stopping = false;
            UpdateUi();
        }
    }

    /// <summary>Освобождает сессию и порт. Идемпотентна: может вызываться из нескольких мест.</summary>
    private async Task CleanupAsync(MuxSession session)
    {
        if (!ReferenceEquals(_session, session))
            return;
        _session = null;
        var transport = _transport;
        _transport = null;
        _startCts?.Dispose();
        _startCts = null;

        Unwire(session);
        if (transport is not null)
        {
            try
            {
                await transport.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logPane.AppendInfo("Ошибка закрытия порта: " + ex.Message, ChunkKind.Error);
            }
        }
        foreach (var ch in _channels.Values)
            UpdateChannelUi(ch.Dlci, ChannelState.Closed);
        _logPane.AppendInfo($"==== Отключено{(session.TerminationReason is { } r ? ": " + r : "")} ====");
        UpdateUi();
    }

    private void Wire(MuxSession s)
    {
        s.Log += OnSessionLog;
        s.FrameTraffic += OnFrameTraffic;
        s.FrameError += OnFrameError;
        s.RawTraffic += OnRawTraffic;
        s.DataReceived += OnDataReceived;
        s.ChannelStateChanged += OnChannelStateChanged;
        s.StateChanged += OnSessionStateChanged;
    }

    private void Unwire(MuxSession s)
    {
        s.Log -= OnSessionLog;
        s.FrameTraffic -= OnFrameTraffic;
        s.FrameError -= OnFrameError;
        s.RawTraffic -= OnRawTraffic;
        s.DataReceived -= OnDataReceived;
        s.ChannelStateChanged -= OnChannelStateChanged;
        s.StateChanged -= OnSessionStateChanged;
    }

    // Обработчики вызываются из фонового потока. TerminalPane.Append потокобезопасен,
    // всё остальное маршалится в UI-поток.

    private void OnSessionLog(LogLevel level, string message)
    {
        string prefix = level switch
        {
            LogLevel.Debug => "[dbg] ",
            LogLevel.Warning => "[WARN] ",
            LogLevel.Error => "[ERROR] ",
            _ => "",
        };
        _logPane.Append(level == LogLevel.Error ? ChunkKind.Error : ChunkKind.Info, Array.Empty<byte>(), prefix + message);
    }

    private void OnFrameTraffic(TrafficDirection dir, MuxFrame frame)
        => _logPane.Append(dir == TrafficDirection.Rx ? ChunkKind.Rx : ChunkKind.Tx, frame.Raw, frame.ToString(),
            isDataFrame: frame.Dlci > 0 && frame.Type is FrameType.UIH or FrameType.UI);

    private void OnFrameError(FrameError error)
        => _logPane.Append(ChunkKind.Error, error.Data, $"{error.Message} ({error.Data.Length} байт): {Hex.Format(error.Data, 48)}");

    private void OnRawTraffic(TrafficDirection dir, byte[] data)
        => _logPane.Append(dir == TrafficDirection.Rx ? ChunkKind.Rx : ChunkKind.Tx, data, "AT-режим: " + Hex.ToPrintable(data, 256));

    private void OnDataReceived(int dlci, byte[] data)
    {
        if (_channels.TryGetValue(dlci, out var view))
            view.Pane.Append(ChunkKind.Rx, data);
        else
            Dispatcher.BeginInvoke(() => EnsureChannel(dlci).Pane.Append(ChunkKind.Rx, data));
    }

    private void OnChannelStateChanged(int dlci, ChannelState state)
    {
        if (dlci == 0)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            var view = EnsureChannel(dlci);
            if (view.Pane.ChannelState != state && state is ChannelState.Open or ChannelState.Closed or ChannelState.Failed)
                view.Pane.AppendInfo($"канал {StateName(state)}");
            UpdateChannelUi(dlci, state);
        });
    }

    private void OnSessionStateChanged(MuxSessionState state)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            var session = _session;
            // Сессию закрыл модем или отвалился порт — освобождаем порт сами.
            // Во время запуска и ручной остановки это делают Start_Click / StopAsync.
            if (session is not null && !_starting && !_stopping
                && state is MuxSessionState.Faulted or MuxSessionState.Stopped && session.State == state)
            {
                if (state == MuxSessionState.Faulted)
                    ShowHint("Соединение потеряно: " + session.TerminationReason);
                await CleanupAsync(session);
            }
            UpdateUi();
        });
    }

    private void UpdateChannelUi(int dlci, ChannelState state)
    {
        if (!_channels.TryGetValue(dlci, out var view))
            return;
        view.Pane.SetChannelState(state, _session?.State == MuxSessionState.Running);
        view.Doc.Title = ChannelTitle(dlci, state);
    }

    private async Task SendToChannelAsync(int dlci, byte[] data)
    {
        var session = _session ?? throw new InvalidOperationException("Нет подключения");
        await session.SendDataAsync(dlci, data);
    }

    private async Task OpenChannelAsync(int dlci)
    {
        var session = _session;
        if (session?.State != MuxSessionState.Running)
            return;
        try
        {
            await session.OpenChannelAsync(dlci);
        }
        catch (Exception ex)
        {
            _logPane.AppendInfo($"DLC{dlci}: {ex.Message}", ChunkKind.Error);
        }
    }

    private async Task CloseChannelAsync(int dlci)
    {
        var session = _session;
        if (session?.State != MuxSessionState.Running)
            return;
        try
        {
            await session.CloseChannelAsync(dlci);
        }
        catch (Exception ex)
        {
            _logPane.AppendInfo($"DLC{dlci}: {ex.Message}", ChunkKind.Error);
        }
    }

    private async void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ExtraDlcBox.Text, out int dlci) || dlci is < 1 or > FrameConstants.MaxDlci)
        {
            MessageBox.Show(this, "Номер канала: 1..63", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var view = EnsureChannel(dlci);
        view.Doc.IsActive = true;
        await OpenChannelAsync(dlci);
    }

    // ───────────────────────────── Состояние UI ─────────────────────────────

    private void UpdateUi()
    {
        var state = _session?.State ?? MuxSessionState.Idle;
        bool connected = _session is not null;
        bool running = state == MuxSessionState.Running;

        StartButton.IsEnabled = !connected;
        StopButton.IsEnabled = connected && !_stopping;
        AddChannelButton.IsEnabled = running;
        foreach (var c in new Control[] { PortCombo, BaudCombo, RtsCtsCheck, DtrCheck, CmuxCombo, SkipCmuxCheck, ChannelsBox })
            c.IsEnabled = !connected;

        foreach (var ch in _channels.Values)
            ch.Pane.SetChannelState(ch.Pane.ChannelState, running);

        (Brush brush, string text) = state switch
        {
            MuxSessionState.Initializing => ((Brush)Brushes.Gold, "Инициализация MUX…"),
            MuxSessionState.Running => (Brushes.LimeGreen, $"MUX работает · {_transport?.Name} · N1={_session!.MaxFrameSize}"),
            MuxSessionState.Stopping => (Brushes.Gold, "Остановка…"),
            _ => (Brushes.Gray, "Не подключено"),
        };
        SessionDot.Fill = brush;
        SessionStateText.Text = text;
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        var s = _session;
        if (s is not null)
            CountersText.Text = $"Кадров RX: {s.RxFrames}  TX: {s.TxFrames}  Ошибок: {s.Errors}";
    }

    private void ShowHint(string text)
    {
        HintText.Text = text;
        HintText.Foreground = Brushes.DarkRed;
    }

    // ───────────────────────────── Прочее ─────────────────────────────

    private void SaveSettings()
    {
        _settings.PortName = PortCombo.Text.Trim();
        if (int.TryParse(BaudCombo.Text, out int baud))
            _settings.BaudRate = baud;
        _settings.HardwareFlowControl = RtsCtsCheck.IsChecked == true;
        _settings.Dtr = DtrCheck.IsChecked == true;
        _settings.CmuxCommand = CmuxCombo.Text.Trim();
        _settings.SkipCmux = SkipCmuxCheck.IsChecked == true;
        _settings.Channels = ChannelsBox.Text.Trim();
        _settings.Save();
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var doc = ActiveDocument();
        if (doc?.Content is not TerminalPane pane)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить содержимое вкладки",
            FileName = $"{(pane.Dlci == 0 ? "system-log" : $"dlc{pane.Dlci}")}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Текст (*.txt)|*.txt|Все файлы (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            File.WriteAllText(dialog.FileName, pane.GetPlainText());
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveSettings();
        if (_session is null)
            return;
        // Сначала корректно закрываем MUX (CLD), чтобы модем вернулся в AT-режим.
        e.Cancel = true;
        if (_closeRequested)
            return;
        _closeRequested = true;
        await StopAsync();
        await Dispatcher.BeginInvoke(Close);
    }
}

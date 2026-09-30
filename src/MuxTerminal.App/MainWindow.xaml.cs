using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private LayoutDocument _logDoc;
    // Доступ из фонового потока сессии (маршрутизация данных) — поэтому ConcurrentDictionary.
    private readonly ConcurrentDictionary<int, ChannelView> _channels = new();
    private readonly DispatcherTimer _statusTimer;

    private IMuxTransport? _transport;
    private MuxSession? _session;
    private CancellationTokenSource? _startCts;
    private readonly CollectorPane _collector = new();
    private LayoutDocument _collectorDoc;
    private List<CompiledRule> _rules = new();
    private LayoutDocument? _lastActiveDoc;
    private SessionLogger? _logger;
    private ConnectParams? _lastConnect;
    private CancellationTokenSource? _reconnectCts;
    private string? _reconnectStatus;
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
        _logPane.LayoutCommandRequested += OnPaneLayoutCommand;
        _logPane.Collected += OnCollected;

        _collectorDoc = CreateDocument("Копилка", "collector", _collector,
            "Все строки, совпавшие с правилами поиска (меню «Настройки», «Отображение и подсветка»)");
        _collector.LayoutCommandRequested += (_, cmd) => ExecuteLayoutCommand(_collectorDoc, cmd);
        _collector.NavigateRequested += NavigateTo;
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            UpdateCounters();
            ResetUnreadForVisibleTabs();
        }, Dispatcher);
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        RestorePlacement();
    }

    /// <summary>Документ вкладки ищем по содержимому: после восстановления раскладки объекты LayoutDocument новые.</summary>
    private void OnPaneLayoutCommand(TerminalPane pane, string command)
    {
        if (AllDocuments().FirstOrDefault(d => d.Content == pane) is { } doc)
            ExecuteLayoutCommand(doc, command);
    }

    // ───────────────────────────── Положение окна и раскладка ─────────────────────────────

    private void RestorePlacement()
    {
        if (_settings.Window is not { Width: > 200, Height: > 200 } w)
            return;
        // Окно должно попадать на экран (монитор могли отключить).
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(w.Left, w.Top, w.Width, w.Height);
        if (!screen.IntersectsWith(rect) || rect.Top < screen.Top - 10)
            return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = w.Left;
        Top = w.Top;
        Width = Math.Min(w.Width, screen.Width);
        Height = Math.Min(w.Height, screen.Height);
        if (w.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty)
            return;
        _settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    private void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            new AvalonDock.Layout.Serialization.XmlLayoutSerializer(Dock).Serialize(AppPaths.Layout);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "layout (save)");
        }
    }

    /// <summary>
    /// Восстанавливает раскладку прошлого запуска: вкладки, разделения, отдельные окна.
    /// Вкладки сопоставляются по ContentId (log, collector, dlcN); новые объекты LayoutDocument
    /// подставляются вместо наших, вкладки без места в сохранённой раскладке добавляются в основную группу.
    /// </summary>
    private bool RestoreLayout()
    {
        if (!File.Exists(AppPaths.Layout))
            return false;
        var ours = AllDocuments().Where(d => d.ContentId is not null).ToDictionary(d => d.ContentId!, d => d);
        try
        {
            var serializer = new AvalonDock.Layout.Serialization.XmlLayoutSerializer(Dock);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                if (args.Model.ContentId is { } id && ours.TryGetValue(id, out var doc))
                    args.Content = doc.Content;
                else
                    args.Cancel = true; // канала больше нет в списке
            };
            serializer.Deserialize(AppPaths.Layout);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "layout (restore)");
            return false;
        }

        var restored = Dock.Layout.Descendents().OfType<LayoutDocument>()
            .Concat(Dock.Layout.FloatingWindows.SelectMany(f => f.Descendents().OfType<LayoutDocument>()))
            .Where(d => d.Content is not null)
            .Distinct()
            .ToList();
        foreach (var doc in restored)
        {
            var old = ours.Values.FirstOrDefault(o => o.Content == doc.Content);
            if (old is null)
                continue;
            doc.CanClose = false;
            doc.CanFloat = true;
            doc.ToolTip = old.ToolTip;
            doc.Title = old.Title;
            if (old == _logDoc)
                _logDoc = doc;
            else if (old == _collectorDoc)
                _collectorDoc = doc;
            else if (doc.Content is TerminalPane pane && _channels.TryGetValue(pane.Dlci, out var view))
                _channels[pane.Dlci] = view with { Doc = doc };
        }
        foreach (var missing in AllDocuments().Where(d => d.Parent is null).ToList())
            MainDocumentPane().Children.Add(missing);
        return true;
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

        AutoReconnectCheck.IsChecked = _settings.AutoReconnect;
        SessionLogMenu.IsChecked = _settings.SessionLog;
        RawFramesLogMenu.IsChecked = _settings.SessionLogRawFrames;
        RawFramesLogMenu.IsEnabled = _settings.SessionLog;
        RefreshProfiles(_settings.LastProfile);

        var pane = MainDocumentPane();
        pane.Children.Add(_logDoc);
        pane.Children.Add(_collectorDoc);
        ApplyDisplay(_settings.Display);
        if (TryParseChannels(_settings.Channels, out var channels, out _))
            foreach (var dlci in channels)
                EnsureChannel(dlci);
        if (!RestoreLayout())
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
        pane.LayoutCommandRequested += OnPaneLayoutCommand;
        pane.RenameRequested += p => RenameChannel(p.Dlci);
        pane.NewLines += OnPaneNewLines;
        pane.SourceName = ChannelLabel(dlci);
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

    /// <summary>«DLC 1 · AT» — имя канала без состояния и счётчиков.</summary>
    private string ChannelLabel(int dlci)
    {
        string name = ChannelName(dlci);
        return name.Length > 0 ? $"DLC {dlci} · {name}" : $"DLC {dlci}";
    }

    private string ChannelTitle(int dlci, ChannelState state)
    {
        string title = ChannelLabel(dlci);
        if (state != ChannelState.Open)
            title += $" ({StateName(state)})";
        if (_unread.TryGetValue(dlci, out int unread) && unread > 0)
            title += $" [{(unread > 999 ? "999+" : unread.ToString(CultureInfo.InvariantCulture))}]";
        return title;
    }

    // ───────────────────────────── Непрочитанное ─────────────────────────────

    private readonly Dictionary<int, int> _unread = new();

    /// <summary>Новые строки на невидимой вкладке — число в квадратных скобках в заголовке.</summary>
    private void OnPaneNewLines(TerminalPane pane, int count)
    {
        if (!_channels.TryGetValue(pane.Dlci, out var view) || view.Doc.IsSelected)
            return;
        _unread.TryGetValue(pane.Dlci, out int n);
        _unread[pane.Dlci] = n + count;
        view.Doc.Title = ChannelTitle(pane.Dlci, pane.ChannelState);
    }

    /// <summary>Вкладку открыли — сбрасываем счётчик (проверяется по таймеру статуса).</summary>
    private void ResetUnreadForVisibleTabs()
    {
        foreach (var dlci in _unread.Where(x => x.Value > 0).Select(x => x.Key).ToList())
        {
            if (_channels.TryGetValue(dlci, out var view) && view.Doc.IsSelected)
            {
                _unread[dlci] = 0;
                view.Doc.Title = ChannelTitle(dlci, view.Pane.ChannelState);
            }
        }
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
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            RenameActiveChannel();
            return;
        }
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
                error = $"Некорректный номер канала «{part}». Допустимо 1..61 (62 и 63 зарезервированы стандартом).";
                return false;
            }
            if (!channels.Contains(dlci))
                channels.Add(dlci);
        }
        return true;
    }

    /// <summary>Параметры подключения, прочитанные с формы (для повторного подключения).</summary>
    private sealed record ConnectParams(
        string Port, int Baud, bool RtsCts, bool Dtr, string Cmux, bool SkipCmux, List<int> Channels, int? SwitchBaud);

    private bool TryReadConnectParams(out ConnectParams p)
    {
        p = null!;
        string port = PortCombo.Text.Trim();
        if (port.Length == 0)
        {
            MessageBox.Show(this, "Выберите COM-порт.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!int.TryParse(BaudCombo.Text, out int baud) || baud <= 0)
        {
            MessageBox.Show(this, "Некорректная скорость порта.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!TryParseChannels(ChannelsBox.Text, out var channels, out var error))
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        string cmux = CmuxCombo.Text.Trim();
        bool skip = SkipCmuxCheck.IsChecked == true;
        var cmuxParams = CmuxParameters.Parse(cmux);
        if (!skip && cmuxParams.Mode != 0)
        {
            MessageBox.Show(this, "Поддерживается только базовый режим: AT+CMUX=0,…", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // В AT+CMUX указана другая скорость: модем переключится на неё сразу после OK.
        int? switchBaud = null;
        if (!skip && port != EmulatorPort && cmuxParams.PortBaudRate is { } newBaud && newBaud != baud)
        {
            var answer = MessageBox.Show(this,
                $"В команде указана скорость порта {newBaud} (параметр port_speed), а порт открывается на {baud}.\n" +
                $"После ответа OK модем переключится на {newBaud}, и без переключения порта связь пропадёт.\n\n" +
                $"Да — переключить порт на {newBaud} автоматически после OK\n" +
                "Нет — не переключать (модем не меняет скорость)\n" +
                "Отмена — исправить параметры",
                "Скорость порта", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
                return false;
            if (answer == MessageBoxResult.Yes)
                switchBaud = newBaud;
        }

        p = new ConnectParams(port, baud, RtsCtsCheck.IsChecked == true, DtrCheck.IsChecked == true, cmux, skip, channels, switchBaud);
        return true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _reconnectCts is not null)
            return;
        if (!TryReadConnectParams(out var p))
            return;
        SaveSettings();
        await ConnectAsync(p, interactive: true);
    }

    /// <summary>Открывает порт и запускает MUX. Возвращает true, если сеанс работает.</summary>
    private async Task<bool> ConnectAsync(ConnectParams p, bool interactive)
    {
        foreach (var dlci in p.Channels)
            EnsureChannel(dlci);

        IMuxTransport transport;
        try
        {
            transport = p.Port == EmulatorPort
                ? new EmulatorTransport()
                : SerialPortTransport.Open(new SerialPortSettings
                {
                    PortName = p.Port,
                    BaudRate = p.Baud,
                    Handshake = p.RtsCts ? IOHandshake.RequestToSend : IOHandshake.None,
                    DtrEnable = p.Dtr,
                });
        }
        catch (Exception ex)
        {
            _logPane.AppendInfo($"Не удалось открыть {p.Port}: {ex.Message}", ChunkKind.Error);
            if (interactive)
                MessageBox.Show(this, $"Не удалось открыть {p.Port}:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _transport = transport;

        if (_settings.SessionLog)
        {
            try
            {
                _logger = new SessionLogger(
                    AppInfo.Diagnostics +
                    $"Порт: {transport.Name}, RTS/CTS: {p.RtsCts}, DTR: {p.Dtr}\r\n" +
                    $"Команда: {(p.SkipCmux ? "(модем уже в MUX)" : p.Cmux)}, каналы: {string.Join(",", p.Channels)}",
                    _settings.SessionLogRawFrames);
            }
            catch (Exception ex)
            {
                _logPane.AppendInfo("Журнал сеанса не записывается: " + ex.Message, ChunkKind.Error);
            }
        }

        _logPane.AppendInfo($"==== Подключение: {transport.Name} ====");
        var session = new MuxSession(transport.Stream, new MuxSessionOptions
        {
            CmuxCommand = p.Cmux,
            SkipCmuxCommand = p.SkipCmux,
            Channels = p.Channels,
            OnMuxEntered = p.SwitchBaud is { } newBaud && transport is SerialPortTransport serial
                ? _ =>
                {
                    serial.SetBaudRate(newBaud);
                    _logPane.AppendInfo($"Порт переключён на {newBaud} вслед за модемом");
                    _logger?.System(LogLevel.Info, $"Порт переключён на {newBaud}");
                    return Task.CompletedTask;
                }
                : null,
        });
        _session = session;
        _lastConnect = p;
        Wire(session);
        _startCts = new CancellationTokenSource();
        UpdateUi();

        bool ok = false;
        _starting = true;
        try
        {
            await session.StartAsync(_startCts.Token);
            ok = true;
            _logPane.AppendInfo($"MUX запущен. Открыто каналов: {p.Channels.Count(d => session.GetChannelState(d) == ChannelState.Open)} из {p.Channels.Count}");
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(session);
        }
        catch (Exception ex)
        {
            await CleanupAsync(session);
            if (interactive)
                MessageBox.Show(this, ex.Message, "Ошибка запуска MUX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _starting = false;
        }
        // Модем мог закрыть сессию прямо во время запуска.
        if (_session is { State: MuxSessionState.Stopped or MuxSessionState.Faulted } ended)
        {
            ok = false;
            await CleanupAsync(ended);
        }
        UpdateUi();
        return ok;
    }

    /// <summary>Связь потеряна: пытаемся подключиться снова каждые 5 секунд, пока не получится или не нажмут «Стоп».</summary>
    private async Task ReconnectLoopAsync(ConnectParams p)
    {
        if (_reconnectCts is not null)
            return;
        var cts = _reconnectCts = new CancellationTokenSource();
        int attempt = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                attempt++;
                for (int s = 5; s > 0 && !cts.IsCancellationRequested; s--)
                {
                    _reconnectStatus = $"Связь потеряна. Переподключение через {s} с (попытка {attempt})…";
                    UpdateUi();
                    await Task.Delay(1000);
                }
                if (cts.IsCancellationRequested)
                    break;
                _reconnectStatus = $"Переподключение (попытка {attempt})…";
                UpdateUi();
                _logPane.AppendInfo($"Переподключение, попытка {attempt}");
                if (await ConnectAsync(p, interactive: false))
                {
                    ShowHint($"Связь восстановлена (попытка {attempt})", error: false);
                    break;
                }
            }
        }
        finally
        {
            _reconnectCts = null;
            _reconnectStatus = null;
            cts.Dispose();
            UpdateUi();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        _reconnectCts?.Cancel(); // «Стоп» прекращает и попытки переподключения
        await StopAsync();
    }

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
        string reason = session.TerminationReason is { } r ? ": " + r : "";
        _logPane.AppendInfo($"==== Отключено{reason} ====");

        var logger = _logger;
        _logger = null;
        if (logger is not null)
        {
            logger.System(LogLevel.Info, "Отключено" + reason);
            await Task.Run(logger.Dispose);
        }
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
        _logger?.System(level, message);
    }

    private void OnFrameTraffic(TrafficDirection dir, MuxFrame frame)
    {
        _logPane.Append(dir == TrafficDirection.Rx ? ChunkKind.Rx : ChunkKind.Tx, frame.Raw, frame.ToString(),
            isDataFrame: frame.Dlci > 0 && frame.Type is FrameType.UIH or FrameType.UI);
        _logger?.Frame(dir, frame);
    }

    private void OnFrameError(FrameError error)
    {
        _logPane.Append(ChunkKind.Error, error.Data, $"{error.Message} ({error.Data.Length} байт): {Hex.Format(error.Data, 48)}");
        _logger?.FrameError(error);
    }

    private void OnRawTraffic(TrafficDirection dir, byte[] data)
    {
        _logPane.Append(dir == TrafficDirection.Rx ? ChunkKind.Rx : ChunkKind.Tx, data, "AT-режим: " + Hex.ToPrintable(data, 256));
        _logger?.Data("AT", dir, data);
    }

    private void OnDataReceived(int dlci, byte[] data)
    {
        _logger?.Data($"DLC{dlci}", TrafficDirection.Rx, data);
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
                if (state == MuxSessionState.Faulted && AutoReconnectCheck.IsChecked == true && _lastConnect is { } p)
                    await ReconnectLoopAsync(p);
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
        _logger?.Data($"DLC{dlci}", TrafficDirection.Tx, data);
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
            MessageBox.Show(this, "Номер канала: 1..61", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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

        bool reconnecting = _reconnectCts is not null;
        StartButton.IsEnabled = !connected && !reconnecting;
        StopButton.IsEnabled = (connected && !_stopping) || reconnecting;
        AddChannelButton.IsEnabled = running;
        foreach (var c in new Control[] { PortCombo, BaudCombo, RtsCtsCheck, DtrCheck, CmuxCombo, SkipCmuxCheck, ChannelsBox, ProfileCombo })
            c.IsEnabled = !connected && !reconnecting;

        foreach (var ch in _channels.Values)
            ch.Pane.SetChannelState(ch.Pane.ChannelState, running);

        (Brush brush, string text) = state switch
        {
            MuxSessionState.Initializing => ((Brush)Brushes.Gold, "Инициализация MUX…"),
            MuxSessionState.Running => (Brushes.LimeGreen, $"MUX работает · {_transport?.Name} · N1={_session!.MaxFrameSize}"),
            MuxSessionState.Stopping => (Brushes.Gold, "Остановка…"),
            _ when _reconnectStatus is not null => (Brushes.OrangeRed, _reconnectStatus),
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

    private void ShowHint(string text, bool error = true)
    {
        HintText.Text = text;
        HintText.Foreground = error ? Brushes.DarkRed : Brushes.DarkGreen;
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
        _settings.AutoReconnect = AutoReconnectCheck.IsChecked == true;
        _settings.LastProfile = (ProfileCombo.SelectedItem as ConnectionProfile)?.Name;
        _settings.Save();
    }

    // ───────────────────────────── Журналы ─────────────────────────────

    private void SessionLogMenu_Click(object sender, RoutedEventArgs e)
    {
        _settings.SessionLog = SessionLogMenu.IsChecked;
        _settings.SessionLogRawFrames = RawFramesLogMenu.IsChecked;
        RawFramesLogMenu.IsEnabled = SessionLogMenu.IsChecked;
        _settings.Save();
        if (_session is not null)
            ShowHint("Настройка журнала применится со следующего подключения.", error: false);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть " + path + ":\n" + ex.Message, "Журналы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Logs);
        OpenInExplorer(AppPaths.Logs);
    }

    private void OpenCrashLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.CrashLog))
            OpenInExplorer(AppPaths.CrashLog);
        else
            MessageBox.Show(this, "Ошибок не было — журнал ошибок пуст.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ───────────────────────────── Профили подключения ─────────────────────────────

    private bool _loadingProfile;

    private void RefreshProfiles(string? select)
    {
        _loadingProfile = true;
        ProfileCombo.Items.Clear();
        foreach (var profile in _settings.Profiles.OrderBy(x => x.Name))
            ProfileCombo.Items.Add(profile);
        ProfileCombo.SelectedItem = _settings.Profiles.FirstOrDefault(x => x.Name == select);
        _loadingProfile = false;
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || ProfileCombo.SelectedItem is not ConnectionProfile p)
            return;
        PortCombo.Text = p.PortName;
        BaudCombo.Text = p.BaudRate.ToString(CultureInfo.InvariantCulture);
        RtsCtsCheck.IsChecked = p.HardwareFlowControl;
        DtrCheck.IsChecked = p.Dtr;
        CmuxCombo.Text = p.CmuxCommand;
        SkipCmuxCheck.IsChecked = p.SkipCmux;
        ChannelsBox.Text = p.Channels;
        SaveSettings();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        string current = (ProfileCombo.SelectedItem as ConnectionProfile)?.Name ?? "";
        var name = InputDialog.Ask(this, "Профиль подключения", "Название профиля (например, модель модема):", current);
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name!.Trim();
        SaveSettings();
        _settings.Profiles.RemoveAll(x => x.Name == name);
        _settings.Profiles.Add(_settings.ToProfile(name));
        RefreshProfiles(name);
        SaveSettings();
        ShowHint($"Профиль «{name}» сохранён", error: false);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not ConnectionProfile p)
            return;
        if (MessageBox.Show(this, $"Удалить профиль «{p.Name}»?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _settings.Profiles.Remove(p);
        RefreshProfiles(null);
        SaveSettings();
    }

    // ───────────────────────────── Имена вкладок ─────────────────────────────

    private void RenameTab_Click(object sender, RoutedEventArgs e) => RenameActiveChannel();

    private void RenameActiveChannel()
    {
        if (ActiveDocument()?.Content is not TerminalPane { Dlci: > 0 } pane)
        {
            MessageBox.Show(this, "Выберите вкладку канала (DLC 1..61).", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RenameChannel(pane.Dlci);
    }

    private void RenameChannel(int dlci)
    {
        var name = InputDialog.Ask(this, "Имя вкладки", $"Имя канала DLC {dlci} (пусто — без имени):", ChannelName(dlci));
        if (name is null)
            return;
        _settings.ChannelNames[dlci] = name.Trim();
        _settings.Save();
        if (_channels.TryGetValue(dlci, out var view))
        {
            view.Pane.SourceName = ChannelLabel(dlci);
            UpdateChannelUi(dlci, view.Pane.ChannelState);
        }
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
        _reconnectCts?.Cancel();
        SavePlacement();
        SaveSettings();
        if (_session is null)
        {
            SaveLayout();
            return;
        }
        // Сначала корректно закрываем MUX (CLD), чтобы модем вернулся в AT-режим.
        e.Cancel = true;
        if (_closeRequested)
            return;
        _closeRequested = true;
        await StopAsync();
        await Dispatcher.BeginInvoke(Close);
    }
}

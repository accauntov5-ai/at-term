using System.Collections.Concurrent;
using System.Text;
using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Session;

public sealed class MuxException : Exception
{
    public MuxException(string message) : base(message) { }
}

/// <summary>Ответ модема на AT-команду в текстовом режиме.</summary>
public sealed record AtResponse(string FinalResult, string Text, bool TimedOut)
{
    public bool IsOk => !TimedOut && FinalResult == "OK";
    public override string ToString() => TimedOut ? "таймаут" : FinalResult;
}

/// <summary>
/// Сессия мультиплексора GSM 07.10 (Basic Option) поверх одного байтового потока (COM-порта).
/// Мы — инициатор (TE): отправляем AT+CMUX, открываем DLC0, затем каналы данных.
///
/// Потоки:
///  * цикл чтения работает в фоновой задаче и синхронно разбирает кадры;
///  * запись любых кадров сериализуется одним <see cref="SemaphoreSlim"/> — кадры разных DLC
///    никогда не перемешиваются; данные одного канала дополнительно упорядочены своим замком,
///    поэтому длинная посылка, нарезанная на кадры по N1, не перемежается с другой посылкой того же канала.
/// События вызываются из фонового потока — UI должен сам маршалить их в свой поток.
/// </summary>
public sealed class MuxSession : IAsyncDisposable
{
    private sealed class Channel
    {
        public Channel(int dlci) => Dlci = dlci;
        public int Dlci { get; }
        public volatile ChannelState State;
        public volatile bool RemoteFlowOff;
        public TaskCompletionSource<FrameType>? Pending;
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    private static readonly TimeSpan FlowControlTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private MuxSessionOptions _options;
    private CancellationTokenSource? _muxCts;
    private bool _keepPortOpen;
    private readonly FrameParser _parser = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Channel> _channels = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _atLock = new();
    private readonly List<byte> _atBuffer = new();

    private Task? _readTask;
    private volatile bool _muxMode;
    /// <summary>
    /// «Модем уже в MUX»: мы лишь подключились к работающему мультиплексору. Никаких служебных команд
    /// от себя (SABM, DISC, MSC, CLD) — только разбор кадров, данные в каналы и ответы на команды модема.
    /// </summary>
    private volatile bool _attached;
    /// <summary>OK пришёл с «\r», а «\n» ещё в пути — первый байт MUX-потока может быть этим «\n».</summary>
    private bool _swallowLf;
    private volatile bool _globalFlowOff;
    private TaskCompletionSource<AtResponse>? _atTcs;
    private bool _switchToMuxOnOk;
    private TaskCompletionSource<bool>? _cldTcs;
    private int _shutdown;
    private long _rxFrames, _txFrames, _errors;

    public MuxSession(Stream stream, MuxSessionOptions options)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ApplyOptions(options);
        _parser.FrameReceived += OnFrameReceived;
        _parser.Error += OnParserError;
    }

    public event Action<LogLevel, string>? Log;
    public event Action<TrafficDirection, MuxFrame>? FrameTraffic;
    public event Action<FrameError>? FrameError;
    /// <summary>Сырые данные в текстовом (AT) режиме, до перехода в MUX.</summary>
    public event Action<TrafficDirection, byte[]>? RawTraffic;
    public event Action<int, byte[]>? DataReceived;
    public event Action<int, ChannelState>? ChannelStateChanged;
    public event Action<MuxSessionState>? StateChanged;

    public MuxSessionState State { get; private set; } = MuxSessionState.Idle;
    public string? TerminationReason { get; private set; }
    /// <summary>N1 — максимальный размер Payload исходящего кадра.</summary>
    public int MaxFrameSize { get; private set; }
    public bool IsMuxMode => _muxMode;
    /// <summary>Подключены к уже работающему MUX (см. <see cref="MuxSessionOptions.SkipCmuxCommand"/>).</summary>
    public bool IsAttached => _attached;
    public long RxFrames => Interlocked.Read(ref _rxFrames);
    public long TxFrames => Interlocked.Read(ref _txFrames);
    public long Errors => Interlocked.Read(ref _errors);

    public ChannelState GetChannelState(int dlci)
        => _channels.TryGetValue(dlci, out var ch) ? ch.State : ChannelState.Closed;

    // ───────────────────────────── Запуск ─────────────────────────────

    private void ApplyOptions(MuxSessionOptions options)
    {
        _options = options;
        MaxFrameSize = options.MaxFrameSize;
        // Заголовок с запасом: модемы иногда игнорируют N1 для служебных кадров.
        _parser.MaxPayloadLength = Math.Max(MaxFrameSize, 127);
    }

    /// <summary>
    /// Открывает порт в обычном AT-режиме: принятые байты приходят в <see cref="RawTraffic"/>,
    /// отправка — <see cref="SendRawAsync"/>. Мультиплексор затем запускается <see cref="StartAsync(CancellationToken)"/>,
    /// а <see cref="StopMuxAsync"/> возвращает сюда же, не закрывая порт.
    /// </summary>
    public Task OpenAsync()
    {
        if (State != MuxSessionState.Idle)
            throw new InvalidOperationException("Порт уже открыт");
        _keepPortOpen = true;
        _muxMode = false;
        _readTask = Task.Run(ReadLoopAsync);
        SetState(MuxSessionState.PortOpen);
        Emit(LogLevel.Info, "Порт открыт, модем в AT-режиме");
        return Task.CompletedTask;
    }

    /// <summary>Отправка в порт в AT-режиме (без мультиплексора).</summary>
    public async Task SendRawAsync(byte[] data, CancellationToken ct = default)
    {
        if (State != MuxSessionState.PortOpen)
            throw new InvalidOperationException(State == MuxSessionState.Running
                ? "Порт в режиме MUX — используйте вкладки каналов"
                : "Порт не открыт");
        await WriteRawAsync(data, ct);
    }

    /// <summary>Запуск MUX с новыми параметрами (из открытого порта — можно менять команду и каналы).</summary>
    public Task StartAsync(MuxSessionOptions options, CancellationToken cancellationToken = default)
    {
        if (State is not (MuxSessionState.Idle or MuxSessionState.PortOpen))
            throw new InvalidOperationException("Мультиплексор уже запущен");
        ApplyOptions(options);
        return StartAsync(cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State is not (MuxSessionState.Idle or MuxSessionState.PortOpen))
            throw new InvalidOperationException("Сессия уже запускалась");

        var cmux = CmuxParameters.Parse(_options.CmuxCommand);
        if (!_options.SkipCmuxCommand && cmux.Mode != 0)
            throw new NotSupportedException("Поддерживается только Basic Option (AT+CMUX=0,...)");

        bool fromPort = State == MuxSessionState.PortOpen;
        SetState(MuxSessionState.Initializing);
        _muxCts?.Dispose();
        _muxCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, _muxCts.Token);
        var ct = linked.Token;

        // Состояние предыдущего сеанса MUX (если порт был открыт и MUX уже запускался).
        _channels.Clear();
        _globalFlowOff = false;
        _swallowLf = false;
        _parser.Reset();
        _muxMode = _options.SkipCmuxCommand;
        _attached = _options.SkipCmuxCommand;
        if (!fromPort)
            _readTask = Task.Run(ReadLoopAsync);

        try
        {
            if (_attached)
            {
                // Модем уже в MUX, каналы открыты до нас: ничего не отправляем, просто раскладываем кадры.
                // SABM здесь вреден: модем, у которого канал уже открыт, может не ответить, и запуск «провалится».
                SetChannelState(GetChannel(0), ChannelState.Open);
                foreach (int dlci in _options.Channels.Distinct().Where(d => d is > 0 and <= FrameConstants.MaxDlci))
                    SetChannelState(GetChannel(dlci), ChannelState.Open);
                Emit(LogLevel.Info, $"Подключение к работающему MUX (AT+CMUX, SABM и MSC не отправляются), N1={MaxFrameSize}");
                SetState(MuxSessionState.Running);
                return;
            }

            if (_options.SendAtProbe)
            {
                bool alive = false;
                for (int i = 0; i < 3 && !alive; i++)
                    alive = (await SendAtCommandAsync("AT", false, TimeSpan.FromSeconds(1), ct)).IsOk;
                if (!alive && _options.RecoverStuckMux)
                    alive = await TryRecoverFromStuckMuxAsync(ct);
                if (!alive)
                    Emit(LogLevel.Warning, "Модем не ответил OK на \"AT\" — пробую AT+CMUX всё равно");
            }

            var response = await SendAtCommandAsync(_options.CmuxCommand, true, _options.AtTimeout, ct);
            if (!response.IsOk)
                throw new MuxException($"Модем не перешёл в MUX: ответ на {_options.CmuxCommand} — {response}");

            Emit(LogLevel.Info, $"Модем в режиме MUX, N1={MaxFrameSize}");
            if (_options.OnMuxEntered is { } hook)
                await hook(ct); // например, переключить скорость порта вслед за модемом
            await Task.Delay(_options.SwitchDelay, ct);

            if (!await OpenChannelCoreAsync(0, ct))
                throw new MuxException("Канал управления DLC0 не открыт: модем не ответил UA на SABM");

            foreach (int dlci in _options.Channels.Distinct().Where(d => d is > 0 and <= FrameConstants.MaxDlci))
                await OpenChannelAsync(dlci, ct);

            SetState(MuxSessionState.Running);
        }
        catch (Exception ex)
        {
            if (_keepPortOpen && !_cts.IsCancellationRequested)
            {
                // Порт жив — возвращаемся в AT-режим; если модем уже перешёл в MUX, закрываем его.
                await ReturnToPortModeAsync(ex is OperationCanceledException ? "Запуск MUX отменён" : ex.Message, closeMux: _muxMode && !_attached);
            }
            else if (ex is OperationCanceledException)
            {
                await ShutdownAsync(MuxSessionState.Stopped, "Запуск отменён");
            }
            else
            {
                await ShutdownAsync(MuxSessionState.Faulted, ex.Message);
            }
            throw;
        }
    }

    /// <summary>Выход из MUX с сохранением открытого порта (при необходимости — с командами закрытия MUX).</summary>
    private async Task ReturnToPortModeAsync(string reason, bool closeMux)
    {
        if (closeMux)
        {
            try
            {
                var close = new List<byte> { FrameConstants.Flag };
                close.AddRange(FrameEncoder.Uih(0, ControlMessage.CloseDown().Encode()));
                close.AddRange(FrameEncoder.Disc(0));
                await WriteRawAsync(close.ToArray(), _cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
            {
                Emit(LogLevel.Warning, "Не удалось отправить закрытие MUX: " + ex.Message);
            }
        }
        LeaveMuxState(reason);
    }

    /// <summary>Сбрасывает всё, что относится к MUX, и переводит сессию в «порт открыт».</summary>
    private void LeaveMuxState(string reason)
    {
        _muxMode = false;
        _attached = false;
        _swallowLf = false;
        _parser.Reset();
        _cldTcs?.TrySetResult(false);
        foreach (var ch in _channels.Values)
        {
            ch.Pending?.TrySetCanceled();
            if (ch.State != ChannelState.Closed)
                SetChannelState(ch, ChannelState.Closed);
        }
        Emit(LogLevel.Info, reason);
        SetState(MuxSessionState.PortOpen);
    }

    /// <summary>Модем сам закрыл MUX (CLD / DISC DLC0).</summary>
    private Task OnModemClosedMuxAsync(string reason)
        => _keepPortOpen ? Task.Run(() => LeaveMuxState(reason + ", порт остаётся открытым")) : ShutdownAsync(MuxSessionState.Stopped, reason);

    /// <summary>
    /// Модем молчит на AT — обычно он остался в режиме MUX после аварийного завершения программы
    /// или отключения кабеля. Отправляем ему закрытие мультиплексора (CLD и DISC на DLC0) и проверяем AT снова.
    /// Если модем был в AT-режиме, эти байты он просто проигнорирует как мусор.
    /// </summary>
    private async Task<bool> TryRecoverFromStuckMuxAsync(CancellationToken ct)
    {
        Emit(LogLevel.Warning, "Модем не отвечает на AT — возможно, он остался в режиме MUX после сбоя. Отправляю закрытие MUX (CLD, DISC)…");
        var close = new List<byte> { FrameConstants.Flag, FrameConstants.Flag };
        close.AddRange(FrameEncoder.Uih(0, ControlMessage.CloseDown().Encode()));
        close.AddRange(FrameEncoder.Disc(0));
        await WriteRawAsync(close.ToArray(), ct);
        await Task.Delay(_options.RecoveryDelay, ct);

        for (int i = 0; i < 3; i++)
        {
            if ((await SendAtCommandAsync("AT", false, TimeSpan.FromSeconds(1), ct)).IsOk)
            {
                Emit(LogLevel.Info, "Модем вернулся в AT-режим");
                return true;
            }
        }
        return false;
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(data, 0, data.Length, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
        RawTraffic?.Invoke(TrafficDirection.Tx, data);
    }

    /// <summary>Отправляет AT-команду в текстовом режиме и ждёт финального результата.</summary>
    private async Task<AtResponse> SendAtCommandAsync(string command, bool switchToMuxOnOk, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AtResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_atLock)
        {
            _atBuffer.Clear();
            _atTcs = tcs;
            _switchToMuxOnOk = switchToMuxOnOk;
        }

        var bytes = Encoding.ASCII.GetBytes(command + "\r");
        Emit(LogLevel.Info, $"AT >> {command}");
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, 0, bytes.Length, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
        RawTraffic?.Invoke(TrafficDirection.Tx, bytes);

        try
        {
            var response = await tcs.Task.WithTimeout(timeout, ct);
            Emit(response.IsOk ? LogLevel.Info : LogLevel.Warning, $"AT << {response.Text.Trim().Replace("\r", "").Replace("\n", " | ")}");
            return response;
        }
        catch (TimeoutException)
        {
            Emit(LogLevel.Warning, $"AT: нет ответа на {command}");
            return new AtResponse("", "", true);
        }
        finally
        {
            lock (_atLock)
            {
                if (_atTcs == tcs)
                    _atTcs = null;
            }
        }
    }

    // ───────────────────────────── Каналы ─────────────────────────────

    /// <summary>Открывает канал (SABM → UA). Возвращает false, если модем отказал или не ответил.</summary>
    public async Task<bool> OpenChannelAsync(int dlci, CancellationToken ct = default)
    {
        if (dlci is < 1 or > FrameConstants.MaxDlci)
            throw new ArgumentOutOfRangeException(nameof(dlci), "Каналы данных: 1..61");
        if (_attached)
        {
            // Каналами управляет тот, кто включил MUX: мы только начинаем слушать/писать в канал.
            SetChannelState(GetChannel(dlci), ChannelState.Open);
            return true;
        }
        if (!await OpenChannelCoreAsync(dlci, ct))
            return false;
        if (_options.SendMscOnOpen)
            await SendControlAsync(ControlMessage.Msc(dlci, _options.MscSignals), ct);
        return true;
    }

    private async Task<bool> OpenChannelCoreAsync(int dlci, CancellationToken ct)
    {
        var ch = GetChannel(dlci);
        if (ch.State == ChannelState.Open)
            return true;
        SetChannelState(ch, ChannelState.Opening);
        var reply = await ExchangeAsync(ch, new MuxFrame(dlci, FrameType.SABM, true, true, Array.Empty<byte>()), _options.Retries, ct);
        if (reply == FrameType.UA)
        {
            SetChannelState(ch, ChannelState.Open);
            Emit(LogLevel.Info, $"DLC{dlci} открыт");
            return true;
        }
        SetChannelState(ch, ChannelState.Failed);
        Emit(LogLevel.Error, reply == FrameType.DM ? $"DLC{dlci}: модем отказал (DM)" : $"DLC{dlci}: нет ответа на SABM");
        return false;
    }

    /// <summary>Закрывает канал (DISC → UA).</summary>
    public async Task CloseChannelAsync(int dlci, CancellationToken ct = default)
    {
        if (dlci is < 1 or > FrameConstants.MaxDlci)
            throw new ArgumentOutOfRangeException(nameof(dlci));
        if (_attached)
        {
            SetChannelState(GetChannel(dlci), ChannelState.Closed); // без DISC: канал модема не трогаем
            return;
        }
        await CloseChannelCoreAsync(dlci, _options.Retries, ct);
    }

    private async Task CloseChannelCoreAsync(int dlci, int retries, CancellationToken ct)
    {
        var ch = GetChannel(dlci);
        if (ch.State is ChannelState.Closed or ChannelState.Failed)
            return;
        SetChannelState(ch, ChannelState.Closing);
        var reply = await ExchangeAsync(ch, new MuxFrame(dlci, FrameType.DISC, true, true, Array.Empty<byte>()), retries, ct);
        if (reply is null)
            Emit(LogLevel.Warning, $"DLC{dlci}: нет ответа на DISC");
        SetChannelState(ch, ChannelState.Closed);
    }

    private async Task<FrameType?> ExchangeAsync(Channel ch, MuxFrame command, int retries, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= Math.Max(1, retries); attempt++)
        {
            var tcs = new TaskCompletionSource<FrameType>(TaskCreationOptions.RunContinuationsAsynchronously);
            ch.Pending = tcs;
            try
            {
                await WriteFrameAsync(command, ct);
                return await tcs.Task.WithTimeout(_options.ResponseTimeout, ct);
            }
            catch (TimeoutException)
            {
                if (attempt < retries)
                    Emit(LogLevel.Warning, $"DLC{ch.Dlci}: нет ответа на {command.Type}, повтор {attempt + 1}/{retries}");
            }
            finally
            {
                Interlocked.CompareExchange(ref ch.Pending, null, tcs);
            }
        }
        return null;
    }

    // ───────────────────────────── Передача ─────────────────────────────

    /// <summary>
    /// Отправляет данные в канал. Данные длиннее N1 режутся на несколько UIH-кадров.
    /// Метод потокобезопасен.
    /// </summary>
    public async Task SendDataAsync(int dlci, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (State != MuxSessionState.Running)
            throw new InvalidOperationException("Мультиплексор не запущен");
        if (!_channels.TryGetValue(dlci, out var ch) || ch.State != ChannelState.Open)
            throw new InvalidOperationException($"Канал DLC{dlci} не открыт");

        await ch.SendLock.WaitAsync(ct);
        try
        {
            for (int offset = 0; offset < data.Length; offset += MaxFrameSize)
            {
                await WaitForFlowAsync(ch, ct);
                var chunk = data.Slice(offset, Math.Min(MaxFrameSize, data.Length - offset));
                await WriteFrameAsync(new MuxFrame(dlci, FrameType.UIH, true, false, chunk.ToArray()), ct);
            }
        }
        finally
        {
            ch.SendLock.Release();
        }
    }

    private async Task WaitForFlowAsync(Channel ch, CancellationToken ct)
    {
        if (!_globalFlowOff && !ch.RemoteFlowOff)
            return;
        Emit(LogLevel.Debug, $"DLC{ch.Dlci}: передача приостановлена модемом (flow control)");
        var deadline = DateTime.UtcNow + FlowControlTimeout;
        while (_globalFlowOff || ch.RemoteFlowOff)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"DLC{ch.Dlci}: модем не снял flow control за {FlowControlTimeout.TotalSeconds:0} с");
            await Task.Delay(20, ct);
        }
    }

    private async Task SendControlAsync(ControlMessage message, CancellationToken ct = default)
    {
        Emit(LogLevel.Debug, $"CTRL >> {message}");
        await WriteFrameAsync(new MuxFrame(0, FrameType.UIH, true, false, message.Encode()), ct);
    }

    private async Task WriteFrameAsync(MuxFrame frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(frame.Raw, 0, frame.Raw.Length, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
        Interlocked.Increment(ref _txFrames);
        FrameTraffic?.Invoke(TrafficDirection.Tx, frame);
    }

    /// <summary>Отправка из цикла чтения: не блокирует разбор и не бросает исключений.</summary>
    private void Reply(MuxFrame frame) => _ = SafeAsync(() => WriteFrameAsync(frame, _cts.Token));

    private void Reply(ControlMessage message) => _ = SafeAsync(() => SendControlAsync(message, _cts.Token));

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Error, "Ошибка записи в порт: " + ex.Message);
        }
    }

    // ───────────────────────────── Приём ─────────────────────────────

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (n == 0)
                {
                    if (!ct.IsCancellationRequested)
                        await ShutdownAsync(MuxSessionState.Faulted, "Порт закрыт");
                    return;
                }
                ProcessIncoming(buffer.AsSpan(0, n));
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                await ShutdownAsync(MuxSessionState.Faulted, "Ошибка чтения порта: " + ex.Message);
        }
    }

    private void ProcessIncoming(ReadOnlySpan<byte> data)
    {
        if (_muxMode)
        {
            if (_swallowLf)
            {
                _swallowLf = false;
                if (data.Length > 0 && data[0] == (byte)'\n')
                    data = data.Slice(1);
            }
            _parser.Feed(data);
            return;
        }

        RawTraffic?.Invoke(TrafficDirection.Rx, data.ToArray());

        byte[]? leftover = null;
        lock (_atLock)
        {
            // Переход в MUX мог произойти, пока ждали замок.
            if (_muxMode)
            {
                leftover = data.ToArray();
            }
            else
            {
                foreach (var b in data)
                    _atBuffer.Add(b);
                if (_atTcs is { } tcs && TryFindFinalResult(_atBuffer, out var result, out int end))
                {
                    var text = Compat.Latin1.GetString(_atBuffer.GetRange(0, end).ToArray());
                    bool endsWithBareCr = _atBuffer[end - 1] == (byte)'\r' && end == _atBuffer.Count;
                    if (end < _atBuffer.Count)
                        leftover = _atBuffer.GetRange(end, _atBuffer.Count - end).ToArray();
                    _atBuffer.Clear();
                    _atTcs = null;
                    if (result == "OK" && _switchToMuxOnOk)
                    {
                        _muxMode = true; // всё, что пришло после OK, — уже кадры
                        _swallowLf = endsWithBareCr;
                    }
                    else
                        leftover = null;
                    tcs.TrySetResult(new AtResponse(result, text, false));
                }
                else if (_atBuffer.Count > 64 * 1024)
                {
                    _atBuffer.Clear();
                }
            }
        }

        if (leftover is { Length: > 0 })
            _parser.Feed(leftover);
    }

    private static readonly string[] FinalResults = { "OK", "ERROR", "NO CARRIER" };

    /// <summary>Ищет строку финального результата AT (OK / ERROR / +CME ERROR ...).</summary>
    internal static bool TryFindFinalResult(List<byte> buffer, out string result, out int endIndex)
    {
        int lineStart = 0;
        for (int i = 0; i < buffer.Count; i++)
        {
            byte b = buffer[i];
            if (b != '\r' && b != '\n')
                continue;
            if (i > lineStart)
            {
                var line = Compat.Latin1.GetString(buffer.GetRange(lineStart, i - lineStart).ToArray()).Trim();
                if (FinalResults.Contains(line) || line.StartsWith("+CME ERROR", StringComparison.Ordinal) || line.StartsWith("+CMS ERROR", StringComparison.Ordinal))
                {
                    endIndex = i + 1;
                    if (b == '\r' && endIndex < buffer.Count && buffer[endIndex] == '\n')
                        endIndex++;
                    result = line;
                    return true;
                }
            }
            lineStart = i + 1;
        }
        result = "";
        endIndex = 0;
        return false;
    }

    private void OnParserError(FrameError error)
    {
        Interlocked.Increment(ref _errors);
        FrameError?.Invoke(error);
    }

    private void OnFrameReceived(MuxFrame frame)
    {
        Interlocked.Increment(ref _rxFrames);
        FrameTraffic?.Invoke(TrafficDirection.Rx, frame);

        var ch = GetChannel(frame.Dlci);
        switch (frame.Type)
        {
            case FrameType.UA:
            case FrameType.DM:
                if (ch.Pending is { } pending)
                    pending.TrySetResult(frame.Type);
                else if (frame.Type == FrameType.DM && ch.State == ChannelState.Open)
                {
                    SetChannelState(ch, ChannelState.Closed);
                    Emit(LogLevel.Warning, $"DLC{frame.Dlci}: модем сообщил DM — канал закрыт");
                }
                break;

            case FrameType.SABM:
                // Модем сам открывает канал — подтверждаем (ответ инициатора: C/R=0).
                Reply(new MuxFrame(frame.Dlci, FrameType.UA, false, true, Array.Empty<byte>()));
                SetChannelState(ch, ChannelState.Open);
                Emit(LogLevel.Info, $"DLC{frame.Dlci} открыт модемом");
                break;

            case FrameType.DISC:
                Reply(new MuxFrame(frame.Dlci, FrameType.UA, false, true, Array.Empty<byte>()));
                if (frame.Dlci == 0)
                    _ = Task.Run(() => OnModemClosedMuxAsync("Модем закрыл мультиплексор (DISC DLC0)"));
                else
                {
                    SetChannelState(ch, ChannelState.Closed);
                    Emit(LogLevel.Warning, $"DLC{frame.Dlci} закрыт модемом");
                }
                break;

            case FrameType.UIH:
            case FrameType.UI:
                if (frame.Dlci == 0)
                    HandleControl(frame.Payload);
                else
                {
                    if (ch.State != ChannelState.Open)
                    {
                        if (_attached && ch.Pending is null)
                        {
                            // Модем шлёт данные — значит, канал у него открыт.
                            SetChannelState(ch, ChannelState.Open);
                            Emit(LogLevel.Info, $"DLC{frame.Dlci}: данные от модема — канал считается открытым");
                        }
                        else
                            Emit(LogLevel.Warning, $"Данные для неоткрытого DLC{frame.Dlci}");
                    }
                    if (frame.Payload.Length > 0)
                        DataReceived?.Invoke(frame.Dlci, frame.Payload);
                }
                break;
        }
    }

    private void HandleControl(byte[] payload)
    {
        foreach (var msg in ControlMessage.ParseAll(payload))
        {
            Emit(LogLevel.Debug, $"CTRL << {msg}");
            if (!msg.IsCommand)
            {
                if (msg.Type == ControlMessageType.CLD)
                    _cldTcs?.TrySetResult(true);
                else if (msg.Type == ControlMessageType.NSC)
                    Emit(LogLevel.Warning, "Модем не поддерживает управляющую команду: " + msg);
                continue;
            }

            switch (msg.Type)
            {
                case ControlMessageType.MSC:
                    if (msg.Value.Length >= 2)
                    {
                        var ch = GetChannel(msg.Value[0] >> 2);
                        bool fc = (msg.Value[1] & (byte)ModemSignals.FC) != 0;
                        if (fc != ch.RemoteFlowOff)
                            Emit(LogLevel.Info, $"DLC{ch.Dlci}: flow control {(fc ? "включён модемом" : "снят")}");
                        ch.RemoteFlowOff = fc;
                    }
                    Reply(msg.ToResponse());
                    break;
                case ControlMessageType.FCoff:
                    _globalFlowOff = true;
                    Emit(LogLevel.Info, "Модем приостановил приём (FCoff)");
                    Reply(msg.ToResponse());
                    break;
                case ControlMessageType.FCon:
                    _globalFlowOff = false;
                    Emit(LogLevel.Info, "Модем возобновил приём (FCon)");
                    Reply(msg.ToResponse());
                    break;
                case ControlMessageType.CLD:
                    Reply(msg.ToResponse());
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        await OnModemClosedMuxAsync("Модем закрыл мультиплексор (CLD)");
                    });
                    break;
                case ControlMessageType.Test:
                case ControlMessageType.PN:
                case ControlMessageType.RPN:
                case ControlMessageType.RLS:
                case ControlMessageType.PSC:
                case ControlMessageType.SNC:
                    // Принимаем предложенные параметры как есть.
                    Reply(msg.ToResponse());
                    break;
                default:
                    Reply(new ControlMessage(ControlMessageType.NSC, false, new[] { msg.TypeOctet }));
                    break;
            }
        }
    }

    // ───────────────────────────── Остановка ─────────────────────────────

    /// <summary>
    /// Закрывает мультиплексор (DISC каналов, затем CLD), но оставляет порт открытым в AT-режиме.
    /// Работает, только если сессия открыта через <see cref="OpenAsync"/>; иначе равносильна <see cref="StopAsync"/>.
    /// </summary>
    public async Task StopMuxAsync()
    {
        if (!_keepPortOpen)
        {
            await StopAsync();
            return;
        }
        if (State == MuxSessionState.Initializing)
        {
            _muxCts?.Cancel(); // StartAsync сам вернётся в «порт открыт»
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (State == MuxSessionState.Initializing && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            return;
        }
        if (State != MuxSessionState.Running)
            return;

        SetState(MuxSessionState.Stopping);
        if (_attached)
        {
            LeaveMuxState("Отключились от MUX (модем остаётся в MUX — его включали не мы), порт открыт");
            return;
        }
        await CloseMuxProtocolAsync();
        LeaveMuxState("MUX остановлен, модем в AT-режиме, порт открыт");
    }

    /// <summary>
    /// Корректно закрывает мультиплексор и сессию целиком: DISC для каналов данных, затем CLD
    /// (модем возвращается в AT-режим). Поток (порт) не закрывается — это делает владелец.
    /// </summary>
    public async Task StopAsync()
    {
        if (State is MuxSessionState.Idle or MuxSessionState.Stopped or MuxSessionState.Faulted or MuxSessionState.PortOpen)
        {
            await ShutdownAsync(MuxSessionState.Stopped, State == MuxSessionState.PortOpen ? "Порт закрыт" : "Сессия остановлена");
            await WaitReadLoopAsync();
            return;
        }
        if (State == MuxSessionState.Initializing)
        {
            _cts.Cancel(); // StartAsync сам завершит сессию
            await WaitReadLoopAsync();
            return;
        }
        if (State == MuxSessionState.Stopping)
            return;

        SetState(MuxSessionState.Stopping);
        if (!_attached)
            await CloseMuxProtocolAsync();
        await ShutdownAsync(MuxSessionState.Stopped, _attached ? "Отключились от MUX (модем остаётся в MUX)" : "Сессия остановлена");
        await WaitReadLoopAsync();
    }

    private async Task CloseMuxProtocolAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var ch in _channels.Values.Where(c => c.Dlci > 0 && c.State == ChannelState.Open).OrderByDescending(c => c.Dlci))
                await CloseChannelCoreAsync(ch.Dlci, 1, timeout.Token);

            _cldTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendControlAsync(ControlMessage.CloseDown(), timeout.Token);
            try
            {
                await _cldTcs.Task.WithTimeout(_options.ResponseTimeout, timeout.Token);
                Emit(LogLevel.Info, "Мультиплексор закрыт (CLD), модем в AT-режиме");
            }
            catch (TimeoutException)
            {
                Emit(LogLevel.Warning, "Нет ответа на CLD — отправляю DISC на DLC0");
                await ExchangeAsync(GetChannel(0), new MuxFrame(0, FrameType.DISC, true, true, Array.Empty<byte>()), 1, timeout.Token);
            }
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Warning, "Ошибка при закрытии MUX: " + ex.Message);
        }
    }

    private async Task WaitReadLoopAsync()
    {
        if (_readTask is { } t)
            await Task.WhenAny(t, Task.Delay(500));
    }

    private Task ShutdownAsync(MuxSessionState finalState, string reason)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
            return Task.CompletedTask;

        TerminationReason = reason;
        _cts.Cancel();
        _muxMode = false;
        lock (_atLock)
            _atTcs?.TrySetCanceled();
        _cldTcs?.TrySetResult(false);

        foreach (var ch in _channels.Values)
        {
            ch.Pending?.TrySetCanceled();
            if (ch.State != ChannelState.Closed)
                SetChannelState(ch, ChannelState.Closed);
        }

        Emit(finalState == MuxSessionState.Faulted ? LogLevel.Error : LogLevel.Info, reason);
        SetState(finalState);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _muxCts?.Dispose();
        _cts.Dispose();
    }

    // ───────────────────────────── Вспомогательное ─────────────────────────────

    private Channel GetChannel(int dlci) => _channels.GetOrAdd(dlci, d => new Channel(d));

    private void SetChannelState(Channel ch, ChannelState state)
    {
        if (ch.State == state)
            return;
        ch.State = state;
        if (state != ChannelState.Open)
            ch.RemoteFlowOff = false;
        ChannelStateChanged?.Invoke(ch.Dlci, state);
    }

    private void SetState(MuxSessionState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Emit(LogLevel level, string message) => Log?.Invoke(level, message);
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Transport;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Emulator;

/// <summary>
/// Программный модем с поддержкой AT+CMUX (Basic Option). Позволяет проверить терминал без железа:
/// отвечает на базовые AT-команды, эмулирует отправку SMS (AT+CMGS … Ctrl+Z),
/// выдаёт NMEA-предложения на отдельном канале и бинарные данные (AT+BINTEST), содержащие 0xF9.
/// Модем — отвечающая сторона: его команды идут с C/R=0, ответы — с C/R=1.
/// </summary>
public sealed class ModemEmulator : IAsyncDisposable
{
    private sealed class ChannelContext
    {
        public readonly List<byte> Line = new();
        public bool Echo = true;
        public bool SmsInput;
        public readonly List<byte> Sms = new();
    }

    private readonly Stream _stream;
    private readonly FrameParser _parser = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, ChannelContext> _open = new();
    private readonly ChannelContext _atContext = new();
    private readonly List<MuxFrame> _received = new();
    private readonly List<MuxFrame> _log = new();
    private readonly HashSet<int> _mscReceived = new();
    private readonly Random _rng;
    private bool _lastWriteEndedWithFlag;
    private Task? _loop;
    private Task? _nmeaLoop;
    private volatile bool _mux;
    private int _n1 = FrameConstants.DefaultN1;
    private int _smsCounter;
    private long _framesReceived, _frameErrors;

    public ModemEmulator(Stream stream, ModemQuirks? quirks = null)
    {
        _stream = stream;
        Quirks = quirks ?? new ModemQuirks();
        _rng = new Random(Quirks.Seed);
        _atContext.Echo = Quirks.Echo;
        _parser.FrameReceived += OnFrame;
        _parser.Error += _ => Interlocked.Increment(ref _frameErrors);
    }

    /// <summary>Канал, на который выдаются NMEA-предложения (0 — отключено).</summary>
    public int NmeaDlci { get; set; } = 3;
    public TimeSpan NmeaInterval { get; set; } = TimeSpan.FromSeconds(1);

    public ModemQuirks Quirks { get; }
    public bool InMuxMode => _mux;

    /// <summary>Все кадры, полученные от терминала (для проверок в тестах).</summary>
    public IReadOnlyList<MuxFrame> ReceivedFrames
    {
        get
        {
            lock (_log)
                return _log.ToArray();
        }
    }

    /// <summary>Управляющие сообщения DLC0, полученные от терминала.</summary>
    public IReadOnlyList<ControlMessage> ReceivedControl
        => ReceivedFrames.Where(f => f.Dlci == 0 && f.Type is FrameType.UIH or FrameType.UI)
            .SelectMany(f => ControlMessage.ParseAll(f.Payload)).ToList();
    public int N1 => _n1;
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FrameErrors => Interlocked.Read(ref _frameErrors);
    public IReadOnlyCollection<int> OpenChannels => _open.Keys.Where(k => k > 0).OrderBy(k => k).ToArray();

    /// <summary>Эмулятор «перезагрузился» по AT+CFUN=1,1 и разорвал связь.</summary>
    public event Action? Disconnected;

    /// <summary>Полученные командные строки (DLCI, строка) — для тестов и отладки.</summary>
    public event Action<int, string>? CommandReceived;

    public void Start()
    {
        _loop = Task.Run(ReadLoopAsync);
        _nmeaLoop = Task.Run(NmeaLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (n == 0)
                    break;
                await ProcessAsync(buffer, n);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task ProcessAsync(byte[] buffer, int count)
    {
        int i = 0;
        while (i < count && !_mux)
        {
            byte b = buffer[i++];
            if (_atContext.Echo)
                await WriteRawAsync(new[] { b });
            if (b == '\r')
            {
                var line = Encoding.ASCII.GetString(_atContext.Line.ToArray()).Trim();
                _atContext.Line.Clear();
                if (line.Length > 0)
                    await ExecuteAtModeAsync(line);
            }
            else if (b != '\n')
            {
                if (_atContext.Line.Count < 1024)
                    _atContext.Line.Add(b);
            }
        }
        if (i < count && _mux)
        {
            _parser.Feed(buffer.AsSpan(i, count - i));
            var frames = _received.ToArray();
            _received.Clear();
            foreach (var frame in frames)
                await HandleFrameAsync(frame);
        }
    }

    private async Task ExecuteAtModeAsync(string line)
    {
        CommandReceived?.Invoke(-1, line);
        if (line.StartsWith("AT+CMUX=", StringComparison.OrdinalIgnoreCase))
        {
            var p = CmuxParameters.Parse(line);
            if (p.Mode != 0)
            {
                await WriteRawAsync(Encoding.ASCII.GetBytes("\r\nERROR\r\n"));
                return;
            }
            if (Quirks.CmuxResponse != "OK")
            {
                await WriteRawAsync(Encoding.ASCII.GetBytes(Fix("\r\n" + Quirks.CmuxResponse + "\r\n")));
                return;
            }
            var reply = new List<byte>();
            if (Quirks.UrcBeforeCmuxOk is { } urc)
                reply.AddRange(Encoding.ASCII.GetBytes(Fix("\r\n" + urc + "\r\n")));
            reply.AddRange(Encoding.ASCII.GetBytes(Fix("\r\nOK\r\n")));
            _n1 = p.N1;
            _parser.Reset();
            _parser.MaxPayloadLength = Math.Max(Math.Max(_n1, Quirks.ForceN1 ?? 0), 127);
            _mux = true;
            if (Quirks.FrameRightAfterOk)
            {
                var test = new ControlMessage(ControlMessageType.Test, true, Encoding.ASCII.GetBytes("EMU"));
                reply.AddRange(FrameEncoder.Encode(0, FrameType.UIH, false, false, test.Encode()));
            }
            await WriteRawAsync(reply.ToArray());
            _lastWriteEndedWithFlag = Quirks.FrameRightAfterOk;
            return;
        }
        var response = Execute(line, _atContext);
        await WriteRawAsync(Encoding.ASCII.GetBytes(Fix(response)));
    }

    // ───────────── MUX ─────────────

    private void OnFrame(MuxFrame frame)
    {
        Interlocked.Increment(ref _framesReceived);
        lock (_log)
            _log.Add(frame);
        _received.Add(frame); // обрабатываем после Feed, асинхронно и по порядку
    }

    private async Task HandleFrameAsync(MuxFrame frame)
    {
        switch (frame.Type)
        {
            case FrameType.SABM:
                if (Quirks.IgnoreSabm.Contains(frame.Dlci))
                    break;
                if (frame.Dlci > Quirks.MaxDlci)
                {
                    await SendFrameAsync(frame.Dlci, FrameType.DM, true, true, default);
                    break;
                }
                _open.TryAdd(frame.Dlci, new ChannelContext { Echo = Quirks.Echo });
                await SendFrameAsync(frame.Dlci, FrameType.UA, true, true, default);
                if (frame.Dlci > 0 && Quirks.SendMscOnOpen)
                {
                    // Как и реальные модемы, сообщаем состояние V.24-сигналов канала.
                    var msc = ControlMessage.Msc(frame.Dlci, ModemSignals.RTC | ModemSignals.RTR | ModemSignals.DV);
                    await SendFrameAsync(0, FrameType.UIH, false, false, msc.Encode());
                }
                break;

            case FrameType.DISC:
                // Состояние меняем до ответа: получив UA, терминал вправе считать модем уже в AT-режиме.
                _open.TryRemove(frame.Dlci, out _);
                if (frame.Dlci == 0)
                    LeaveMux();
                await SendFrameAsync(frame.Dlci, FrameType.UA, true, true, default);
                break;

            case FrameType.UIH:
            case FrameType.UI:
                if (frame.Dlci == 0 && !_open.ContainsKey(0))
                    break; // DLC0 ещё не открыт (ответ на кадр, посланный сразу за OK) — только фиксируем
                if (!_open.TryGetValue(frame.Dlci, out var ctx))
                {
                    await SendFrameAsync(frame.Dlci, FrameType.DM, true, true, default);
                    break;
                }
                if (frame.Dlci == 0)
                    await HandleControlAsync(frame.Payload);
                else
                    await HandleDataAsync(frame.Dlci, ctx, frame.Payload);
                break;
        }
    }

    private async Task HandleControlAsync(byte[] payload)
    {
        foreach (var msg in ControlMessage.ParseAll(payload))
        {
            if (!msg.IsCommand)
            {
                // Модем сам закрывал MUX (CLD) — получив ответ терминала, возвращается в AT-режим.
                if (msg.Type == ControlMessageType.CLD)
                {
                    LeaveMux();
                    return;
                }
                continue;
            }
            switch (msg.Type)
            {
                case ControlMessageType.CLD when !Quirks.SupportsCld:
                    await SendFrameAsync(0, FrameType.UIH, false, false,
                        new ControlMessage(ControlMessageType.NSC, false, new[] { msg.TypeOctet }).Encode());
                    break;
                case ControlMessageType.MSC:
                    if (msg.Value.Length >= 1)
                        lock (_mscReceived)
                            _mscReceived.Add(msg.Value[0] >> 2);
                    await SendFrameAsync(0, FrameType.UIH, false, false, msg.ToResponse().Encode());
                    break;
                case ControlMessageType.CLD:
                    LeaveMux(); // до ответа — см. DISC
                    await SendFrameAsync(0, FrameType.UIH, false, false, msg.ToResponse().Encode());
                    return;
                case ControlMessageType.Test:
                case ControlMessageType.PN:
                case ControlMessageType.FCon:
                case ControlMessageType.FCoff:
                    await SendFrameAsync(0, FrameType.UIH, false, false, msg.ToResponse().Encode());
                    break;
                default:
                    var nsc = new ControlMessage(ControlMessageType.NSC, false, new[] { msg.TypeOctet });
                    await SendFrameAsync(0, FrameType.UIH, false, false, nsc.Encode());
                    break;
            }
        }
    }

    private async Task HandleDataAsync(int dlci, ChannelContext ctx, byte[] data)
    {
        if (Quirks.RequireMscBeforeData)
        {
            lock (_mscReceived)
                if (!_mscReceived.Contains(dlci))
                    return; // модем молчит, пока терминал не прислал MSC для канала
        }
        foreach (byte b in data)
        {
            if (ctx.SmsInput)
            {
                if (b == 0x1A) // Ctrl+Z — отправить
                {
                    ctx.SmsInput = false;
                    int id = Interlocked.Increment(ref _smsCounter);
                    await SendDataAsync(dlci, Encoding.ASCII.GetBytes($"\r\n+CMGS: {id}\r\n\r\nOK\r\n"));
                    ctx.Sms.Clear();
                }
                else if (b == 0x1B) // Esc — отмена
                {
                    ctx.SmsInput = false;
                    ctx.Sms.Clear();
                    await SendDataAsync(dlci, Encoding.ASCII.GetBytes("\r\nOK\r\n"));
                }
                else
                {
                    ctx.Sms.Add(b);
                    await SendDataAsync(dlci, new[] { b });
                }
                continue;
            }

            if (ctx.Echo)
                await SendDataAsync(dlci, new[] { b });
            if (b == '\r')
            {
                var line = Encoding.ASCII.GetString(ctx.Line.ToArray()).Trim();
                ctx.Line.Clear();
                if (line.Length == 0)
                    continue;
                CommandReceived?.Invoke(dlci, line);
                if (line.Equals("AT+CFUN=1,1", StringComparison.OrdinalIgnoreCase))
                {
                    // Перезагрузка модема: USB-модем при этом пропадает из системы — имитируем обрыв порта.
                    await SendDataAsync(dlci, Encoding.ASCII.GetBytes("\r\nOK\r\n"));
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        Disconnected?.Invoke();
                        _stream.Dispose();
                    });
                    continue;
                }
                if (line.Equals("AT+BINTEST", StringComparison.OrdinalIgnoreCase))
                {
                    var bin = Enumerable.Range(0, 256).Select(x => (byte)x).ToArray();
                    await SendDataAsync(dlci, bin);
                    await SendDataAsync(dlci, Encoding.ASCII.GetBytes("\r\nOK\r\n"));
                    continue;
                }
                await SendDataAsync(dlci, Encoding.ASCII.GetBytes(Execute(line, ctx)));
            }
            else if (b != '\n' && ctx.Line.Count < 4096)
            {
                ctx.Line.Add(b);
            }
        }
    }

    private void LeaveMux()
    {
        _mux = false;
        _open.Clear();
        _parser.Reset();
        lock (_mscReceived)
            _mscReceived.Clear();
    }

    private string Fix(string text) => Quirks.LineEnding == "\r\n" ? text : text.Replace("\r\n", Quirks.LineEnding);

    // ───────────── Команды «от модема» (для тестов) ─────────────

    /// <summary>
    /// Модем уже работает в MUX с открытыми каналами (их открыл кто-то до нас) — как при галочке
    /// «Модем уже в MUX». На повторный SABM такой модем может не отвечать: <see cref="ModemQuirks.IgnoreSabm"/>.
    /// </summary>
    public void EnterMuxDirectly(params int[] dlcis)
    {
        _parser.Reset();
        _parser.MaxPayloadLength = Math.Max(Math.Max(_n1, Quirks.ForceN1 ?? 0), 127);
        _open.TryAdd(0, new ChannelContext { Echo = Quirks.Echo });
        foreach (int dlci in dlcis)
            _open.TryAdd(dlci, new ChannelContext { Echo = Quirks.Echo });
        _mux = true;
    }

    /// <summary>Отправить терминалу управляющее сообщение по DLC0 (модем — отвечающая сторона, C/R=0).</summary>
    public Task SendControlAsync(ControlMessage message)
        => SendFrameAsync(0, FrameType.UIH, false, false, message.Encode());

    /// <summary>Отправить терминалу произвольный кадр (SABM/DISC/UIH от модема и т.п.).</summary>
    public Task SendFrameToTerminalAsync(int dlci, FrameType type, bool commandResponse, bool pollFinal, byte[] payload)
    {
        if (type == FrameType.SABM)
            _open.TryAdd(dlci, new ChannelContext { Echo = Quirks.Echo });
        if (type == FrameType.DISC)
            _open.TryRemove(dlci, out _);
        return SendFrameAsync(dlci, type, commandResponse, pollFinal, payload);
    }

    /// <summary>Отправить данные в канал от имени модема.</summary>
    public Task SendToChannelAsync(int dlci, byte[] data) => SendDataAsync(dlci, data);

    // ───────────── AT-команды ─────────────

    private string Execute(string line, ChannelContext ctx)
    {
        string cmd = line.ToUpperInvariant();
        static string Ok(string body = "") => body.Length == 0 ? "\r\nOK\r\n" : $"\r\n{body}\r\n\r\nOK\r\n";

        if (!cmd.StartsWith("AT", StringComparison.Ordinal))
            return "\r\nERROR\r\n";

        switch (cmd)
        {
            case "AT": return Ok();
            case "ATE0": ctx.Echo = false; return Ok();
            case "ATE1": ctx.Echo = true; return Ok();
            case "ATI": return Ok("MUX-EMU GSM 07.10 Emulator\r\nRevision: 1.0");
            case "AT+CGMI": return Ok("MUX-EMU");
            case "AT+CGMM": return Ok("GSM0710-EMU");
            case "AT+CGMR": return Ok("EMU01A01V01");
            case "AT+CGSN": return Ok("867000000000001");
            case "AT+CSQ": return Ok($"+CSQ: {Compat.RandomNext(15, 28)},99");
            case "AT+CREG?": return Ok("+CREG: 0,1");
            case "AT+COPS?": return Ok("+COPS: 0,0,\"EMU Network\",7");
            case "AT+CPIN?": return Ok("+CPIN: READY");
            case "AT+CMGF=0":
            case "AT+CMGF=1": return Ok();
            case "AT+CMGF?": return Ok("+CMGF: 1");
            case "AT+CCLK?": return Ok($"+CCLK: \"{DateTime.Now:yy/MM/dd,HH:mm:ss}+12\"");
        }

        if (cmd.StartsWith("AT+CMGS=", StringComparison.Ordinal))
        {
            ctx.SmsInput = true;
            ctx.Sms.Clear();
            return "\r\n> ";
        }
        if (cmd.StartsWith("AT+CMGL", StringComparison.Ordinal))
            return Ok("+CMGL: 1,\"REC UNREAD\",\"+70000000000\",,\"26/01/01,12:00:00+12\"\r\nHello from MUX emulator");
        if (cmd.StartsWith("AT+CMUX", StringComparison.Ordinal))
            return "\r\nERROR\r\n"; // уже в MUX
        return "\r\nERROR\r\n";
    }

    // ───────────── NMEA ─────────────

    private async Task NmeaLoopAsync()
    {
        double lat = 55.7558, lon = 37.6173;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(NmeaInterval, _cts.Token);
                if (!_mux || NmeaDlci <= 0 || !_open.ContainsKey(NmeaDlci))
                    continue;
                lat += (Compat.RandomDouble() - 0.5) * 0.0002;
                lon += (Compat.RandomDouble() - 0.5) * 0.0002;
                var now = DateTime.UtcNow;
                string gga = Nmea($"GPGGA,{now:HHmmss.ff},{ToNmea(lat, 2)},N,{ToNmea(lon, 3)},E,1,08,0.9,145.0,M,14.0,M,,");
                string rmc = Nmea($"GPRMC,{now:HHmmss.ff},A,{ToNmea(lat, 2)},N,{ToNmea(lon, 3)},E,0.10,0.0,{now:ddMMyy},,,A");
                await SendDataAsync(NmeaDlci, Encoding.ASCII.GetBytes(gga + rmc));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string ToNmea(double value, int degreeDigits)
    {
        int deg = (int)value;
        double minutes = (value - deg) * 60;
        return deg.ToString(new string('0', degreeDigits), CultureInfo.InvariantCulture)
               + minutes.ToString("00.0000", CultureInfo.InvariantCulture);
    }

    private static string Nmea(string body)
    {
        byte cs = 0;
        foreach (char c in body)
            cs ^= (byte)c;
        return $"${body}*{cs:X2}\r\n";
    }

    // ───────────── Вывод ─────────────

    private async Task SendDataAsync(int dlci, byte[] data)
    {
        if (!_mux)
            return;
        int n1 = Quirks.ForceN1 ?? _n1;
        var type = Quirks.DataFramesAsUi ? FrameType.UI : FrameType.UIH;
        for (int offset = 0; offset < data.Length; offset += n1)
        {
            int len = Math.Min(n1, data.Length - offset);
            await SendFrameAsync(dlci, type, Quirks.DataCrBit, false, data.AsMemory(offset, len));
        }
    }

    private Task SendFrameAsync(int dlci, FrameType type, bool cr, bool pf, ReadOnlyMemory<byte> payload)
        => WriteRawAsync(FrameEncoder.Encode(dlci, type, cr, pf, payload.Span), isFrame: true);

    private async Task WriteRawAsync(byte[] data, bool isFrame = false)
    {
        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var bytes = new List<byte>(data.Length + 16);
            if (isFrame)
            {
                if (Quirks.NoiseProbability > 0 && _rng.NextDouble() < Quirks.NoiseProbability)
                    for (int i = _rng.Next(1, 12); i > 0; i--)
                        bytes.Add((byte)(_rng.Next(0, 0xF8))); // мусор без 0xF9
                for (int i = 0; i < Quirks.ExtraFlags; i++)
                    bytes.Add(FrameConstants.Flag);
                // Общий флаг: открывающий флаг опускаем, если предыдущий кадр закончился флагом.
                bool skipOpening = Quirks.SharedFlags && _lastWriteEndedWithFlag && bytes.Count == 0;
                bytes.AddRange(skipOpening ? data.Skip(1) : data);
                _lastWriteEndedWithFlag = true;
            }
            else
            {
                bytes.AddRange(data);
                _lastWriteEndedWithFlag = false;
            }

            var all = bytes.ToArray();
            if (Quirks.MaxChunk <= 0)
            {
                await _stream.WriteAsync(all, 0, all.Length, _cts.Token);
            }
            else
            {
                for (int offset = 0; offset < all.Length;)
                {
                    int len = Math.Min(all.Length - offset, _rng.Next(1, Quirks.MaxChunk + 1));
                    await _stream.WriteAsync(all, offset, len, _cts.Token);
                    offset += len;
                }
            }
            await _stream.FlushAsync(_cts.Token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _stream.Dispose();
        foreach (var t in new[] { _loop, _nmeaLoop })
        {
            if (t is null)
                continue;
            try { await t.WithTimeout(TimeSpan.FromSeconds(1)); } catch { /* завершение */ }
        }
        _cts.Dispose();
    }
}

/// <summary>Транспорт «Эмулятор модема»: терминал говорит с <see cref="ModemEmulator"/> через поток в памяти.</summary>
public sealed class EmulatorTransport : IMuxTransport
{
    private readonly InMemoryDuplexStream _client;

    public EmulatorTransport(ModemQuirks? quirks = null)
    {
        var (client, modem) = InMemoryDuplexStream.CreatePair();
        _client = client;
        Emulator = new ModemEmulator(modem, quirks);
        Emulator.Start();
    }

    public ModemEmulator Emulator { get; }
    public string Name => "Эмулятор модема";
    public Stream Stream => _client;

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Emulator.DisposeAsync();
    }
}

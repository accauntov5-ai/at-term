namespace MuxTerminal.Core.Protocol;

/// <summary>Причина отбрасывания байтов потока.</summary>
public enum FrameErrorKind
{
    /// <summary>Байты вне кадра (мусор между кадрами, остатки текстового режима).</summary>
    Garbage,
    /// <summary>Не совпала контрольная сумма.</summary>
    BadFcs,
    /// <summary>Нет закрывающего флага в ожидаемой позиции.</summary>
    MissingClosingFlag,
    /// <summary>Некорректный заголовок (EA=0 в адресе, неизвестный тип, длина больше допустимой).</summary>
    BadHeader,
}

public sealed record FrameError(FrameErrorKind Kind, byte[] Data, string Message);

/// <summary>
/// Потоковый разборщик кадров GSM 07.10 Basic Option.
/// В базовом режиме нет byte stuffing: границы кадра определяются полем Length,
/// поэтому 0xF9 внутри Payload допустим. Флаги служат для синхронизации,
/// а FCS + закрывающий флаг — для проверки. При ошибке поиск начала кадра
/// возобновляется со следующего байта, что гарантирует ресинхронизацию.
/// Класс не потокобезопасен: вызывайте <see cref="Feed"/> из одного потока (цикл чтения порта).
/// </summary>
public sealed class FrameParser
{
    private byte[] _buffer = new byte[4096];
    private int _count;

    /// <summary>Максимально допустимая длина Payload. Кадры с большей длиной считаются повреждёнными.</summary>
    public int MaxPayloadLength { get; set; } = FrameConstants.MaxLength;

    public event Action<MuxFrame>? FrameReceived;
    public event Action<FrameError>? Error;

    public int BufferedBytes => _count;

    public void Reset() => _count = 0;

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;
        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
        Process();
    }

    private void EnsureCapacity(int size)
    {
        if (size <= _buffer.Length)
            return;
        Array.Resize(ref _buffer, Math.Max(size, _buffer.Length * 2));
    }

    private void Process()
    {
        int pos = 0;
        while (pos < _count)
        {
            int start = Array.IndexOf(_buffer, FrameConstants.Flag, pos, _count - pos);
            if (start < 0)
            {
                Report(FrameErrorKind.Garbage, pos, _count - pos, "Данные вне кадра");
                pos = _count;
                break;
            }
            if (start > pos)
                Report(FrameErrorKind.Garbage, pos, start - pos, "Данные вне кадра");

            // Несколько флагов подряд (закрывающий + открывающий) — берём последний.
            while (start + 1 < _count && _buffer[start + 1] == FrameConstants.Flag)
                start++;
            pos = start;

            var r = TryParseAt(start);
            switch (r.Status)
            {
                case ParseStatus.Incomplete:
                    // Ждём хвост кадра. Но если «начало» было ложным (мусор похож на заголовок с большой длиной),
                    // ждать можно бесконечно, а настоящие кадры застрянут в буфере. Поэтому, если дальше
                    // уже лежит целый корректный кадр (FCS и закрывающий флаг сошлись) — считаем начало ложным.
                    int next = FindCompleteFrameAfter(start + 1);
                    if (next < 0)
                        goto done;
                    Report(FrameErrorKind.BadHeader, start, next - start, "Ложное начало кадра: дальше найден целый кадр");
                    pos = next;
                    continue;

                case ParseStatus.BadHeader:
                    Report(FrameErrorKind.BadHeader, start, 1, r.Message!);
                    pos = start + 1;
                    continue;

                case ParseStatus.MissingClosingFlag:
                    Report(FrameErrorKind.MissingClosingFlag, start, 1, "Нет закрывающего флага 0xF9");
                    pos = start + 1;
                    continue;

                case ParseStatus.BadFcs:
                    Report(FrameErrorKind.BadFcs, start, r.CloseIndex - start + 1, "Ошибка FCS, кадр отброшен");
                    // Продолжаем поиск со следующего байта, а не с «закрывающего» флага: заголовок мог быть ложным
                    // (мусор, потерянный байт), и тогда между ним и найденным 0xF9 лежат настоящие кадры.
                    pos = start + 1;
                    continue;

                default:
                    pos = r.CloseIndex; // закрывающий флаг может быть открывающим для следующего кадра
                    FrameReceived?.Invoke(r.Frame!);
                    continue;
            }
        }
        done:

        // Сдвигаем необработанный хвост в начало буфера.
        if (pos > 0)
        {
            int remaining = _count - pos;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, pos, _buffer, 0, remaining);
            _count = remaining;
        }
    }

    private enum ParseStatus
    {
        Incomplete,
        BadHeader,
        MissingClosingFlag,
        BadFcs,
        Ok,
    }

    private readonly struct ParseResult
    {
        public ParseResult(ParseStatus status, int closeIndex = 0, MuxFrame? frame = null, string? message = null)
        {
            Status = status;
            CloseIndex = closeIndex;
            Frame = frame;
            Message = message;
        }

        public ParseStatus Status { get; }
        public int CloseIndex { get; }
        public MuxFrame? Frame { get; }
        public string? Message { get; }
    }

    /// <summary>Пробует разобрать кадр, начинающийся с флага в позиции start.</summary>
    private ParseResult TryParseAt(int start)
    {
        // Минимум: F9 Addr Ctrl Len
        if (_count - start < 4)
            return new ParseResult(ParseStatus.Incomplete);

        int i = start + 1;
        byte address = _buffer[i++];
        byte control = _buffer[i++];
        byte len1 = _buffer[i++];
        if ((address & 0x01) == 0 || !TryGetFrameType(control, out var type))
            return new ParseResult(ParseStatus.BadHeader, message: $"Некорректный заголовок: addr=0x{address:X2} ctrl=0x{control:X2}");

        int length;
        if ((len1 & 0x01) != 0)
        {
            length = len1 >> 1;
        }
        else
        {
            if (_count - start < 5)
                return new ParseResult(ParseStatus.Incomplete);
            length = (len1 >> 1) | (_buffer[i++] << 7);
        }
        if (length > MaxPayloadLength)
            return new ParseResult(ParseStatus.BadHeader, message: $"Длина {length} превышает максимум {MaxPayloadLength}");

        int headerEnd = i;
        int fcsIndex = headerEnd + length;
        int closeIndex = fcsIndex + 1;
        if (closeIndex >= _count)
            return new ParseResult(ParseStatus.Incomplete);
        if (_buffer[closeIndex] != FrameConstants.Flag)
            return new ParseResult(ParseStatus.MissingClosingFlag);

        var header = _buffer.AsSpan(start + 1, headerEnd - start - 1);
        var payload = _buffer.AsSpan(headerEnd, length);
        bool fcsOk = type == FrameType.UIH
            ? Fcs.Check(header, ReadOnlySpan<byte>.Empty, _buffer[fcsIndex])
            : Fcs.Check(header, payload, _buffer[fcsIndex]);
        if (!fcsOk)
            return new ParseResult(ParseStatus.BadFcs, closeIndex);

        var frame = new MuxFrame(
            address >> 2,
            type,
            (address & 0x02) != 0,
            (control & FrameConstants.PollFinalBit) != 0,
            payload.ToArray(),
            _buffer.AsSpan(start, closeIndex - start + 1).ToArray());
        return new ParseResult(ParseStatus.Ok, closeIndex, frame);
    }

    /// <summary>Позиция флага, с которого в буфере начинается целый корректный кадр (после from), или -1.</summary>
    private int FindCompleteFrameAfter(int from)
    {
        int p = from;
        while (p < _count)
        {
            int flag = Array.IndexOf(_buffer, FrameConstants.Flag, p, _count - p);
            if (flag < 0)
                return -1;
            while (flag + 1 < _count && _buffer[flag + 1] == FrameConstants.Flag)
                flag++;
            if (TryParseAt(flag).Status == ParseStatus.Ok)
                return flag;
            p = flag + 1;
        }
        return -1;
    }

    private static bool TryGetFrameType(byte control, out FrameType type)
    {
        type = (FrameType)(control & ~FrameConstants.PollFinalBit);
        return type is FrameType.SABM or FrameType.UA or FrameType.DM or FrameType.DISC or FrameType.UIH or FrameType.UI;
    }

    private void Report(FrameErrorKind kind, int offset, int length, string message)
    {
        if (length <= 0)
            return;
        // Одиночный флаг-разделитель не является ошибкой.
        if (kind == FrameErrorKind.Garbage && length == 1 && _buffer[offset] == FrameConstants.Flag)
            return;
        Error?.Invoke(new FrameError(kind, _buffer.AsSpan(offset, length).ToArray(), message));
    }
}

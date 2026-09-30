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

            // Минимум: F9 Addr Ctrl Len
            if (_count - start < 4)
                break;

            int i = start + 1;
            byte address = _buffer[i++];
            byte control = _buffer[i++];
            byte len1 = _buffer[i++];

            if ((address & 0x01) == 0 || !TryGetFrameType(control, out var type))
            {
                Report(FrameErrorKind.BadHeader, start, 1, $"Некорректный заголовок: addr=0x{address:X2} ctrl=0x{control:X2}");
                pos = start + 1;
                continue;
            }

            int length;
            if ((len1 & 0x01) != 0)
            {
                length = len1 >> 1;
            }
            else
            {
                if (_count - start < 5)
                    break;
                byte len2 = _buffer[i++];
                length = (len1 >> 1) | (len2 << 7);
            }

            if (length > MaxPayloadLength)
            {
                Report(FrameErrorKind.BadHeader, start, 1, $"Длина {length} превышает максимум {MaxPayloadLength}");
                pos = start + 1;
                continue;
            }

            int headerEnd = i;
            int fcsIndex = headerEnd + length;
            int closeIndex = fcsIndex + 1;
            if (closeIndex >= _count)
                break; // кадр ещё не принят целиком

            if (_buffer[closeIndex] != FrameConstants.Flag)
            {
                Report(FrameErrorKind.MissingClosingFlag, start, 1, "Нет закрывающего флага 0xF9");
                pos = start + 1;
                continue;
            }

            var header = _buffer.AsSpan(start + 1, headerEnd - start - 1);
            var payload = _buffer.AsSpan(headerEnd, length);
            bool fcsOk = type == FrameType.UIH
                ? Fcs.Check(header, ReadOnlySpan<byte>.Empty, _buffer[fcsIndex])
                : Fcs.Check(header, payload, _buffer[fcsIndex]);

            if (!fcsOk)
            {
                Report(FrameErrorKind.BadFcs, start, closeIndex - start + 1, "Ошибка FCS, кадр отброшен");
                pos = closeIndex; // закрывающий флаг может быть открывающим для следующего кадра
                continue;
            }

            var raw = _buffer.AsSpan(start, closeIndex - start + 1).ToArray();
            var frame = new MuxFrame(
                address >> 2,
                type,
                (address & 0x02) != 0,
                (control & FrameConstants.PollFinalBit) != 0,
                payload.ToArray(),
                raw);
            pos = closeIndex;
            FrameReceived?.Invoke(frame);
        }

        // Сдвигаем необработанный хвост в начало буфера.
        if (pos > 0)
        {
            int remaining = _count - pos;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, pos, _buffer, 0, remaining);
            _count = remaining;
        }
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

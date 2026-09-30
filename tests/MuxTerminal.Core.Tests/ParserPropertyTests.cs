using MuxTerminal.Core.Protocol;

namespace MuxTerminal.Core.Tests;

/// <summary>
/// Рандомизированные проверки разбора потока (с фиксированными seed — воспроизводимо):
/// случайные кадры любых DLC/типов/длин, случайная нарезка потока, общие и двойные флаги, мусор, порча байтов.
/// </summary>
public class ParserPropertyTests
{
    private static readonly FrameType[] Types = { FrameType.SABM, FrameType.UA, FrameType.DM, FrameType.DISC, FrameType.UIH, FrameType.UI };

    private sealed record Spec(int Dlci, FrameType Type, bool Cr, bool Pf, byte[] Payload)
    {
        public byte[] Encode() => FrameEncoder.Encode(Dlci, Type, Cr, Pf, Payload);
        public bool Matches(MuxFrame f) => f.Dlci == Dlci && f.Type == Type && f.CommandResponse == Cr && f.PollFinal == Pf && f.Payload.SequenceEqual(Payload);
    }

    private static Spec RandomFrame(Random rng)
    {
        var type = Types[rng.Next(Types.Length)];
        int length = type is FrameType.UIH or FrameType.UI
            ? rng.Next(10) switch { 0 => 0, 1 => rng.Next(128, 600), _ => rng.Next(1, 128) }
            : 0;
        var payload = new byte[length];
        rng.NextBytes(payload);
        // Обязательно проверяем «опасные» байты внутри данных.
        if (length > 2)
        {
            payload[rng.Next(length)] = 0xF9;
            payload[rng.Next(length)] = 0x7E;
        }
        return new Spec(rng.Next(0, FrameConstants.MaxDlci + 1), type, rng.Next(2) == 0, rng.Next(2) == 0, payload);
    }

    private static (List<MuxFrame> Frames, List<FrameError> Errors) FeedInRandomChunks(byte[] stream, Random rng, int maxChunk)
    {
        var parser = new FrameParser();
        var frames = new List<MuxFrame>();
        var errors = new List<FrameError>();
        parser.FrameReceived += frames.Add;
        parser.Error += errors.Add;
        for (int offset = 0; offset < stream.Length;)
        {
            int n = Math.Min(stream.Length - offset, rng.Next(1, maxChunk + 1));
            parser.Feed(stream.AsSpan(offset, n));
            offset += n;
        }
        return (frames, errors);
    }

    /// <summary>60 seed по умолчанию; для длительной проверки: MUX_FUZZ_SEEDS=5000 dotnet test.</summary>
    public static IEnumerable<object[]> Seeds()
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("MUX_FUZZ_SEEDS"), out var n) && n > 0 ? n : 60;
        return Enumerable.Range(1, count).Select(i => new object[] { i });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomFramesSurviveRandomChunking(int seed)
    {
        var rng = new Random(seed);
        var specs = Enumerable.Range(0, rng.Next(1, 40)).Select(_ => RandomFrame(rng)).ToList();
        var stream = new List<byte>();
        int style = seed % 3; // 0 — отдельные флаги, 1 — общий флаг, 2 — лишние флаги
        foreach (var spec in specs)
        {
            var bytes = spec.Encode();
            if (style == 1 && stream.Count > 0)
                stream.AddRange(bytes.Skip(1));
            else
            {
                if (style == 2)
                    stream.AddRange(Enumerable.Repeat(FrameConstants.Flag, rng.Next(1, 4)));
                stream.AddRange(bytes);
            }
        }

        var (frames, errors) = FeedInRandomChunks(stream.ToArray(), rng, maxChunk: rng.Next(1, 64));

        Assert.Empty(errors);
        Assert.Equal(specs.Count, frames.Count);
        for (int i = 0; i < specs.Count; i++)
            Assert.True(specs[i].Matches(frames[i]), $"seed {seed}: кадр {i} отличается");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GarbageBetweenFramesIsSkipped(int seed)
    {
        var rng = new Random(seed);
        var specs = Enumerable.Range(0, rng.Next(1, 30)).Select(_ => RandomFrame(rng)).ToList();
        var stream = new List<byte>();
        foreach (var spec in specs)
        {
            if (rng.Next(3) == 0)
                for (int i = rng.Next(1, 20); i > 0; i--)
                    stream.Add((byte)rng.Next(0, 0xF9)); // мусор без 0xF9: текст, обрывки
            stream.AddRange(spec.Encode());
        }

        var (frames, errors) = FeedInRandomChunks(stream.ToArray(), rng, 32);

        Assert.Equal(specs.Count, frames.Count);
        for (int i = 0; i < specs.Count; i++)
            Assert.True(specs[i].Matches(frames[i]));
        // Мусор иногда похож на заголовок (тогда будет «плохой заголовок» или даже ошибка FCS) — вид ошибки
        // не важен; важно, что ни один кадр не потерян и не выдуман (проверено выше).
        Assert.NotNull(errors);
    }

    /// <summary>Порча FCS или данных UI-кадра: этот кадр отбрасывается, все остальные — на месте.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void CorruptedFramesAreDroppedOthersKept(int seed)
    {
        var rng = new Random(seed);
        var specs = Enumerable.Range(0, rng.Next(5, 30)).Select(_ => RandomFrame(rng)).ToList();
        var stream = new List<byte>();
        var intact = new List<Spec>();
        foreach (var spec in specs)
        {
            var bytes = spec.Encode();
            if (rng.Next(4) == 0)
            {
                // FCS — предпоследний байт; у UI с данными можно испортить и данные (их покрывает FCS).
                int index = spec.Type == FrameType.UI && spec.Payload.Length > 0 && rng.Next(2) == 0
                    ? bytes.Length - 2 - 1 - rng.Next(spec.Payload.Length)
                    : bytes.Length - 2;
                // Любое другое значение, кроме исходного и флага (превращение во флаг — другой вид порчи).
                byte original = bytes[index];
                do
                    bytes[index] = (byte)rng.Next(256);
                while (bytes[index] == original || bytes[index] == FrameConstants.Flag);
            }
            else
            {
                intact.Add(spec);
            }
            stream.AddRange(bytes);
        }

        var (frames, errors) = FeedInRandomChunks(stream.ToArray(), rng, 40);

        Assert.Equal(intact.Count, frames.Count);
        for (int i = 0; i < intact.Count; i++)
            Assert.True(intact[i].Matches(frames[i]), $"seed {seed}: кадр {i}");
        // Число ошибок FCS не проверяем: после отброшенного кадра поиск идёт и по его данным, где случайный 0xF9
        // может дать ещё одну ложную попытку, а об испорченном кадре в конце потока парсер сообщит позже.
        if (intact.Count < specs.Count)
            Assert.NotEmpty(errors);
    }

    /// <summary>
    /// Порча заголовка (адрес/тип/длина) или потеря байтов: парсер обязан ресинхронизироваться.
    /// Каждый целый кадр, перед которым нет испорченного, принят; «лишние» кадры — не больше числа испорченных.
    /// Ограничения протокола: у UIH FCS не покрывает данные (UIH с потерянным байтом данных примется с искажёнными
    /// данными), а 8-битная FCS пропускает ~1/256 испорченных заголовков.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void HeaderCorruptionAndByteLossResync(int seed)
    {
        var rng = new Random(seed);
        var specs = Enumerable.Range(0, rng.Next(5, 30)).Select(_ => RandomFrame(rng)).ToList();
        var stream = new List<byte>();
        var damaged = new bool[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            var bytes = specs[i].Encode().ToList();
            if (rng.Next(4) == 0)
            {
                damaged[i] = true;
                if (rng.Next(2) == 0)
                    bytes[1 + rng.Next(3)] ^= (byte)rng.Next(1, 256); // адрес, тип или длина
                else
                    bytes.RemoveRange(1 + rng.Next(bytes.Count - 2), 1); // потерян байт (переполнение UART)
            }
            stream.AddRange(bytes);
        }

        var (frames, _) = FeedInRandomChunks(stream.ToArray(), rng, 40);

        // «Лишние» кадры возможны только из испорченных: FCS в 07.10 восьмибитная, и примерно 1 из 256
        // испорченных заголовков проходит проверку (свойство протокола). Больше, чем испорчено, — ошибка парсера.
        int unexpected = frames.Count(f => !specs.Any(sp => sp.Matches(f)));
        Assert.True(unexpected <= damaged.Count(d => d), $"seed {seed}: лишних кадров {unexpected}, испорчено {damaged.Count(d => d)}");
        // Все целые кадры, перед которыми нет испорченного, приняты.
        for (int i = 0; i < specs.Count; i++)
        {
            if (damaged[i] || (i > 0 && damaged[i - 1]))
                continue;
            Assert.Contains(frames, f => specs[i].Matches(f));
        }
    }

    /// <summary>
    /// Ложное начало кадра (мусор, похожий на заголовок с большой длиной) не должно задерживать настоящие кадры:
    /// ответ модема, пришедший следом, выдаётся сразу, без ожидания «хвоста» ложного кадра.
    /// </summary>
    [Fact]
    public void FalseStartDoesNotDelayFollowingFrames()
    {
        var parser = new FrameParser { MaxPayloadLength = 1509 };
        var frames = new List<MuxFrame>();
        parser.FrameReceived += frames.Add;
        // F9, адрес 0x07 (DLC1), UIH, длина 2 байта = 1000 — а данных нет: мусор после обрыва.
        parser.Feed(new byte[] { 0xF9, 0x07, 0xEF, 0xD0, 0x0F, 0x41, 0x42 });
        Assert.Empty(frames);
        parser.Feed(FrameEncoder.Uih(1, "\r\nOK\r\n"u8));
        Assert.Equal("\r\nOK\r\n"u8.ToArray(), Assert.Single(frames).Payload);
        Assert.True(parser.BufferedBytes <= 1); // остался максимум закрывающий флаг
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(126)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1509)]
    [InlineData(4096)]
    [InlineData(32767)]
    public void LengthBoundaries(int length)
    {
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
        foreach (var type in new[] { FrameType.UIH, FrameType.UI })
        {
            var parser = new FrameParser();
            MuxFrame? got = null;
            parser.FrameReceived += f => got = f;
            parser.Feed(FrameEncoder.Encode(9, type, true, false, payload));
            Assert.NotNull(got);
            Assert.Equal(payload, got!.Payload);
        }
    }

    [Fact]
    public void LengthAboveMaximumIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameEncoder.Uih(1, new byte[32768]));
        var parser = new FrameParser { MaxPayloadLength = 127 };
        var errors = new List<FrameError>();
        parser.Error += errors.Add;
        var frames = new List<MuxFrame>();
        parser.FrameReceived += frames.Add;
        parser.Feed(FrameEncoder.Uih(1, new byte[200]).Concat(FrameEncoder.Uih(2, "OK"u8)).ToArray());
        Assert.Contains(errors, e => e.Kind == FrameErrorKind.BadHeader);
        Assert.Contains(frames, f => f.Dlci == 2);
    }

    /// <summary>Разрез потока в каждой возможной точке кадра с двухбайтовой длиной.</summary>
    [Fact]
    public void SplitAtEveryPosition()
    {
        var bytes = FrameEncoder.Uih(5, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        for (int split = 1; split < bytes.Length; split++)
        {
            var parser = new FrameParser();
            int count = 0;
            parser.FrameReceived += _ => count++;
            parser.Feed(bytes.AsSpan(0, split));
            Assert.Equal(0, count);
            parser.Feed(bytes.AsSpan(split));
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public void AddressWithoutEaBitIsRejected()
    {
        var bad = FrameEncoder.Uih(1, "X"u8);
        bad[1] &= 0xFE; // EA=0 — в Basic Option недопустимо
        var parser = new FrameParser();
        var frames = new List<MuxFrame>();
        var errors = new List<FrameError>();
        parser.FrameReceived += frames.Add;
        parser.Error += errors.Add;
        parser.Feed(bad.Concat(FrameEncoder.Uih(1, "OK"u8)).ToArray());
        Assert.Single(frames);
        Assert.Contains(errors, e => e.Kind == FrameErrorKind.BadHeader);
    }

    [Fact]
    public void UnknownControlFieldIsRejected()
    {
        var bad = FrameEncoder.Uih(1, "X"u8);
        bad[2] = 0x11; // не SABM/UA/DM/DISC/UIH/UI
        var parser = new FrameParser();
        var frames = new List<MuxFrame>();
        parser.FrameReceived += frames.Add;
        parser.Feed(bad.Concat(FrameEncoder.Uih(3, "OK"u8)).ToArray());
        Assert.Equal(3, Assert.Single(frames).Dlci);
    }

    [Fact]
    public void LongRunOfFlagsIsIgnored()
    {
        var parser = new FrameParser();
        var errors = new List<FrameError>();
        var frames = new List<MuxFrame>();
        parser.Error += errors.Add;
        parser.FrameReceived += frames.Add;
        parser.Feed(Enumerable.Repeat(FrameConstants.Flag, 500).Concat(FrameEncoder.Sabm(0)).Concat(Enumerable.Repeat(FrameConstants.Flag, 3)).ToArray());
        Assert.Single(frames);
        Assert.Empty(errors);
    }

    /// <summary>UIH: FCS не покрывает данные (так по стандарту) — порча данных не обнаруживается, кадр принимается.</summary>
    [Fact]
    public void UihPayloadIsNotCoveredByFcs()
    {
        var frame = FrameEncoder.Uih(1, "DATA"u8);
        frame[5] ^= 0x20;
        var parser = new FrameParser();
        MuxFrame? got = null;
        parser.FrameReceived += f => got = f;
        parser.Feed(frame);
        Assert.NotNull(got);
        Assert.NotEqual("DATA"u8.ToArray(), got!.Payload);
    }

    [Fact]
    public void ControlMessageWithMultiOctetLength()
    {
        // Длина значения в несколько октетов (EA=0 в первом) — для PN/Test с длинными данными.
        var value = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        var payload = new List<byte> { (byte)((byte)ControlMessageType.Test | 0x03), (byte)((200 & 0x7F) << 1), (byte)((200 >> 7) << 1 | 1) };
        payload.AddRange(value);
        var msg = Assert.Single(ControlMessage.ParseAll(payload.ToArray()));
        Assert.Equal(ControlMessageType.Test, msg.Type);
        Assert.Equal(value, msg.Value);
    }
}

public class DlciRangeTests
{
    /// <summary>Почему DLCI 62/63 запрещены: адрес DLCI 62 с C/R=0 совпадает с флагом 0xF9.</summary>
    [Fact]
    public void Dlci62AddressCollidesWithFlag()
        => Assert.Equal(FrameConstants.Flag, (byte)((62 << 2) | 0x01));

    [Fact]
    public void EncoderRejectsReservedDlci()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameEncoder.Sabm(62));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameEncoder.Uih(63, "X"u8));
        Assert.Equal(0xF7, FrameEncoder.Sabm(61)[1]);
    }
}

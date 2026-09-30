using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Tests;

public class FcsTests
{
    // Эталонные кадры из документации модемов (Quectel/SIMCom) и 3GPP TS 27.010.
    [Theory]
    [InlineData("03 3F 01", 0x1C)] // SABM DLC0
    [InlineData("03 73 01", 0xD7)] // UA DLC0
    [InlineData("07 3F 01", 0xDE)] // SABM DLC1
    [InlineData("03 EF 09", 0xFB)] // UIH DLC0 (MSC)
    [InlineData("03 EF 05", 0xF2)] // UIH DLC0 (CLD)
    public void Compute_MatchesReferenceFrames(string header, byte expected)
    {
        Assert.True(Hex.TryParse(header, out var bytes));
        Assert.Equal(expected, Fcs.Compute(bytes));
        Assert.True(Fcs.Check(bytes, ReadOnlySpan<byte>.Empty, expected));
        Assert.False(Fcs.Check(bytes, ReadOnlySpan<byte>.Empty, (byte)(expected ^ 0x01)));
    }
}

public class FrameEncoderTests
{
    [Fact]
    public void Sabm_Dlc0() => Assert.Equal("F9 03 3F 01 1C F9", Hex.Format(FrameEncoder.Sabm(0)));

    [Fact]
    public void Sabm_Dlc1() => Assert.Equal("F9 07 3F 01 DE F9", Hex.Format(FrameEncoder.Sabm(1)));

    [Fact]
    public void Msc_MatchesQuectelExample()
    {
        var msc = ControlMessage.Msc(1, ModemSignals.RTC | ModemSignals.RTR);
        Assert.Equal("F9 03 EF 09 E3 05 07 0D FB F9", Hex.Format(FrameEncoder.Uih(0, msc.Encode())));
    }

    [Fact]
    public void CloseDown_MatchesReference()
        => Assert.Equal("F9 03 EF 05 C3 01 F2 F9", Hex.Format(FrameEncoder.Uih(0, ControlMessage.CloseDown().Encode())));

    [Fact]
    public void Uih_WithAtCommand()
    {
        var frame = FrameEncoder.Uih(1, "ATI\r"u8);
        Assert.Equal(new byte[] { 0xF9, 0x07, 0xEF, 0x09, 0x41, 0x54, 0x49, 0x0D }, frame.Take(8).ToArray());
        Assert.Equal(0xF9, frame[frame.Length - 1]);
    }

    [Fact]
    public void LongPayload_UsesTwoByteLength()
    {
        var payload = new byte[300];
        var frame = FrameEncoder.Uih(2, payload);
        Assert.Equal(300 + 7, frame.Length);
        Assert.Equal((300 & 0x7F) << 1, frame[3]); // EA=0
        Assert.Equal(300 >> 7, frame[4]);
    }
}

public class FrameParserTests
{
    private static (List<MuxFrame> Frames, List<FrameError> Errors, FrameParser Parser) Create()
    {
        var parser = new FrameParser();
        var frames = new List<MuxFrame>();
        var errors = new List<FrameError>();
        parser.FrameReceived += frames.Add;
        parser.Error += errors.Add;
        return (frames, errors, parser);
    }

    [Fact]
    public void ParsesSingleFrame()
    {
        var (frames, errors, parser) = Create();
        parser.Feed(FrameEncoder.Uih(1, "OK"u8));
        var f = Assert.Single(frames);
        Assert.Equal(1, f.Dlci);
        Assert.Equal(FrameType.UIH, f.Type);
        Assert.Equal("OK"u8.ToArray(), f.Payload);
        Assert.Empty(errors);
    }

    [Fact]
    public void ParsesFrameFedByteByByte()
    {
        var (frames, errors, parser) = Create();
        foreach (var b in FrameEncoder.Uih(3, "$GPGGA,123\r\n"u8))
            parser.Feed(new[] { b });
        Assert.Single(frames);
        Assert.Empty(errors);
    }

    [Fact]
    public void ParsesBackToBackFramesWithSharedFlag()
    {
        var (frames, _, parser) = Create();
        var a = FrameEncoder.Uih(1, "A"u8);
        var b = FrameEncoder.Uih(2, "B"u8);
        // Закрывающий флаг первого кадра служит открывающим для второго.
        parser.Feed(a.Concat(b.Skip(1)).ToArray());
        Assert.Equal(new[] { 1, 2 }, frames.Select(f => f.Dlci));
    }

    [Fact]
    public void PayloadMayContainFlagByte()
    {
        var (frames, errors, parser) = Create();
        var payload = new byte[] { 0xF9, 0xF9, 0x00, 0xF9, 0xA5, 0x7E };
        parser.Feed(FrameEncoder.Uih(2, payload));
        Assert.Equal(payload, Assert.Single(frames).Payload);
        Assert.Empty(errors);
    }

    [Fact]
    public void SkipsGarbageBeforeFrame()
    {
        var (frames, errors, parser) = Create();
        parser.Feed("\r\nOK\r\n"u8.ToArray().Concat(FrameEncoder.Sabm(0)).ToArray());
        Assert.Single(frames);
        Assert.Contains(errors, e => e.Kind == FrameErrorKind.Garbage);
    }

    [Fact]
    public void DropsFrameWithBadFcsAndResyncs()
    {
        var (frames, errors, parser) = Create();
        var bad = FrameEncoder.Uih(1, "XX"u8);
        bad[bad.Length - 2] ^= 0xFF;
        parser.Feed(bad.Concat(FrameEncoder.Uih(1, "OK"u8)).ToArray());
        Assert.Equal("OK"u8.ToArray(), Assert.Single(frames).Payload);
        Assert.Contains(errors, e => e.Kind == FrameErrorKind.BadFcs);
    }

    [Fact]
    public void ResyncsAfterTruncatedFrame()
    {
        var (frames, _, parser) = Create();
        var cut = FrameEncoder.Uih(1, "HELLO WORLD"u8).Take(6).ToArray();
        parser.Feed(cut.Concat(FrameEncoder.Uih(2, "OK"u8)).Concat(FrameEncoder.Uih(2, "!"u8)).ToArray());
        Assert.Contains(frames, f => f.Dlci == 2 && f.Payload.SequenceEqual("OK"u8.ToArray()));
        Assert.Contains(frames, f => f.Dlci == 2 && f.Payload.SequenceEqual("!"u8.ToArray()));
    }

    [Fact]
    public void UiFrameFcsCoversPayload()
    {
        var (frames, errors, parser) = Create();
        var ui = FrameEncoder.Encode(1, FrameType.UI, true, false, "DATA"u8);
        var corrupted = (byte[])ui.Clone();
        corrupted[5] ^= 0x20; // меняем байт данных — для UI это ошибка FCS
        parser.Feed(corrupted);
        Assert.Empty(frames);
        Assert.Contains(errors, e => e.Kind == FrameErrorKind.BadFcs);
        parser.Feed(ui);
        Assert.Single(frames);
    }

    [Fact]
    public void TwoByteLengthRoundTrip()
    {
        var (frames, _, parser) = Create();
        var payload = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        parser.Feed(FrameEncoder.Uih(5, payload));
        Assert.Equal(payload, Assert.Single(frames).Payload);
    }

    [Fact]
    public void DecodesAddressAndControlBits()
    {
        var (frames, _, parser) = Create();
        parser.Feed(FrameEncoder.Encode(7, FrameType.UA, false, true, default));
        var f = Assert.Single(frames);
        Assert.Equal(7, f.Dlci);
        Assert.Equal(FrameType.UA, f.Type);
        Assert.False(f.CommandResponse);
        Assert.True(f.PollFinal);
    }
}

public class ControlMessageTests
{
    [Fact]
    public void ParsesMultipleMessages()
    {
        var payload = ControlMessage.Msc(2, ModemSignals.FC).Encode()
            .Concat(new ControlMessage(ControlMessageType.Test, true, new byte[] { 1, 2, 3 }).Encode()).ToArray();
        var list = ControlMessage.ParseAll(payload);
        Assert.Equal(2, list.Count);
        Assert.Equal(ControlMessageType.MSC, list[0].Type);
        Assert.True(list[0].IsCommand);
        Assert.Equal(2, list[0].Value[0] >> 2);
        Assert.Equal(ControlMessageType.Test, list[1].Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, list[1].Value);
    }

    [Fact]
    public void ResponseClearsCrBit()
    {
        var rsp = ControlMessage.CloseDown().ToResponse();
        Assert.Equal(0xC1, rsp.TypeOctet);
    }
}

public class HexTests
{
    [Theory]
    [InlineData("41 54 0D", new byte[] { 0x41, 0x54, 0x0D })]
    [InlineData("41540d", new byte[] { 0x41, 0x54, 0x0D })]
    [InlineData("0x1A, 0x0d", new byte[] { 0x1A, 0x0D })]
    public void Parse(string text, byte[] expected)
    {
        Assert.True(Hex.TryParse(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void RejectsInvalid() => Assert.False(Hex.TryParse("zz", out _));
}

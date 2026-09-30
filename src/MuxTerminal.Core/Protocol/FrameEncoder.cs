namespace MuxTerminal.Core.Protocol;

/// <summary>Упаковщик кадров GSM 07.10 Basic Option.</summary>
public static class FrameEncoder
{
    public static byte EncodeAddress(int dlci, bool commandResponse)
        => (byte)((dlci << 2) | (commandResponse ? 0x02 : 0x00) | 0x01);

    public static byte EncodeControl(FrameType type, bool pollFinal)
        => (byte)((byte)type | (pollFinal ? FrameConstants.PollFinalBit : 0));

    public static byte[] Encode(MuxFrame frame)
        => Encode(frame.Dlci, frame.Type, frame.CommandResponse, frame.PollFinal, frame.Payload);

    /// <summary>
    /// Собирает кадр: F9 | Address | Control | Length(1..2) | Payload | FCS | F9.
    /// FCS для UIH считается по Address+Control+Length, для остальных типов — ещё и по Payload.
    /// </summary>
    public static byte[] Encode(int dlci, FrameType type, bool commandResponse, bool pollFinal, ReadOnlySpan<byte> payload)
    {
        if (dlci is < 0 or > FrameConstants.MaxDlci)
            throw new ArgumentOutOfRangeException(nameof(dlci));
        if (payload.Length > FrameConstants.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload длиннее 32767 байт");

        int lengthBytes = payload.Length > 127 ? 2 : 1;
        var frame = new byte[1 + 2 + lengthBytes + payload.Length + 2];
        int i = 0;
        frame[i++] = FrameConstants.Flag;
        frame[i++] = EncodeAddress(dlci, commandResponse);
        frame[i++] = EncodeControl(type, pollFinal);
        if (lengthBytes == 1)
        {
            frame[i++] = (byte)((payload.Length << 1) | 0x01);
        }
        else
        {
            frame[i++] = (byte)((payload.Length & 0x7F) << 1);
            frame[i++] = (byte)(payload.Length >> 7);
        }
        int headerEnd = i;
        payload.CopyTo(frame.AsSpan(i));
        i += payload.Length;

        var header = frame.AsSpan(1, headerEnd - 1);
        frame[i++] = type == FrameType.UIH ? Fcs.Compute(header) : Fcs.Compute(header, payload);
        frame[i] = FrameConstants.Flag;
        return frame;
    }

    public static byte[] Sabm(int dlci) => Encode(dlci, FrameType.SABM, true, true, default);
    public static byte[] Disc(int dlci) => Encode(dlci, FrameType.DISC, true, true, default);
    public static byte[] Uih(int dlci, ReadOnlySpan<byte> data) => Encode(dlci, FrameType.UIH, true, false, data);
}

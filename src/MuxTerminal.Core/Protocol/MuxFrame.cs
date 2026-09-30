using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Protocol;

/// <summary>Разобранный кадр GSM 07.10 (Basic Option).</summary>
public sealed class MuxFrame
{
    public MuxFrame(int dlci, FrameType type, bool commandResponse, bool pollFinal, byte[] payload, byte[]? raw = null)
    {
        if (dlci is < 0 or > FrameConstants.MaxAddressDlci)
            throw new ArgumentOutOfRangeException(nameof(dlci), "DLCI должен быть в диапазоне 0..63");
        Dlci = dlci;
        Type = type;
        CommandResponse = commandResponse;
        PollFinal = pollFinal;
        Payload = payload ?? Array.Empty<byte>();
        Raw = raw ?? FrameEncoder.Encode(this);
    }

    public int Dlci { get; }
    public FrameType Type { get; }
    /// <summary>Бит C/R поля Address.</summary>
    public bool CommandResponse { get; }
    /// <summary>Бит P/F поля Control.</summary>
    public bool PollFinal { get; }
    public byte[] Payload { get; }
    /// <summary>Кадр целиком, включая оба флага 0xF9.</summary>
    public byte[] Raw { get; }

    public override string ToString()
    {
        var s = $"DLC{Dlci} {Type}{(PollFinal ? (Type is FrameType.SABM or FrameType.DISC ? " P" : " F") : "")} C/R={(CommandResponse ? 1 : 0)} len={Payload.Length}";
        if (Payload.Length > 0)
            s += " \"" + Hex.ToPrintable(Payload, 64) + "\"";
        return s;
    }
}

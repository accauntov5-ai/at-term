using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Protocol;

/// <summary>Типы сообщений канала управления DLC0 (биты 2..7 октета типа).</summary>
public enum ControlMessageType : byte
{
    /// <summary>DLC parameter negotiation.</summary>
    PN = 0x80,
    /// <summary>Power saving control.</summary>
    PSC = 0x40,
    /// <summary>Multiplexer close down.</summary>
    CLD = 0xC0,
    /// <summary>Test command.</summary>
    Test = 0x20,
    /// <summary>Flow control on (для всех каналов).</summary>
    FCon = 0xA0,
    /// <summary>Flow control off (для всех каналов).</summary>
    FCoff = 0x60,
    /// <summary>Modem status command (V.24-сигналы канала).</summary>
    MSC = 0xE0,
    /// <summary>Non supported command response.</summary>
    NSC = 0x10,
    /// <summary>Remote port negotiation.</summary>
    RPN = 0x90,
    /// <summary>Remote line status.</summary>
    RLS = 0x50,
    /// <summary>Service negotiation.</summary>
    SNC = 0xD0,
}

/// <summary>V.24-сигналы в сообщении MSC.</summary>
[Flags]
public enum ModemSignals : byte
{
    None = 0,
    EA = 0x01,
    /// <summary>Flow Control: 1 — отправитель не может принимать данные.</summary>
    FC = 0x02,
    /// <summary>Ready To Communicate (DTR/DSR).</summary>
    RTC = 0x04,
    /// <summary>Ready To Receive (RTS/CTS).</summary>
    RTR = 0x08,
    /// <summary>Incoming Call (RI).</summary>
    IC = 0x40,
    /// <summary>Data Valid (DCD).</summary>
    DV = 0x80,
}

/// <summary>Сообщение канала управления, переносимое в UIH-кадре по DLC0.</summary>
public sealed record ControlMessage(ControlMessageType Type, bool IsCommand, byte[] Value)
{
    public byte TypeOctet => (byte)((byte)Type | (IsCommand ? 0x02 : 0x00) | 0x01);

    public byte[] Encode()
    {
        if (Value.Length > 127)
            throw new InvalidOperationException("Слишком длинное значение управляющего сообщения");
        var result = new byte[2 + Value.Length];
        result[0] = TypeOctet;
        result[1] = (byte)((Value.Length << 1) | 0x01);
        Value.CopyTo(result, 2);
        return result;
    }

    /// <summary>Разбирает содержимое UIH-кадра DLC0 (может содержать несколько сообщений).</summary>
    public static IReadOnlyList<ControlMessage> ParseAll(ReadOnlySpan<byte> payload)
    {
        var list = new List<ControlMessage>();
        int i = 0;
        while (i < payload.Length)
        {
            // Октет типа может быть расширен (EA=0) — в 27.010 такие не определены, пропускаем расширения.
            byte typeOctet = payload[i++];
            while ((payload[i - 1] & 0x01) == 0 && i < payload.Length)
                i++;

            int length = 0, shift = 0;
            while (i < payload.Length)
            {
                byte b = payload[i++];
                length |= (b >> 1) << shift;
                shift += 7;
                if ((b & 0x01) != 0)
                    break;
            }
            if (i + length > payload.Length)
                length = payload.Length - i; // усечённое сообщение — берём что есть

            var value = payload.Slice(i, length).ToArray();
            i += length;
            list.Add(new ControlMessage((ControlMessageType)(typeOctet & 0xFC), (typeOctet & 0x02) != 0, value));
        }
        return list;
    }

    public static ControlMessage Msc(int dlci, ModemSignals signals, bool isCommand = true)
        => new(ControlMessageType.MSC, isCommand, new[] { (byte)((dlci << 2) | 0x03), (byte)(signals | ModemSignals.EA) });

    public static ControlMessage CloseDown() => new(ControlMessageType.CLD, true, Array.Empty<byte>());

    public ControlMessage ToResponse() => this with { IsCommand = false };

    public override string ToString()
    {
        string name = Enum.IsDefined(Type) ? Type.ToString() : $"0x{(byte)Type:X2}";
        string kind = IsCommand ? "cmd" : "rsp";
        if (Type == ControlMessageType.MSC && Value.Length >= 2)
        {
            var sig = (ModemSignals)Value[1];
            return $"{name} {kind} DLC{Value[0] >> 2} [{DescribeSignals(sig)}]";
        }
        return Value.Length == 0 ? $"{name} {kind}" : $"{name} {kind} [{Hex.Format(Value)}]";
    }

    private static string DescribeSignals(ModemSignals s)
    {
        var parts = new List<string>();
        if (s.HasFlag(ModemSignals.FC)) parts.Add("FC");
        if (s.HasFlag(ModemSignals.RTC)) parts.Add("RTC");
        if (s.HasFlag(ModemSignals.RTR)) parts.Add("RTR");
        if (s.HasFlag(ModemSignals.IC)) parts.Add("IC");
        if (s.HasFlag(ModemSignals.DV)) parts.Add("DV");
        return parts.Count == 0 ? "-" : string.Join(' ', parts);
    }
}

namespace MuxTerminal.Core.Protocol;

/// <summary>
/// Frame Check Sequence для GSM 07.10 / 3GPP TS 27.010.
/// CRC-8, полином x^8 + x^2 + x + 1 (в отражённом виде 0xE0), начальное значение 0xFF,
/// результат — дополнение до единиц. При проверке CRC по полям + принятому FCS даёт 0xCF.
/// </summary>
public static class Fcs
{
    public const byte GoodRemainder = 0xCF;

    private static readonly byte[] Table = BuildTable();

    private static byte[] BuildTable()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            byte crc = (byte)i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x01) != 0 ? (byte)((crc >> 1) ^ 0xE0) : (byte)(crc >> 1);
            table[i] = crc;
        }
        return table;
    }

    private static byte Accumulate(byte crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[crc ^ b];
        return crc;
    }

    /// <summary>Вычисляет FCS для последовательности байт (без флагов).</summary>
    public static byte Compute(ReadOnlySpan<byte> data) => (byte)(0xFF - Accumulate(0xFF, data));

    /// <summary>Вычисляет FCS для заголовка и (опционально) данных, не копируя их в общий буфер.</summary>
    public static byte Compute(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
        => (byte)(0xFF - Accumulate(Accumulate(0xFF, header), payload));

    /// <summary>Проверяет принятый FCS.</summary>
    public static bool Check(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, byte received)
    {
        byte crc = Accumulate(Accumulate(0xFF, header), payload);
        crc = Table[crc ^ received];
        return crc == GoodRemainder;
    }
}

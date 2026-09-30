namespace MuxTerminal.Core.Protocol;

/// <summary>Тип кадра (поле Control без бита P/F).</summary>
public enum FrameType : byte
{
    /// <summary>Set Asynchronous Balanced Mode — открытие канала.</summary>
    SABM = 0x2F,
    /// <summary>Unnumbered Acknowledgement — подтверждение.</summary>
    UA = 0x63,
    /// <summary>Disconnected Mode — отказ / канал не открыт.</summary>
    DM = 0x0F,
    /// <summary>Disconnect — закрытие канала.</summary>
    DISC = 0x43,
    /// <summary>Unnumbered Information with Header check — данные, FCS только по заголовку.</summary>
    UIH = 0xEF,
    /// <summary>Unnumbered Information — данные, FCS по заголовку и данным.</summary>
    UI = 0x03,
}

public static class FrameConstants
{
    public const byte Flag = 0xF9;
    public const byte PollFinalBit = 0x10;
    /// <summary>
    /// Максимальный номер канала данных. DLCI 62 и 63 зарезервированы стандартом (27.010, п. 5.6):
    /// адрес DLCI 62 с C/R=0 равен 0xF9 — совпадает с флагом и разрушил бы разбор потока.
    /// </summary>
    public const int MaxDlci = 61;
    /// <summary>Максимум, помещающийся в поле адреса (при приёме принимаем любой).</summary>
    public const int MaxAddressDlci = 63;
    /// <summary>Максимальная длина, кодируемая двухбайтовым полем Length (15 бит).</summary>
    public const int MaxLength = 0x7FFF;
    /// <summary>N1 по умолчанию для Basic Option.</summary>
    public const int DefaultN1 = 31;
}

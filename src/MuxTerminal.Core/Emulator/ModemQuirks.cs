namespace MuxTerminal.Core.Emulator;

/// <summary>
/// Особенности поведения конкретных модемов. Все они допустимы стандартом 27.010 (или встречаются на практике),
/// и терминал обязан с ними работать. Используются эмулятором для проверки совместимости.
/// </summary>
public sealed class ModemQuirks
{
    public string Name { get; set; } = "Generic";

    /// <summary>Эхо команд в AT-режиме (ATE1).</summary>
    public bool Echo { get; set; } = true;

    /// <summary>Ответ на AT+CMUX: "OK", "ERROR", "+CME ERROR: 3"…</summary>
    public string CmuxResponse { get; set; } = "OK";

    /// <summary>Незапрошенное сообщение (URC) перед OK на AT+CMUX, например "+CREG: 1".</summary>
    public string? UrcBeforeCmuxOk { get; set; }

    /// <summary>Сразу за OK в той же посылке — кадр Test по DLC0 (модем начинает MUX-обмен без паузы).</summary>
    public bool FrameRightAfterOk { get; set; }

    /// <summary>Окончание строк ответов: "\r\n" (стандарт) или только "\r".</summary>
    public string LineEnding { get; set; } = "\r\n";

    /// <summary>Данные в кадрах UI (FCS по данным) вместо UIH.</summary>
    public bool DataFramesAsUi { get; set; }

    /// <summary>Нестандартный бит C/R=1 в кадрах данных от модема.</summary>
    public bool DataCrBit { get; set; }

    /// <summary>Модем сам присылает MSC после открытия канала.</summary>
    public bool SendMscOnOpen { get; set; } = true;

    /// <summary>Модем не отдаёт данные в канал, пока не получит MSC от терминала (часть Telit/Quectel).</summary>
    public bool RequireMscBeforeData { get; set; }

    /// <summary>Максимальный номер канала; на SABM для больших — DM (SIM800: 3).</summary>
    public int MaxDlci { get; set; } = 61;

    /// <summary>Каналы, на SABM которых модем не отвечает вовсе.</summary>
    public HashSet<int> IgnoreSabm { get; set; } = new();

    /// <summary>Поддерживает CLD; иначе отвечает NSC, и закрывать MUX нужно DISC на DLC0.</summary>
    public bool SupportsCld { get; set; } = true;

    /// <summary>Закрывающий флаг кадра служит открывающим для следующего (один 0xF9 между кадрами).</summary>
    public bool SharedFlags { get; set; }

    /// <summary>Лишние флаги 0xF9 перед каждым кадром.</summary>
    public int ExtraFlags { get; set; }

    /// <summary>&gt;0 — выдача в порт кусками случайного размера до MaxChunk байт (как UART/USB).</summary>
    public int MaxChunk { get; set; }

    /// <summary>Модем игнорирует N1 и шлёт кадры данных до этого размера.</summary>
    public int? ForceN1 { get; set; }

    /// <summary>Вероятность мусора (случайные байты без 0xF9) между кадрами.</summary>
    public double NoiseProbability { get; set; }

    public int Seed { get; set; } = 1;

    public override string ToString() => Name;
}

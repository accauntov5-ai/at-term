using MuxTerminal.Core.Protocol;

namespace MuxTerminal.Core.Session;

public sealed class MuxSessionOptions
{
    /// <summary>
    /// Команда перевода модема в MUX. Формат: AT+CMUX=&lt;mode&gt;[,&lt;subset&gt;[,&lt;port_speed&gt;[,&lt;N1&gt;[,&lt;T1&gt;[,&lt;N2&gt;[,&lt;T2&gt;[,&lt;T3&gt;[,&lt;k&gt;]]]]]]]].
    /// Поддерживается только mode=0 (Basic Option).
    /// </summary>
    public string CmuxCommand { get; set; } = "AT+CMUX=0";

    /// <summary>Модем уже в режиме MUX — не отправлять AT+CMUX.</summary>
    public bool SkipCmuxCommand { get; set; }

    /// <summary>Перед AT+CMUX отправить "AT" для синхронизации автобода/проверки связи.</summary>
    public bool SendAtProbe { get; set; } = true;

    /// <summary>Каналы, открываемые при старте (помимо DLC0).</summary>
    public IReadOnlyList<int> Channels { get; set; } = new[] { 1, 2, 3 };

    /// <summary>Таймаут ответа на AT-команду.</summary>
    public TimeSpan AtTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>T1 — таймаут ожидания UA/DM на SABM/DISC.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>N2 — число повторов SABM/DISC.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>После открытия канала отправлять MSC с RTC/RTR (многим модемам без этого данные не идут).</summary>
    public bool SendMscOnOpen { get; set; } = true;

    public ModemSignals MscSignals { get; set; } = ModemSignals.RTC | ModemSignals.RTR | ModemSignals.DV;

    /// <summary>Пауза после OK на AT+CMUX, прежде чем слать SABM (модему нужно переключиться).</summary>
    public TimeSpan SwitchDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// N1 — максимальный размер Payload. Если null — берётся из параметров AT+CMUX (по умолчанию 31).
    /// </summary>
    public int? MaxFrameSizeOverride { get; set; }

    public int MaxFrameSize => MaxFrameSizeOverride ?? CmuxParameters.Parse(CmuxCommand).N1;
}

/// <summary>Параметры команды AT+CMUX.</summary>
public sealed record CmuxParameters(int Mode, int N1)
{
    public static CmuxParameters Parse(string command)
    {
        int eq = command.IndexOf('=');
        if (eq < 0)
            return new CmuxParameters(0, FrameConstants.DefaultN1);
        var parts = command[(eq + 1)..].Split(',');
        int mode = TryInt(parts, 0) ?? 0;
        int n1 = TryInt(parts, 3) ?? FrameConstants.DefaultN1;
        if (n1 is < 1 or > FrameConstants.MaxLength)
            n1 = FrameConstants.DefaultN1;
        return new CmuxParameters(mode, n1);
    }

    private static int? TryInt(string[] parts, int index)
        => index < parts.Length && int.TryParse(parts[index].Trim(), out var v) ? v : null;
}

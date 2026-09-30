using System.Globalization;
using System.Text;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Session;

namespace MuxTerminal.Core.Tests;

/// <summary>
/// Один и тот же сценарий против модемов с разными особенностями реализации 27.010.
/// Профили смоделированы по документированному поведению семейств модемов; это не замена проверке
/// на настоящем устройстве, но гарантирует, что терминал не опирается на поведение одного модема.
/// </summary>
public class ModemProfileTests
{
    public sealed record Profile(string Name, ModemQuirks Quirks, string Cmux)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<object[]> Profiles() => new[]
    {
        new Profile("Эталон (как в стандарте)", new ModemQuirks(), "AT+CMUX=0"),
        new Profile("Quectel EC2x/BG9x: N1=127, USB-пакеты по 64 байта",
            new ModemQuirks { MaxChunk = 64 }, "AT+CMUX=0,0,5,127,10,3,30,10,2"),
        new Profile("SIMCom SIM800: только DLC 1..3, URC перед OK",
            new ModemQuirks { MaxDlci = 3, UrcBeforeCmuxOk = "+CPIN: READY" }, "AT+CMUX=0,0,5,127"),
        new Profile("Telit: MSC сам не шлёт, данные только после MSC от терминала",
            new ModemQuirks { SendMscOnOpen = false, RequireMscBeforeData = true }, "AT+CMUX=0,0,5,127,10,3,30,10,2"),
        new Profile("u-blox: N1=1509 (двухбайтовая длина)",
            new ModemQuirks { MaxChunk = 200 }, "AT+CMUX=0,0,,1509,253,3,254,,"),
        new Profile("Общий флаг между кадрами + UART по 1 байту",
            new ModemQuirks { SharedFlags = true, MaxChunk = 1 }, "AT+CMUX=0"),
        new Profile("Двойные флаги + мусор на линии",
            new ModemQuirks { ExtraFlags = 2, NoiseProbability = 0.3, MaxChunk = 7 }, "AT+CMUX=0"),
        new Profile("Кадры UI вместо UIH, нестандартный C/R",
            new ModemQuirks { DataFramesAsUi = true, DataCrBit = true }, "AT+CMUX=0"),
        new Profile("Только \\r в ответах, без эха, кадр сразу за OK",
            new ModemQuirks { LineEnding = "\r", Echo = false, FrameRightAfterOk = true }, "AT+CMUX=0"),
        new Profile("Без поддержки CLD (закрытие через DISC DLC0)",
            new ModemQuirks { SupportsCld = false }, "AT+CMUX=0"),
        new Profile("Игнорирует N1=31 и шлёт кадры по 127 байт",
            new ModemQuirks { ForceN1 = 127 }, "AT+CMUX=0"),
    }.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task FullScenario(Profile profile)
    {
        await using var h = new Harness(profile.Quirks, new MuxSessionOptions { CmuxCommand = profile.Cmux, Channels = new[] { 1, 2, 3 } });
        await h.Session.StartAsync();

        // Запуск: все каналы открыты.
        Assert.Equal(MuxSessionState.Running, h.Session.State);
        foreach (var dlci in new[] { 1, 2, 3 })
            Assert.Equal(ChannelState.Open, h.Session.GetChannelState(dlci));

        // DLC1: ответ только в своём канале.
        await h.SendAsync(1, "ATI\r");
        var ch1 = await h.WaitTextAsync(1, "OK");
        Assert.Contains("MUX-EMU", ch1);

        // DLC2: двоичные данные со всеми значениями байтов (в т.ч. 0xF9) — без искажений.
        await h.SendAsync(2, "ATE0\r");
        await h.WaitTextAsync(2, "OK");
        h.Clear(2);
        await h.SendAsync(2, "AT+BINTEST\r");
        await Harness.WaitAsync(() => h.Bytes(2).Length >= 256 + 4, 3000, "DLC2: нет двоичных данных");
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), h.Bytes(2).Take(256));

        // Одновременный трафик во всех каналах: ни одного байта «не туда».
        h.Clear(1);
        h.Clear(2);
        var sends = Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            Task.Run(() => h.SendAsync(1, "AT+CGMI\r")),
            Task.Run(() => h.SendAsync(2, "AT+CGSN\r")),
        });
        await Task.WhenAll(sends);
        await Harness.WaitAsync(() => Count(h.Text(1), "MUX-EMU") == 20 && Count(h.Text(2), "867000000000001") == 20,
            5000, $"Не все ответы: DLC1={Count(h.Text(1), "MUX-EMU")}, DLC2={Count(h.Text(2), "867000000000001")}");
        Assert.DoesNotContain("867000000000001", h.Text(1));
        Assert.DoesNotContain("MUX-EMU", h.Text(2));
        Assert.DoesNotContain("$GP", h.Text(1) + h.Text(2));

        // DLC3: поток NMEA — только корректные предложения, ничего чужого.
        await Harness.WaitAsync(() => Count(h.Text(3), "$GPRMC") >= 3, 3000, "DLC3: нет NMEA");
        var lines = h.Text(3).Split(new[] { "\r\n" }, StringSplitOptions.None);
        foreach (var line in lines.Take(lines.Length - 1).Where(l => l.Length > 0))
            Assert.True(IsValidNmea(line), $"DLC3: чужие или битые данные: «{line}»");

        // Протокол: без шума — ни одной ошибки разбора.
        if (profile.Quirks.NoiseProbability == 0)
            Assert.Equal(0, h.Session.Errors);
        Assert.Equal(0, h.Emulator.FrameErrors);

        // Закрытие: модем вернулся в AT-режим (CLD или, если не поддерживает, DISC DLC0).
        await h.Session.StopAsync();
        Assert.Equal(MuxSessionState.Stopped, h.Session.State);
        Assert.False(h.Emulator.InMuxMode);
    }

    [Fact]
    public async Task WithoutMscTelitLikeModemStaysSilent()
    {
        // Показывает, зачем терминал шлёт MSC после открытия канала.
        var quirks = new ModemQuirks { SendMscOnOpen = false, RequireMscBeforeData = true };
        await using var h = new Harness(quirks, new MuxSessionOptions { Channels = new[] { 1 }, SendMscOnOpen = false });
        await h.Session.StartAsync();
        await h.SendAsync(1, "ATI\r");
        await Task.Delay(400);
        Assert.Equal("", h.Text(1));
    }

    [Fact]
    public async Task Sim800RejectsFourthChannel()
    {
        await using var h = new Harness(new ModemQuirks { MaxDlci = 3 }, new MuxSessionOptions { Channels = new[] { 1, 2, 3, 4 } });
        await h.Session.StartAsync();
        Assert.Equal(MuxSessionState.Running, h.Session.State);
        Assert.Equal(ChannelState.Open, h.Session.GetChannelState(3));
        Assert.Equal(ChannelState.Failed, h.Session.GetChannelState(4));
        Assert.Contains(h.Log, m => m.Contains("DLC4") && m.Contains("DM"));
    }

    private static int Count(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static bool IsValidNmea(string line)
    {
        if (!line.StartsWith("$GP", StringComparison.Ordinal))
            return false;
        int star = line.LastIndexOf('*');
        if (star < 0 || star + 3 != line.Length)
            return false;
        byte cs = 0;
        foreach (char c in line.Substring(1, star - 1))
            cs ^= (byte)c;
        return cs == byte.Parse(line.Substring(star + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

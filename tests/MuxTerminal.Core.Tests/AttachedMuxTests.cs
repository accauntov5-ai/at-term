using System.Text;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Tests;

/// <summary>
/// Галочка «Модем уже в MUX»: мультиплексор и каналы включены до нас. Терминал ничего не отправляет от себя
/// (ни AT+CMUX, ни SABM/MSC/DISC/CLD) — только раскладывает кадры по каналам и шлёт данные в UIH.
/// </summary>
public class AttachedMuxTests
{
    private static MuxSessionOptions Attached(params int[] channels) => new()
    {
        SkipCmuxCommand = true,
        Channels = channels,
        ResponseTimeout = TimeSpan.FromMilliseconds(300),
    };

    /// <summary>Модем с открытыми каналами, который, как многие реальные, молчит на повторный SABM.</summary>
    private static Harness AttachedModem(params int[] openChannels)
    {
        var h = new Harness(new ModemQuirks { IgnoreSabm = new HashSet<int>(Enumerable.Range(0, 62)) }, Attached(1, 2));
        h.Emulator.NmeaDlci = 0;
        h.Emulator.EnterMuxDirectly(openChannels);
        return h;
    }

    private static void AssertTerminalSentOnlyData(ModemEmulator emulator)
    {
        var service = emulator.ReceivedFrames.Where(f => f.Type != FrameType.UIH || f.Dlci == 0).ToList();
        Assert.True(service.Count == 0, "терминал отправил служебные кадры: " + string.Join(", ", service.Select(f => $"{f.Type} DLC{f.Dlci}")));
    }

    [Fact]
    public async Task StartSendsNothingAndChannelsWork()
    {
        await using var h = AttachedModem(1, 2);
        await h.Session.StartAsync().WithTimeout(TimeSpan.FromSeconds(2));
        Assert.Equal(MuxSessionState.Running, h.Session.State);
        Assert.Equal(ChannelState.Open, h.Session.GetChannelState(1));
        Assert.Equal(ChannelState.Open, h.Session.GetChannelState(2));

        await h.Session.SendDataAsync(1, Encoding.ASCII.GetBytes("ATI\r"));
        await h.Session.SendDataAsync(2, Encoding.ASCII.GetBytes("AT+CSQ\r"));
        await Harness.WaitAsync(() => h.Text(1).Contains("MUX-EMU"), 2000, "нет ответа в DLC1: " + h.Text(1));
        await Harness.WaitAsync(() => h.Text(2).Contains("+CSQ"), 2000, "нет ответа в DLC2: " + h.Text(2));
        Assert.DoesNotContain("MUX-EMU", h.Text(2));

        Assert.True(h.Emulator.InMuxMode);
        AssertTerminalSentOnlyData(h.Emulator);
    }

    [Fact]
    public async Task StartFromOpenPortSendsNothing()
    {
        await using var h = AttachedModem(1, 2);
        await h.Session.OpenAsync();
        await h.Session.StartAsync(Attached(1, 2)).WithTimeout(TimeSpan.FromSeconds(2));
        Assert.Equal(MuxSessionState.Running, h.Session.State);
        await h.Session.SendDataAsync(1, Encoding.ASCII.GetBytes("AT\r"));
        await Harness.WaitAsync(() => h.Text(1).Contains("OK"), 2000, "нет ответа в DLC1");
        AssertTerminalSentOnlyData(h.Emulator);
    }

    [Fact]
    public async Task DataOnUnlistedChannelIsRoutedAndChannelBecomesUsable()
    {
        await using var h = AttachedModem(1, 2, 5);
        await h.Session.StartAsync();
        await h.Emulator.SendToChannelAsync(5, Encoding.ASCII.GetBytes("+CMTI: \"SM\",1\r\n"));
        await Harness.WaitAsync(() => h.Text(5).Contains("+CMTI"), 2000, "нет данных DLC5");
        Assert.Equal(ChannelState.Open, h.Session.GetChannelState(5));
        await h.Session.SendDataAsync(5, Encoding.ASCII.GetBytes("AT\r"));
        await Harness.WaitAsync(() => h.Text(5).Contains("OK"), 2000, "нет ответа в DLC5");
        AssertTerminalSentOnlyData(h.Emulator);
    }

    [Fact]
    public async Task OpenAndCloseChannelDoNotTouchModem()
    {
        await using var h = AttachedModem(1, 2, 7);
        await h.Session.StartAsync();
        Assert.True(await h.Session.OpenChannelAsync(7));
        await h.Session.SendDataAsync(7, Encoding.ASCII.GetBytes("AT\r"));
        await Harness.WaitAsync(() => h.Text(7).Contains("OK"), 2000, "нет ответа в DLC7");
        await h.Session.CloseChannelAsync(7);
        Assert.Equal(ChannelState.Closed, h.Session.GetChannelState(7));
        Assert.Contains(7, h.Emulator.OpenChannels);
        AssertTerminalSentOnlyData(h.Emulator);
    }

    [Fact]
    public async Task ModemCommandsAreStillAnswered()
    {
        await using var h = AttachedModem(1, 2);
        await h.Session.StartAsync();
        await h.Emulator.SendControlAsync(new ControlMessage(ControlMessageType.Test, true, Encoding.ASCII.GetBytes("PING")));
        await Harness.WaitAsync(() => h.Emulator.ReceivedControl.Any(m => m.Type == ControlMessageType.Test && !m.IsCommand), 2000, "нет ответа на Test");
    }

    [Fact]
    public async Task StopMuxDetachesWithoutClosingModemMux()
    {
        await using var h = AttachedModem(1, 2);
        await h.Session.OpenAsync();
        await h.Session.StartAsync(Attached(1, 2));
        await h.Session.StopMuxAsync();
        Assert.Equal(MuxSessionState.PortOpen, h.Session.State);
        Assert.True(h.Emulator.InMuxMode);
        Assert.Equal(new[] { 1, 2 }, h.Emulator.OpenChannels);
        AssertTerminalSentOnlyData(h.Emulator);

        // Повторное подключение к тому же MUX.
        await h.Session.StartAsync(Attached(1, 2));
        h.Clear(1);
        await h.Session.SendDataAsync(1, Encoding.ASCII.GetBytes("AT\r"));
        await Harness.WaitAsync(() => h.Text(1).Contains("OK"), 2000, "нет ответа после переподключения");
    }

    [Fact]
    public async Task StopDoesNotCloseModemMux()
    {
        await using var h = AttachedModem(1, 2);
        await h.Session.StartAsync();
        await h.Session.StopAsync();
        Assert.Equal(MuxSessionState.Stopped, h.Session.State);
        Assert.True(h.Emulator.InMuxMode);
        AssertTerminalSentOnlyData(h.Emulator);
    }

    [Fact]
    public async Task ModemClosingMuxIsStillHandled()
    {
        await using var h = AttachedModem(1, 2);
        await h.Session.OpenAsync();
        await h.Session.StartAsync(Attached(1, 2));
        await h.Emulator.SendControlAsync(ControlMessage.CloseDown());
        await Harness.WaitAsync(() => h.Session.State == MuxSessionState.PortOpen, 2000, "не вернулись в AT-режим");
    }
}

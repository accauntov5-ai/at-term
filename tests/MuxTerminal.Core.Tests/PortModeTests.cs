using System.Text;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Tests;

/// <summary>Порт открыт без мультиплексора: обычный AT-терминал, запуск и остановка MUX без закрытия порта.</summary>
public class PortModeTests
{
    private sealed class PortHarness : IAsyncDisposable
    {
        private readonly StringBuilder _raw = new();

        public PortHarness(ModemQuirks? quirks = null)
        {
            Transport = new EmulatorTransport(quirks);
            Session = new MuxSession(Transport.Stream, Options());
            Session.RawTraffic += (dir, data) =>
            {
                if (dir == TrafficDirection.Rx)
                    lock (_raw)
                        _raw.Append(Compat.Latin1.GetString(data));
            };
        }

        public EmulatorTransport Transport { get; }
        public MuxSession Session { get; }

        public static MuxSessionOptions Options(params int[] channels) => new()
        {
            Channels = channels.Length > 0 ? channels : new[] { 1 },
            SwitchDelay = TimeSpan.Zero,
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            AtTimeout = TimeSpan.FromMilliseconds(800),
        };

        public string Raw
        {
            get
            {
                lock (_raw)
                    return _raw.ToString();
            }
        }

        public void ClearRaw()
        {
            lock (_raw)
                _raw.Clear();
        }

        public async Task<string> RawCommandAsync(string command)
        {
            ClearRaw();
            await Session.SendRawAsync(Encoding.ASCII.GetBytes(command + "\r"));
            await Harness.WaitAsync(() => Raw.Contains("OK") || Raw.Contains("ERROR"), 2000, $"нет ответа на {command}: «{Raw}»");
            return Raw;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpenPortWorksAsPlainTerminal()
    {
        await using var h = new PortHarness();
        await h.Session.OpenAsync();
        Assert.Equal(MuxSessionState.PortOpen, h.Session.State);
        Assert.Contains("MUX-EMU", await h.RawCommandAsync("ATI"));
        Assert.Contains("+CPIN: READY", await h.RawCommandAsync("AT+CPIN?"));
        Assert.False(h.Transport.Emulator.InMuxMode);
    }

    [Fact]
    public async Task StartAndStopMuxKeepPortOpen()
    {
        await using var h = new PortHarness();
        await h.Session.OpenAsync();
        await h.RawCommandAsync("AT");

        for (int round = 1; round <= 2; round++)
        {
            await h.Session.StartAsync(PortHarness.Options(1, 2));
            Assert.Equal(MuxSessionState.Running, h.Session.State);
            Assert.Equal(ChannelState.Open, h.Session.GetChannelState(2));
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Session.SendRawAsync(Encoding.ASCII.GetBytes("AT\r")));

            await h.Session.StopMuxAsync();
            Assert.Equal(MuxSessionState.PortOpen, h.Session.State);
            Assert.Equal(ChannelState.Closed, h.Session.GetChannelState(1));
            Assert.False(h.Transport.Emulator.InMuxMode);
            // Порт жив: модем снова отвечает на обычные AT-команды.
            Assert.Contains("MUX-EMU", await h.RawCommandAsync("ATI"));
        }

        await h.Session.StopAsync();
        Assert.Equal(MuxSessionState.Stopped, h.Session.State);
    }

    [Fact]
    public async Task CmuxErrorReturnsToOpenPort()
    {
        await using var h = new PortHarness(new ModemQuirks { CmuxResponse = "ERROR" });
        await h.Session.OpenAsync();
        await Assert.ThrowsAsync<MuxException>(() => h.Session.StartAsync(PortHarness.Options()));
        Assert.Equal(MuxSessionState.PortOpen, h.Session.State);
        Assert.Contains("OK", await h.RawCommandAsync("AT"));
    }

    [Fact]
    public async Task FailureAfterCmuxClosesMuxAndReturnsToOpenPort()
    {
        // Модем перешёл в MUX, но не отвечает на SABM DLC0: терминал обязан вывести его из MUX.
        await using var h = new PortHarness(new ModemQuirks { IgnoreSabm = new HashSet<int> { 0 } });
        await h.Session.OpenAsync();
        await Assert.ThrowsAsync<MuxException>(() => h.Session.StartAsync(PortHarness.Options()));
        Assert.Equal(MuxSessionState.PortOpen, h.Session.State);
        await Harness.WaitAsync(() => !h.Transport.Emulator.InMuxMode, 2000, "модем остался в MUX");
        Assert.Contains("OK", await h.RawCommandAsync("AT"));
    }

    [Fact]
    public async Task ModemClosingMuxLeavesPortOpen()
    {
        await using var h = new PortHarness();
        await h.Session.OpenAsync();
        await h.Session.StartAsync(PortHarness.Options());
        await h.Transport.Emulator.SendControlAsync(MuxTerminal.Core.Protocol.ControlMessage.CloseDown());
        await Harness.WaitAsync(() => h.Session.State == MuxSessionState.PortOpen, 2000, "не вернулись в AT-режим");
        Assert.Contains("OK", await h.RawCommandAsync("AT"));
    }

    [Fact]
    public async Task StopMuxDuringStartCancelsToOpenPort()
    {
        // Модем молчит на AT и CMUX — запуск долгий; «Стоп MUX» прерывает его, порт остаётся открытым.
        await using var h = new PortHarness(new ModemQuirks { CmuxResponse = "" });
        await h.Session.OpenAsync();
        var start = h.Session.StartAsync(PortHarness.Options());
        await Task.Delay(100);
        await h.Session.StopMuxAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => start);
        Assert.Equal(MuxSessionState.PortOpen, h.Session.State);
    }

    [Fact]
    public async Task RawSendWhenPortClosedFails()
    {
        await using var h = new PortHarness();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Session.SendRawAsync(new byte[] { 0x41 }));
    }
}

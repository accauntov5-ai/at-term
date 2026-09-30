using System.Text;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Tests;

/// <summary>Поведение терминала как стороны протокола 27.010: кадры, канал управления, действия модема, ошибки.</summary>
public class ProtocolConformanceTests
{
    private static async Task<Harness> StartAsync(ModemQuirks? quirks = null, MuxSessionOptions? options = null, params int[] channels)
    {
        options ??= new MuxSessionOptions();
        options.Channels = channels.Length > 0 ? channels : new[] { 1, 2 };
        var h = new Harness(quirks, options);
        await h.Session.StartAsync();
        return h;
    }

    // ───────────── Кадры терминала ─────────────

    [Fact]
    public async Task TerminalFramesUseInitiatorBits()
    {
        await using var h = await StartAsync();
        await h.SendAsync(1, "AT\r");
        await h.WaitTextAsync(1, "OK");
        var frames = h.Emulator.ReceivedFrames;

        // SABM — команда инициатора: C/R=1, P=1; первым открывается DLC0.
        var sabms = frames.Where(f => f.Type == FrameType.SABM).ToList();
        Assert.Equal(0, sabms[0].Dlci);
        Assert.All(sabms, f => Assert.True(f.CommandResponse && f.PollFinal));
        Assert.Equal(new[] { 0, 1, 2 }, sabms.Select(f => f.Dlci));

        // Данные — UIH с C/R=1, P=0.
        var data = frames.Where(f => f.Type == FrameType.UIH && f.Dlci == 1).ToList();
        Assert.NotEmpty(data);
        Assert.All(data, f => Assert.True(f.CommandResponse && !f.PollFinal));

        // После открытия каждого канала — MSC с RTC и RTR.
        var msc = h.Emulator.ReceivedControl.Where(m => m.Type == ControlMessageType.MSC && m.IsCommand).ToList();
        foreach (var dlci in new[] { 1, 2 })
        {
            var m = Assert.Single(msc, x => x.Value[0] >> 2 == dlci);
            var signals = (ModemSignals)m.Value[1];
            Assert.True(signals.HasFlag(ModemSignals.RTC) && signals.HasFlag(ModemSignals.RTR));
            Assert.False(signals.HasFlag(ModemSignals.FC));
        }
        // На MSC модема терминал отвечает MSC-ответом.
        Assert.Contains(h.Emulator.ReceivedControl, m => m.Type == ControlMessageType.MSC && !m.IsCommand);
    }

    [Fact]
    public async Task LongDataIsSplitByN1AndReassembledInOrder()
    {
        await using var h = await StartAsync(options: new MuxSessionOptions { CmuxCommand = "AT+CMUX=0,0,5,64" });
        var data = Enumerable.Range(0, 10_000).Select(i => (byte)(i * 31)).ToArray();
        await h.Session.SendDataAsync(2, data);
        await Harness.WaitAsync(() => h.Emulator.ReceivedFrames.Where(f => f.Dlci == 2 && f.Type == FrameType.UIH).Sum(f => f.Payload.Length) >= data.Length,
            3000, "не все данные дошли");
        var frames = h.Emulator.ReceivedFrames.Where(f => f.Dlci == 2 && f.Type == FrameType.UIH).ToList();
        Assert.All(frames, f => Assert.True(f.Payload.Length <= 64));
        Assert.Equal(data, frames.SelectMany(f => f.Payload).ToArray());
    }

    /// <summary>Четыре канала шлют одновременно: данные каждого приходят целиком и в своём порядке.</summary>
    [Fact]
    public async Task ConcurrentChannelsKeepPerChannelOrder()
    {
        await using var h = await StartAsync(new ModemQuirks { MaxChunk = 13 }, new MuxSessionOptions { CmuxCommand = "AT+CMUX=0,0,5,40" }, 1, 2, 3, 4);
        var expected = new Dictionary<int, string>();
        var tasks = new[] { 1, 2, 3, 4 }.Select(dlci => Task.Run(async () =>
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 100; i++)
            {
                var line = $"<{dlci}:{i:D3}:{new string((char)('a' + dlci), i % 50)}>";
                sb.Append(line);
                await h.SendAsync(dlci, line);
            }
            lock (expected)
                expected[dlci] = sb.ToString();
        })).ToArray();
        await Task.WhenAll(tasks);

        foreach (var dlci in new[] { 1, 2, 3, 4 })
        {
            string Received() => Compat.Latin1.GetString(h.Emulator.ReceivedFrames
                .Where(f => f.Dlci == dlci && f.Type == FrameType.UIH).SelectMany(f => f.Payload).ToArray());
            await Harness.WaitAsync(() => Received().Length >= expected[dlci].Length, 3000, $"DLC{dlci}: не всё дошло");
            Assert.Equal(expected[dlci], Received());
        }
        Assert.Equal(0, h.Emulator.FrameErrors);
    }

    // ───────────── Действия модема ─────────────

    [Fact]
    public async Task ModemOpensChannelItself()
    {
        await using var h = await StartAsync();
        await h.Emulator.SendFrameToTerminalAsync(5, FrameType.SABM, false, true, Array.Empty<byte>());
        await Harness.WaitAsync(() => h.Session.GetChannelState(5) == ChannelState.Open, 2000, "DLC5 не открыт");
        // Ответ инициатора на команду модема: UA с C/R=0, F=1.
        await Harness.WaitAsync(() => h.Emulator.ReceivedFrames.Any(f => f.Dlci == 5 && f.Type == FrameType.UA), 2000, "нет UA");
        var ua = h.Emulator.ReceivedFrames.First(f => f.Dlci == 5 && f.Type == FrameType.UA);
        Assert.False(ua.CommandResponse);
        Assert.True(ua.PollFinal);

        await h.Emulator.SendToChannelAsync(5, Encoding.ASCII.GetBytes("+URC: hello\r\n"));
        await h.WaitTextAsync(5, "+URC: hello");
    }

    [Fact]
    public async Task ModemClosesChannel()
    {
        await using var h = await StartAsync();
        await h.Emulator.SendFrameToTerminalAsync(2, FrameType.DISC, false, true, Array.Empty<byte>());
        await Harness.WaitAsync(() => h.Session.GetChannelState(2) == ChannelState.Closed, 2000, "DLC2 не закрыт");
        await Harness.WaitAsync(() => h.Emulator.ReceivedFrames.Any(f => f.Dlci == 2 && f.Type == FrameType.UA), 2000, "нет UA на DISC");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.SendAsync(2, "AT\r"));
        // Остальные каналы работают.
        await h.SendAsync(1, "AT\r");
        await h.WaitTextAsync(1, "OK");
    }

    [Fact]
    public async Task ModemClosesMultiplexerWithCld()
    {
        await using var h = await StartAsync();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.StateChanged += s => { if (s == MuxSessionState.Stopped) stopped.TrySetResult(true); };
        await h.Emulator.SendControlAsync(ControlMessage.CloseDown());
        Assert.True(await stopped.Task.WithTimeout(TimeSpan.FromSeconds(3)));
        Assert.Contains(h.Emulator.ReceivedControl, m => m.Type == ControlMessageType.CLD && !m.IsCommand);
        Assert.Equal(ChannelState.Closed, h.Session.GetChannelState(1));
    }

    [Fact]
    public async Task ModemDisconnectsControlChannel()
    {
        await using var h = await StartAsync();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.StateChanged += s => { if (s == MuxSessionState.Stopped) stopped.TrySetResult(true); };
        await h.Emulator.SendFrameToTerminalAsync(0, FrameType.DISC, false, true, Array.Empty<byte>());
        Assert.True(await stopped.Task.WithTimeout(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(ControlMessageType.Test)]
    [InlineData(ControlMessageType.PN)]
    [InlineData(ControlMessageType.RPN)]
    [InlineData(ControlMessageType.RLS)]
    public async Task ControlCommandsAreAnswered(ControlMessageType type)
    {
        await using var h = await StartAsync();
        var value = new byte[] { 0x07, 0x00, 0x01, 0x7F, 0x00 };
        await h.Emulator.SendControlAsync(new ControlMessage(type, true, value));
        await Harness.WaitAsync(() => h.Emulator.ReceivedControl.Any(m => m.Type == type && !m.IsCommand), 2000, $"нет ответа на {type}");
        Assert.Equal(value, h.Emulator.ReceivedControl.First(m => m.Type == type && !m.IsCommand).Value);
    }

    [Fact]
    public async Task UnknownControlCommandGetsNsc()
    {
        await using var h = await StartAsync();
        var unknown = new ControlMessage((ControlMessageType)0x34, true, new byte[] { 1 });
        await h.Emulator.SendControlAsync(unknown);
        await Harness.WaitAsync(() => h.Emulator.ReceivedControl.Any(m => m.Type == ControlMessageType.NSC), 2000, "нет NSC");
        var nsc = h.Emulator.ReceivedControl.First(m => m.Type == ControlMessageType.NSC);
        Assert.False(nsc.IsCommand);
        Assert.Equal(unknown.TypeOctet, nsc.Value[0]);
    }

    [Fact]
    public async Task DataForUnopenedChannelIsStillDelivered()
    {
        await using var h = await StartAsync();
        await h.Emulator.SendToChannelAsync(7, Encoding.ASCII.GetBytes("RING\r\n"));
        await h.WaitTextAsync(7, "RING");
        Assert.Contains(h.Log, m => m.Contains("неоткрытого DLC7"));
    }

    // ───────────── Управление потоком ─────────────

    [Fact]
    public async Task GlobalFlowControlPausesAllSending()
    {
        await using var h = await StartAsync();
        await h.Emulator.SendControlAsync(new ControlMessage(ControlMessageType.FCoff, true, Array.Empty<byte>()));
        await Harness.WaitAsync(() => h.Emulator.ReceivedControl.Any(m => m.Type == ControlMessageType.FCoff && !m.IsCommand), 2000, "нет ответа на FCoff");

        int before = h.Emulator.ReceivedFrames.Count(f => f.Dlci == 1 && f.Type == FrameType.UIH);
        var send = h.SendAsync(1, "AT\r");
        await Task.Delay(300);
        Assert.False(send.IsCompleted);
        Assert.Equal(before, h.Emulator.ReceivedFrames.Count(f => f.Dlci == 1 && f.Type == FrameType.UIH));

        await h.Emulator.SendControlAsync(new ControlMessage(ControlMessageType.FCon, true, Array.Empty<byte>()));
        await send.WithTimeout(TimeSpan.FromSeconds(2));
        await h.WaitTextAsync(1, "OK");
    }

    [Fact]
    public async Task ChannelFlowControlPausesOnlyThatChannel()
    {
        await using var h = await StartAsync();
        // MSC от модема с FC=1 для DLC1: модем не может принимать данные этого канала.
        await h.Emulator.SendControlAsync(ControlMessage.Msc(1, ModemSignals.FC | ModemSignals.RTC | ModemSignals.RTR));
        await Harness.WaitAsync(() => h.Log.Any(m => m.Contains("DLC1: flow control включён")), 2000, "FC не принят");

        var blocked = h.SendAsync(1, "AT\r");
        await h.SendAsync(2, "AT\r");               // другой канал не блокируется
        await h.WaitTextAsync(2, "OK");
        Assert.False(blocked.IsCompleted);

        await h.Emulator.SendControlAsync(ControlMessage.Msc(1, ModemSignals.RTC | ModemSignals.RTR));
        await blocked.WithTimeout(TimeSpan.FromSeconds(2));
        await h.WaitTextAsync(1, "OK");
    }

    // ───────────── Открытие каналов и остановка ─────────────

    [Fact]
    public async Task NoUaIsRetriedN2TimesThenFails()
    {
        var quirks = new ModemQuirks { IgnoreSabm = new HashSet<int> { 2 } };
        var options = new MuxSessionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150), Retries = 3 };
        await using var h = await StartAsync(quirks, options, 1, 2);
        Assert.Equal(MuxSessionState.Running, h.Session.State);
        Assert.Equal(ChannelState.Open, h.Session.GetChannelState(1));
        Assert.Equal(ChannelState.Failed, h.Session.GetChannelState(2));
        Assert.Equal(3, h.Emulator.ReceivedFrames.Count(f => f.Dlci == 2 && f.Type == FrameType.SABM));
    }

    [Fact]
    public async Task NoUaOnControlChannelFailsStart()
    {
        var quirks = new ModemQuirks { IgnoreSabm = new HashSet<int> { 0 } };
        var h = new Harness(quirks, new MuxSessionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(100), Channels = new[] { 1 } });
        await using (h)
        {
            var ex = await Assert.ThrowsAsync<MuxException>(() => h.Session.StartAsync());
            Assert.Contains("DLC0", ex.Message);
            Assert.Equal(MuxSessionState.Faulted, h.Session.State);
        }
    }

    [Fact]
    public async Task StopFallsBackToDiscWhenCldUnsupported()
    {
        var h = await StartAsync(new ModemQuirks { SupportsCld = false }, new MuxSessionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(300) });
        await using (h)
        {
            await h.Session.StopAsync();
            Assert.False(h.Emulator.InMuxMode);
            var frames = h.Emulator.ReceivedFrames;
            Assert.Contains(frames, f => f.Dlci == 0 && f.Type == FrameType.DISC);
            // Каналы данных закрыты раньше управляющего.
            int lastDataDisc = frames.ToList().FindLastIndex(f => f.Dlci > 0 && f.Type == FrameType.DISC);
            int dlc0Disc = frames.ToList().FindIndex(f => f.Dlci == 0 && f.Type == FrameType.DISC);
            Assert.True(lastDataDisc < dlc0Disc);
        }
    }

    [Fact]
    public async Task StopDuringTrafficIsClean()
    {
        var h = await StartAsync(options: null, channels: new[] { 1, 2, 3 });
        await using (h)
        {
            var cts = new CancellationTokenSource();
            var traffic = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try { await h.SendAsync(1, "AT+CSQ\r"); }
                    catch (InvalidOperationException) { break; } // сессия остановлена — ожидаемо
                    await Task.Delay(1);
                }
            });
            await Task.Delay(150);
            await h.Session.StopAsync();
            cts.Cancel();
            await traffic.WithTimeout(TimeSpan.FromSeconds(2));
            Assert.Equal(MuxSessionState.Stopped, h.Session.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.SendAsync(1, "AT\r"));
        }
    }

    // ───────────── AT-фаза ─────────────

    [Theory]
    [InlineData("ERROR")]
    [InlineData("+CME ERROR: 3")]
    [InlineData("+CMS ERROR: 302")]
    public async Task CmuxRejectedByModem(string response)
    {
        var h = new Harness(new ModemQuirks { CmuxResponse = response });
        await using (h)
        {
            var ex = await Assert.ThrowsAsync<MuxException>(() => h.Session.StartAsync());
            Assert.Contains(response, ex.Message);
            Assert.False(h.Emulator.InMuxMode);
        }
    }

    [Fact]
    public async Task OkSplitIntoSingleBytes()
    {
        await using var h = new Harness(new ModemQuirks { MaxChunk = 1 }, new MuxSessionOptions { Channels = new[] { 1 } });
        await h.Session.StartAsync();
        await h.SendAsync(1, "AT\r");
        await h.WaitTextAsync(1, "OK");
        Assert.Equal(0, h.Session.Errors);
    }

    [Fact]
    public async Task FrameInSameChunkAsOkIsProcessed()
    {
        await using var h = new Harness(new ModemQuirks { FrameRightAfterOk = true }, new MuxSessionOptions { Channels = new[] { 1 } });
        await h.Session.StartAsync();
        // Кадр Test, пришедший в одной посылке с OK, разобран и получил ответ.
        await Harness.WaitAsync(() => h.Emulator.ReceivedControl.Any(m => m.Type == ControlMessageType.Test && !m.IsCommand), 2000, "нет ответа на Test");
        Assert.Equal("EMU", Encoding.ASCII.GetString(h.Emulator.ReceivedControl.First(m => m.Type == ControlMessageType.Test).Value));
    }

    [Fact]
    public async Task UrcBeforeOkDoesNotBreakStart()
    {
        await using var h = new Harness(new ModemQuirks { UrcBeforeCmuxOk = "+CREG: 1" }, new MuxSessionOptions { Channels = new[] { 1 } });
        await h.Session.StartAsync();
        Assert.Equal(MuxSessionState.Running, h.Session.State);
    }

    [Theory]
    [InlineData("AT\r\r\nOK\r\n", "OK")]
    [InlineData("\r\n+CREG: 1\r\n\r\nOK\r\n", "OK")]
    [InlineData("\r\nERROR\r\n", "ERROR")]
    [InlineData("\r\n+CME ERROR: 10\r\n", "+CME ERROR: 10")]
    [InlineData("OK\r", "OK")]
    [InlineData("\r\nNO CARRIER\r\n", "NO CARRIER")]
    public void FinalResultDetection(string input, string expected)
    {
        var buf = Encoding.ASCII.GetBytes(input).ToList();
        Assert.True(MuxSession.TryFindFinalResult(buf, out var result, out _));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\r\n+CREG: 1\r\n")]
    [InlineData("\r\nO")]
    [InlineData("AT+CMUX=0\r")]
    public void IncompleteOrIntermediateIsNotFinal(string input)
        => Assert.False(MuxSession.TryFindFinalResult(Encoding.ASCII.GetBytes(input).ToList(), out _, out _));
}

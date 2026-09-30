using System.Collections.Concurrent;
using System.Text;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Transport;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Tests;

public class SessionTests
{
    private static async Task<(EmulatorTransport Transport, MuxSession Session, ConcurrentDictionary<int, StringBuilder> Received)>
        StartAsync(string cmux = "AT+CMUX=0", params int[] channels)
    {
        var transport = new EmulatorTransport();
        transport.Emulator.NmeaInterval = TimeSpan.FromMilliseconds(100);
        var session = new MuxSession(transport.Stream, new MuxSessionOptions
        {
            CmuxCommand = cmux,
            Channels = channels.Length == 0 ? new[] { 1, 2, 3 } : channels,
            SwitchDelay = TimeSpan.Zero,
        });
        var received = new ConcurrentDictionary<int, StringBuilder>();
        session.DataReceived += (dlci, data) =>
        {
            var sb = received.GetOrAdd(dlci, _ => new StringBuilder());
            lock (sb) sb.Append(Compat.Latin1.GetString(data));
        };
        await session.StartAsync();
        return (transport, session, received);
    }

    private static async Task<string> WaitForAsync(ConcurrentDictionary<int, StringBuilder> received, int dlci, string text, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (received.TryGetValue(dlci, out var sb))
            {
                lock (sb)
                {
                    var s = sb.ToString();
                    if (s.Contains(text))
                        return s;
                }
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"DLC{dlci}: не дождались \"{text}\"");
    }

    [Fact]
    public async Task StartsMuxAndOpensChannels()
    {
        var (transport, session, _) = await StartAsync();
        await using (transport)
        {
            Assert.Equal(MuxSessionState.Running, session.State);
            Assert.True(transport.Emulator.InMuxMode);
            Assert.Equal(new[] { 1, 2, 3 }, transport.Emulator.OpenChannels);
            foreach (var dlci in new[] { 1, 2, 3 })
                Assert.Equal(ChannelState.Open, session.GetChannelState(dlci));
            Assert.Equal(0, transport.Emulator.FrameErrors);
        }
    }

    [Fact]
    public async Task RoutesDataPerChannel()
    {
        var (transport, session, received) = await StartAsync();
        await using (transport)
        {
            await session.SendDataAsync(1, "ATI\r"u8.ToArray());
            await session.SendDataAsync(2, "AT+CSQ\r"u8.ToArray());
            var ch1 = await WaitForAsync(received, 1, "OK");
            var ch2 = await WaitForAsync(received, 2, "OK");
            Assert.Contains("MUX-EMU", ch1);
            Assert.DoesNotContain("+CSQ", ch1);
            Assert.Contains("+CSQ", ch2);
            await WaitForAsync(received, 3, "$GPGGA");
        }
    }

    [Fact]
    public async Task SplitsLongDataByN1()
    {
        var (transport, session, received) = await StartAsync();
        await using (transport)
        {
            var commands = new List<string>();
            transport.Emulator.CommandReceived += (d, c) => { lock (commands) commands.Add(c); };
            var longCmd = "AT+CMGS=\"" + new string('1', 100) + "\"";
            long before = session.TxFrames;
            await session.SendDataAsync(2, Encoding.ASCII.GetBytes(longCmd + "\r"));
            await WaitForAsync(received, 2, "> ");
            Assert.True(session.TxFrames - before >= 4, "ожидалась нарезка на кадры по 31 байт");
            lock (commands) Assert.Contains(longCmd, commands);

            await session.SendDataAsync(2, "Hello\x1A"u8.ToArray());
            await WaitForAsync(received, 2, "+CMGS:");
        }
    }

    [Fact]
    public async Task BinaryDataWithFlagBytesIsDelivered()
    {
        var transport = new EmulatorTransport();
        await using (transport)
        {
            var session = new MuxSession(transport.Stream, new MuxSessionOptions { Channels = new[] { 2 }, SwitchDelay = TimeSpan.Zero });
            var bytes = new List<byte>();
            session.DataReceived += (_, d) => { lock (bytes) bytes.AddRange(d); };
            await session.StartAsync();
            await session.SendDataAsync(2, "ATE0\r"u8.ToArray());
            await Task.Delay(100);
            lock (bytes) bytes.Clear();
            await session.SendDataAsync(2, "AT+BINTEST\r"u8.ToArray());
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (bytes) if (bytes.Count >= 256 + 6) break;
                await Task.Delay(10);
            }
            lock (bytes)
                Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), bytes.Take(256));
            Assert.Equal(0, session.Errors);
        }
    }

    [Fact]
    public async Task ConcurrentSendsDoNotCorruptFrames()
    {
        var (transport, session, received) = await StartAsync("AT+CMUX=0,0,5,64", 1, 2);
        await using (transport)
        {
            var tasks = Enumerable.Range(0, 50).SelectMany(i => new[]
            {
                Task.Run(() => session.SendDataAsync(1, Encoding.ASCII.GetBytes($"AT+CGMI\r"))),
                Task.Run(() => session.SendDataAsync(2, Encoding.ASCII.GetBytes($"AT+CGSN\r"))),
            });
            await Task.WhenAll(tasks);
            await Task.Delay(200);
            Assert.Equal(0, transport.Emulator.FrameErrors);
            Assert.True(transport.Emulator.FramesReceived >= 100);
            await WaitForAsync(received, 1, "MUX-EMU");
            await WaitForAsync(received, 2, "867000000000001");
        }
    }

    [Fact]
    public async Task StopClosesMuxAndReturnsModemToAtMode()
    {
        var (transport, session, _) = await StartAsync();
        await using (transport)
        {
            var closed = new List<int>();
            session.ChannelStateChanged += (d, s) => { if (s == ChannelState.Closed) lock (closed) closed.Add(d); };
            await session.StopAsync();
            Assert.Equal(MuxSessionState.Stopped, session.State);
            Assert.False(transport.Emulator.InMuxMode);
            lock (closed) Assert.Contains(1, closed);
        }
    }

    [Fact]
    public async Task ChannelCanBeReopened()
    {
        var (transport, session, received) = await StartAsync("AT+CMUX=0", 1);
        await using (transport)
        {
            await session.CloseChannelAsync(1);
            Assert.Equal(ChannelState.Closed, session.GetChannelState(1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendDataAsync(1, "AT\r"u8.ToArray()));
            Assert.True(await session.OpenChannelAsync(1));
            await session.SendDataAsync(1, "AT\r"u8.ToArray());
            await WaitForAsync(received, 1, "OK");
        }
    }

    [Fact]
    public async Task AdvancedOptionIsRejected()
    {
        var (a, _) = InMemoryDuplexStream.CreatePair();
        var session = new MuxSession(a, new MuxSessionOptions { CmuxCommand = "AT+CMUX=1" });
        await Assert.ThrowsAsync<NotSupportedException>(() => session.StartAsync());
    }

    [Fact]
    public async Task FailsWhenModemIsSilent()
    {
        var (a, b) = InMemoryDuplexStream.CreatePair();
        using var _ = b;
        var session = new MuxSession(a, new MuxSessionOptions
        {
            SendAtProbe = false,
            AtTimeout = TimeSpan.FromMilliseconds(200),
        });
        var ex = await Assert.ThrowsAsync<MuxException>(() => session.StartAsync());
        Assert.Contains("AT+CMUX", ex.Message);
        Assert.Equal(MuxSessionState.Faulted, session.State);
    }

    /// <summary>Программа «упала», модем остался в MUX: новая сессия должна закрыть старый MUX и запуститься.</summary>
    [Fact]
    public async Task RecoversModemStuckInMux()
    {
        var (client, modem) = InMemoryDuplexStream.CreatePair();
        var emulator = new ModemEmulator(modem);
        emulator.Start();
        await using (emulator)
        {
            // Предыдущий сеанс: AT+CMUX и SABM DLC0, затем «падение» без CLD.
            var cmux = Encoding.ASCII.GetBytes("AT+CMUX=0\r");
            await client.WriteAsync(cmux, 0, cmux.Length);
            await DrainAsync(client, 300);
            var sabm = FrameEncoder.Sabm(0);
            await client.WriteAsync(sabm, 0, sabm.Length);
            await DrainAsync(client, 300);
            Assert.True(emulator.InMuxMode);

            var session = new MuxSession(client, new MuxSessionOptions
            {
                Channels = new[] { 1 },
                SwitchDelay = TimeSpan.Zero,
                RecoveryDelay = TimeSpan.FromMilliseconds(100),
            });
            var log = new List<string>();
            session.Log += (_, m) => { lock (log) log.Add(m); };
            await session.StartAsync();
            Assert.Equal(MuxSessionState.Running, session.State);
            lock (log) Assert.Contains(log, m => m.Contains("вернулся в AT-режим"));
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task ModemRebootFaultsSession()
    {
        var (transport, session, _) = await StartAsync("AT+CMUX=0", 1);
        await using (transport)
        {
            var faulted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.StateChanged += s => { if (s == MuxSessionState.Faulted) faulted.TrySetResult(true); };
            await session.SendDataAsync(1, "AT+CFUN=1,1\r"u8.ToArray());
            Assert.True(await faulted.Task.WithTimeout(TimeSpan.FromSeconds(3)));
            Assert.Equal(ChannelState.Closed, session.GetChannelState(1));
        }
    }

    [Fact]
    public async Task OnMuxEnteredHookRunsBeforeSabm()
    {
        var transport = new EmulatorTransport();
        await using (transport)
        {
            bool called = false;
            var session = new MuxSession(transport.Stream, new MuxSessionOptions
            {
                Channels = new[] { 1 },
                SwitchDelay = TimeSpan.Zero,
                OnMuxEntered = _ => { called = transport.Emulator.OpenChannels.Count == 0; return Task.CompletedTask; },
            });
            await session.StartAsync();
            Assert.True(called);
        }
    }

    private static async Task DrainAsync(Stream stream, int ms)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(ms);
        try
        {
            while (true)
                await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void FindsFinalResultAfterEcho()
    {
        var buf = Encoding.ASCII.GetBytes("AT+CMUX=0\r\r\nOK\r\n\xF9").ToList();
        Assert.True(MuxSession.TryFindFinalResult(buf, out var result, out int end));
        Assert.Equal("OK", result);
        Assert.Equal(buf.Count - 1, end);
    }

    [Theory]
    [InlineData("AT+CMUX=0", 0, 31)]
    [InlineData("AT+CMUX=0,0,5,127,10,3,30,10,2", 0, 127)]
    [InlineData("AT+CMUX=1,0,5,64", 1, 64)]
    public void ParsesCmuxParameters(string cmd, int mode, int n1)
    {
        var p = CmuxParameters.Parse(cmd);
        Assert.Equal(mode, p.Mode);
        Assert.Equal(n1, p.N1);
    }

    [Theory]
    [InlineData("AT+CMUX=0", null)]
    [InlineData("AT+CMUX=0,0,5,127", 115200)]
    [InlineData("AT+CMUX=0,0,6", 230400)]
    [InlineData("AT+CMUX=0,0,1,31", 9600)]
    public void MapsPortSpeed(string cmd, int? baud) => Assert.Equal(baud, CmuxParameters.Parse(cmd).PortBaudRate);
}

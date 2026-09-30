using System.Collections.Concurrent;
using System.Text;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Util;

namespace MuxTerminal.Core.Tests;

/// <summary>Сессия терминала против эмулятора с заданными особенностями модема + сбор данных по каналам.</summary>
internal sealed class Harness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, List<byte>> _data = new();
    private readonly List<string> _log = new();

    public Harness(ModemQuirks? quirks = null, MuxSessionOptions? options = null)
    {
        Transport = new EmulatorTransport(quirks);
        Emulator.NmeaInterval = TimeSpan.FromMilliseconds(30);
        options ??= new MuxSessionOptions();
        options.SwitchDelay = TimeSpan.Zero;
        Session = new MuxSession(Transport.Stream, options);
        Session.DataReceived += (dlci, data) =>
        {
            var list = _data.GetOrAdd(dlci, _ => new List<byte>());
            lock (list)
                list.AddRange(data);
        };
        Session.Log += (_, m) => { lock (_log) _log.Add(m); };
    }

    public EmulatorTransport Transport { get; }
    public ModemEmulator Emulator => Transport.Emulator;
    public MuxSession Session { get; }

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
                return _log.ToArray();
        }
    }

    public byte[] Bytes(int dlci)
    {
        if (!_data.TryGetValue(dlci, out var list))
            return Array.Empty<byte>();
        lock (list)
            return list.ToArray();
    }

    public string Text(int dlci) => Compat.Latin1.GetString(Bytes(dlci));

    public void Clear(int dlci)
    {
        if (_data.TryGetValue(dlci, out var list))
            lock (list)
                list.Clear();
    }

    public Task SendAsync(int dlci, string text) => Session.SendDataAsync(dlci, Encoding.ASCII.GetBytes(text));

    public async Task<string> WaitTextAsync(int dlci, string expected, int timeoutMs = 3000)
    {
        await WaitAsync(() => Text(dlci).Contains(expected), timeoutMs, $"DLC{dlci}: нет «{expected}». Принято: «{Text(dlci)}»");
        return Text(dlci);
    }

    public static async Task WaitAsync(Func<bool> condition, int timeoutMs, string message)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(message);
            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Session.State == MuxSessionState.Running)
                await Session.StopAsync();
        }
        catch
        {
            // тест уже проверил, что нужно
        }
        await Transport.DisposeAsync();
    }
}

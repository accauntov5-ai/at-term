using System.Text;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Session;

namespace MuxTerminal.Core.Tests;

/// <summary>
/// Модем шлёт кадры длиннее, чем N1 из нашей команды: игнорирует N1 или MUX включали с другими параметрами
/// («Модем уже в MUX»). Принимать их надо — раньше кадры длиннее 127 байт отбрасывались.
/// </summary>
public class LargeFrameTests
{
    [Theory]
    [InlineData(false, 600)]
    [InlineData(true, 600)]
    [InlineData(true, 1500)]
    public async Task FramesLongerThanOurN1AreReceived(bool attached, int modemN1)
    {
        var options = new MuxSessionOptions { Channels = new[] { 1 }, SkipCmuxCommand = attached };
        var quirks = new ModemQuirks { ForceN1 = modemN1 };
        if (attached)
            quirks.IgnoreSabm = new HashSet<int>(Enumerable.Range(0, 62));
        await using var h = new Harness(quirks, options);
        h.Emulator.NmeaDlci = 0;
        if (attached)
            h.Emulator.EnterMuxDirectly(1);
        await h.Session.StartAsync();

        var big = new string('x', modemN1 * 2 + 10);
        await h.Emulator.SendToChannelAsync(1, Encoding.ASCII.GetBytes(big + "\r\n"));
        await Harness.WaitAsync(() => h.Text(1).Contains(big), 2000, "длинные кадры не приняты");
        Assert.Equal(0, h.Session.Errors);
    }
}

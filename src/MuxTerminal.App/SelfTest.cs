using System.IO;
using System.Text;
using MuxTerminal.App.Services;
using MuxTerminal.Core.Emulator;
using MuxTerminal.Core.Session;

namespace MuxTerminal.App;

/// <summary>
/// «MuxTerminal.exe --selftest»: проверка без окна, что exe работает на этой машине — все встроенные
/// библиотеки загружаются (Costura, версии System.Memory и т.п.), и сеанс MUX с эмулятором проходит.
/// Код выхода 0 — успех; подробности в selftest.log рядом с exe (или в %TEMP%).
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var log = new StringBuilder();
        void Log(string line) => log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        int code;
        try
        {
            Log(AppInfo.Diagnostics.Replace("\r\n", "; "));

            // Встроенные библиотеки интерфейса.
            Log("AvalonDock: " + typeof(AvalonDock.DockingManager).Assembly.GetName().Version);
            Log("AvalonEdit: " + typeof(ICSharpCode.AvalonEdit.TextEditor).Assembly.GetName().Version);
            Log("System.Text.Json: " + System.Text.Json.JsonSerializer.Serialize(new AppSettings()).Length + " байт настроек");
            Log("Правил подсветки: " + DisplaySettings.CreateDefault().CompileRules().Count);

            // Сеанс MUX: порт → MUX → данные в двух каналах → стоп MUX → закрытие.
            var transport = new EmulatorTransport();
            var session = new MuxSession(transport.Stream, new MuxSessionOptions { Channels = new[] { 1, 2 }, SwitchDelay = TimeSpan.Zero });
            var received = new StringBuilder();
            session.DataReceived += (dlci, data) =>
            {
                lock (received)
                    received.Append(dlci).Append(':').Append(Encoding.ASCII.GetString(data));
            };
            session.OpenAsync().Wait();
            if (!session.StartAsync().Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("MUX не запустился");
            Log("MUX: " + session.State);
            session.SendDataAsync(1, Encoding.ASCII.GetBytes("ATI\r")).Wait();
            session.SendDataAsync(2, Encoding.ASCII.GetBytes("AT+CSQ\r")).Wait();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                string text;
                lock (received)
                    text = received.ToString();
                if (text.Contains("MUX-EMU") && text.Contains("+CSQ"))
                    break;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Нет ответов в каналах: " + text);
                Thread.Sleep(20);
            }
            session.StopMuxAsync().Wait(TimeSpan.FromSeconds(10));
            Log("После «Стоп MUX»: " + session.State);
            if (session.State != MuxSessionState.PortOpen)
                throw new InvalidOperationException("Ожидался PortOpen");
            session.StopAsync().Wait(TimeSpan.FromSeconds(10));
            transport.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            Log("OK");
            code = 0;
        }
        catch (Exception ex)
        {
            Log("FAIL: " + ex);
            CrashLog.Write(ex, "selftest");
            code = 1;
        }

        foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString(), Encoding.UTF8);
                break;
            }
            catch
            {
                // Папка только для чтения — пробуем следующую.
            }
        }
        return code;
    }
}

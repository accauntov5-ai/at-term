using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using MuxTerminal.Core.Protocol;
using MuxTerminal.Core.Session;
using MuxTerminal.Core.Util;

namespace MuxTerminal.App.Services;

/// <summary>Папки программы в %AppData%\MuxTerminal.</summary>
public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuxTerminal");
    public static string Logs => Path.Combine(Root, "logs");
    public static string CrashLog => Path.Combine(Root, "crash.log");
    public static string Layout => Path.Combine(Root, "layout.xml");
}

/// <summary>
/// Журнал сеанса в файл: по строке на каждую принятую/отправленную строку каналов, системные сообщения,
/// ошибки протокола и (по желанию) сырые кадры. Запись идёт в фоновом потоке и не тормозит приём данных.
/// Один файл на сеанс: logs\session-ГГГГММДД-ччммсс.log.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private const int KeepFiles = 100;
    private const int MaxLineLength = 4096;

    private sealed class LineAssembler
    {
        public readonly Decoder Decoder = Encoding.UTF8.GetDecoder();
        public readonly StringBuilder Line = new();
    }

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 100_000);
    private readonly Dictionary<string, LineAssembler> _assemblers = new();
    private readonly Thread _writer;
    private readonly StreamWriter _file;
    private readonly bool _rawFrames;
    private int _dropped;

    public SessionLogger(string header, bool rawFrames)
    {
        _rawFrames = rawFrames;
        Directory.CreateDirectory(AppPaths.Logs);
        FilePath = Path.Combine(AppPaths.Logs, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        _file.WriteLine(header.TrimEnd());
        _file.WriteLine(new string('-', 78));
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "SessionLogger" };
        _writer.Start();
        Cleanup();
    }

    public string FilePath { get; }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted && !_queue.TryAdd(line))
            Interlocked.Increment(ref _dropped); // диск не успевает — не блокируем приём данных
    }

    public void System(LogLevel level, string message)
    {
        string tag = level switch
        {
            LogLevel.Error => "ERROR",
            LogLevel.Warning => "WARN ",
            LogLevel.Debug => "DEBUG",
            _ => "INFO ",
        };
        Enqueue($"{Now()} SYS  {tag} {message}");
    }

    /// <summary>Данные канала: собираются в строки по \r / \n.</summary>
    public void Data(string source, TrafficDirection direction, byte[] data)
    {
        string key = source + (direction == TrafficDirection.Rx ? " RX" : " TX");
        lock (_assemblers)
        {
            if (!_assemblers.TryGetValue(key, out var a))
                _assemblers[key] = a = new LineAssembler();
            var chars = new char[a.Decoder.GetCharCount(data, 0, data.Length)];
            a.Decoder.GetChars(data, 0, data.Length, chars, 0);
            foreach (char c in chars)
            {
                if (c is '\r' or '\n')
                {
                    FlushLine(key, a);
                }
                else if (a.Line.Length < MaxLineLength)
                {
                    a.Line.Append(c < 0x20 && c != '\t' ? $"<{(int)c:X2}>" : c.ToString());
                }
            }
        }
    }

    private void FlushLine(string key, LineAssembler a)
    {
        if (a.Line.Length == 0)
            return;
        Enqueue($"{Now()} {key,-8} {a.Line}");
        a.Line.Clear();
    }

    public void Frame(TrafficDirection direction, MuxFrame frame)
    {
        if (_rawFrames)
            Enqueue($"{Now()} FRAME {(direction == TrafficDirection.Rx ? "RX" : "TX")} {frame}  [{Hex.Format(frame.Raw)}]");
    }

    public void FrameError(FrameError error)
        => Enqueue($"{Now()} SYS  ERROR {error.Message} ({error.Data.Length} байт): {Hex.Format(error.Data, 64)}");

    private void WriteLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                _file.WriteLine(line);
                if (_queue.Count == 0)
                    _file.Flush(); // пачками, но без потери хвоста при сбое
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SessionLogger");
        }
    }

    public void Dispose()
    {
        lock (_assemblers)
            foreach (var pair in _assemblers)
                FlushLine(pair.Key, pair.Value);
        if (_dropped > 0)
            Enqueue($"{Now()} SYS  WARN  Пропущено строк журнала (диск не успевал): {_dropped}");
        Enqueue($"{Now()} SYS  INFO  Журнал закрыт");
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
        try
        {
            _file.Dispose();
        }
        catch
        {
            // Диск/сеть недоступны — ничего не поделать.
        }
    }

    /// <summary>Храним последние KeepFiles журналов.</summary>
    private static void Cleanup()
    {
        try
        {
            foreach (var old in new DirectoryInfo(AppPaths.Logs).GetFiles("session-*.log")
                         .OrderByDescending(f => f.Name).Skip(KeepFiles))
                old.Delete();
        }
        catch
        {
            // Файл открыт другим процессом — удалим в следующий раз.
        }
    }
}

/// <summary>Журнал необработанных ошибок: %AppData%\MuxTerminal\crash.log.</summary>
public static class CrashLog
{
    private const long MaxSize = 2 * 1024 * 1024;
    private static readonly object Sync = new();

    public static void Write(Exception exception, string source)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var file = new FileInfo(AppPaths.CrashLog);
                if (file.Exists && file.Length > MaxSize)
                {
                    var old = Path.ChangeExtension(AppPaths.CrashLog, ".old.log");
                    File.Delete(old);
                    file.MoveTo(old);
                }
                File.AppendAllText(AppPaths.CrashLog,
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] ====\r\n{AppInfo.Diagnostics}{exception}\r\n\r\n",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Журнал ошибок не должен сам вызывать ошибки.
        }
    }
}

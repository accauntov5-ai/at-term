using System.Text;

namespace MuxTerminal.Core.Util;

/// <summary>Общие помощники, одинаково работающие на .NET Framework 4.8 и .NET 8.</summary>
public static class Compat
{
    /// <summary>ISO-8859-1: байт → символ один к одному.</summary>
    public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    private static readonly Random SharedRandom = new();

    public static int RandomNext(int min, int max)
    {
        lock (SharedRandom)
            return SharedRandom.Next(min, max);
    }

    public static double RandomDouble()
    {
        lock (SharedRandom)
            return SharedRandom.NextDouble();
    }

    /// <summary>Аналог Task.WaitAsync(timeout, ct) из .NET 6+: по таймауту бросает <see cref="TimeoutException"/>.</summary>
    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout, CancellationToken ct = default)
    {
        if (task.IsCompleted)
            return await task.ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, cts.Token);
        var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (finished == task)
        {
            cts.Cancel(); // останавливаем таймер
            return await task.ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException();
    }

    public static async Task WithTimeout(this Task task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        var finished = await Task.WhenAny(task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);
        if (finished != task)
            throw new TimeoutException();
        cts.Cancel();
        await task.ConfigureAwait(false);
    }

    public static string TakeLast(string text, int count)
        => text.Length <= count ? text : text.Substring(text.Length - count);
}

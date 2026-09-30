namespace MuxTerminal.Core.Transport;

/// <summary>Физический канал к модему (COM-порт, эмулятор).</summary>
public interface IMuxTransport : IAsyncDisposable
{
    string Name { get; }
    Stream Stream { get; }
}

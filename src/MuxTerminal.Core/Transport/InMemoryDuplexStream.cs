using System.Threading.Channels;

namespace MuxTerminal.Core.Transport;

/// <summary>
/// Пара связанных потоков в памяти: то, что записано в один, читается из другого.
/// Используется эмулятором модема и тестами вместо реального COM-порта.
/// </summary>
public sealed class InMemoryDuplexStream : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private byte[] _current = Array.Empty<byte>();
    private int _currentOffset;
    private volatile bool _disposed;

    private InMemoryDuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (InMemoryDuplexStream A, InMemoryDuplexStream B) CreatePair()
    {
        var ab = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var ba = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        return (new InMemoryDuplexStream(ba, ab), new InMemoryDuplexStream(ab, ba));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_currentOffset >= _current.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
                return 0;
            if (!_incoming.Reader.TryRead(out var next))
                return 0;
            _current = next;
            _currentOffset = 0;
        }
        int n = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsMemory(_currentOffset, n).CopyTo(buffer);
        _currentOffset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length > 0 && !_outgoing.Writer.TryWrite(buffer.ToArray()))
            throw new IOException("Другая сторона закрыта");
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _outgoing.Writer.TryComplete();
            _incoming.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }
}

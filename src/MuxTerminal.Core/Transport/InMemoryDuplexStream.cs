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

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InMemoryDuplexStream));
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_currentOffset >= _current.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;
            if (!_incoming.Reader.TryRead(out var next))
                return 0;
            _current = next;
            _currentOffset = 0;
        }
        int n = Math.Min(count, _current.Length - _currentOffset);
        Buffer.BlockCopy(_current, _currentOffset, buffer, offset, n);
        _currentOffset += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (count == 0)
            return;
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        if (!_outgoing.Writer.TryWrite(copy))
            throw new IOException("Другая сторона закрыта");
    }

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

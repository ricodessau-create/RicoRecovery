using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.Recovery.Carving;

internal sealed class BufferedReader
{
    private const int BufferBytes = 64 * 1024;

    private readonly IBlockSource source;
    private readonly long limit;
    private byte[] buffer = [];
    private long bufferStart;

    public BufferedReader(IBlockSource source, long position, long limit)
    {
        this.source = source;
        this.limit = Math.Min(limit, source.Length);
        Position = position;
    }

    public long Position { get; set; }

    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (Position < 0 || Position >= limit)
        {
            return -1;
        }

        if (Position < bufferStart || Position >= bufferStart + buffer.Length)
        {
            var length = (int)Math.Min(BufferBytes, limit - Position);
            var data = await SourceReader.ReadAsync(source, Position, length, cancellationToken).ConfigureAwait(false);

            if (data is null)
            {
                buffer = [];

                return -1;
            }

            buffer = data;
            bufferStart = Position;
        }

        return buffer[(int)(Position++ - bufferStart)];
    }

    public async ValueTask<byte[]?> ReadBytesAsync(int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];

        for (var index = 0; index < count; index++)
        {
            var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (value < 0)
            {
                return null;
            }

            result[index] = (byte)value;
        }

        return result;
    }
}

using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.Tests.TestSupport;

internal sealed class FaultyBlockSource(IBlockSource inner, params (long Start, long End)[] badRanges) : IBlockSource
{
    public string DisplayName => inner.DisplayName;

    public long Length => inner.Length;

    public int SectorSize => inner.SectorSize;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var end = offset + buffer.Length;

        foreach (var (start, rangeEnd) in badRanges)
        {
            if (offset < rangeEnd && end > start)
            {
                throw new IOException("Simulierter Lesefehler.");
            }
        }

        return inner.ReadAsync(offset, buffer, cancellationToken);
    }

    public void Dispose() => inner.Dispose();
}

using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.Tests.TestSupport;

internal sealed class MemoryBlockSource(byte[] data, int sectorSize = 512) : IBlockSource
{
    public string DisplayName => "Speicherabbild";

    public long Length => data.Length;

    public int SectorSize => sectorSize;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset >= data.Length)
        {
            return ValueTask.FromResult(0);
        }

        var count = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsMemory((int)offset, count).CopyTo(buffer);

        return ValueTask.FromResult(count);
    }

    public void Dispose()
    {
    }
}

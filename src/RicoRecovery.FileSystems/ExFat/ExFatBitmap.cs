using System.Buffers.Binary;
using RicoRecovery.FileSystems.Fat;

namespace RicoRecovery.FileSystems.ExFat;

internal sealed class ExFatBitmap
{
    private const long MaxBitmapBytes = 256L * 1024 * 1024;

    private readonly byte[] bits;

    private ExFatBitmap(byte[] bits)
    {
        this.bits = bits;
    }

    public bool? IsAllocated(uint cluster)
    {
        if (cluster < 2)
        {
            return null;
        }

        var index = cluster - 2;
        var byteIndex = (long)(index >> 3);

        if (byteIndex >= bits.Length)
        {
            return null;
        }

        return (bits[byteIndex] & (1 << (int)(index & 7))) != 0;
    }

    public static async Task<ExFatBitmap?> LoadAsync(ExFatLayout layout, byte[] rootDirectory, CancellationToken cancellationToken)
    {
        var count = rootDirectory.Length / 32;
        var required = ((long)layout.Volume.ClusterCount + 7) / 8;

        for (var index = 0; index < count; index++)
        {
            var offset = index * 32;

            if (rootDirectory[offset] == 0x00)
            {
                break;
            }

            if (rootDirectory[offset] != 0x81)
            {
                continue;
            }

            var cluster = BinaryPrimitives.ReadUInt32LittleEndian(rootDirectory.AsSpan(offset + 20));
            var rawLength = BinaryPrimitives.ReadUInt64LittleEndian(rootDirectory.AsSpan(offset + 24));

            if (rawLength == 0 || rawLength > MaxBitmapBytes || (long)rawLength < required)
            {
                continue;
            }

            if (cluster < 2 || cluster > layout.Volume.ClusterCount + 1)
            {
                continue;
            }

            var length = (long)rawLength;
            var clusters = (length + layout.ClusterBytes - 1) / layout.ClusterBytes;
            var chain = await FatChain.FollowAsync(layout.Table, cluster, clusters, layout.OffsetOf, layout.ClusterBytes, cancellationToken).ConfigureAwait(false);

            if (!chain.Complete)
            {
                chain = FatChain.Contiguous(cluster, clusters, layout.OffsetOf, layout.ClusterBytes);
            }

            var data = await RunReader.ReadAllAsync(layout.Source, chain.Runs, length, cancellationToken).ConfigureAwait(false);

            if (data.Length >= required)
            {
                return new ExFatBitmap(data);
            }
        }

        return null;
    }
}

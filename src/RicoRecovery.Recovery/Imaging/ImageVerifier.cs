using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Imaging;

public static class ImageVerifier
{
    private const int ChunkBytes = 1 << 20;

    public static async Task<ImageVerificationResult> CompareAsync(
        IBlockSource source,
        IBlockSource image,
        IReadOnlyList<BadRange> excluded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(excluded);

        if (source.Length != image.Length)
        {
            return new ImageVerificationResult(false, Math.Min(source.Length, image.Length), 0);
        }

        var sectorSize = Math.Max(512, source.SectorSize);
        var left = new byte[ChunkBytes];
        var right = new byte[ChunkBytes];
        long compared = 0;

        for (long offset = 0; offset < source.Length; offset += ChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = (int)Math.Min(ChunkBytes, source.Length - offset);
            var leftRead = await ReadFullAsync(source, offset, left.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            var rightRead = await ReadFullAsync(image, offset, right.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

            if (leftRead != length || rightRead != length)
            {
                return new ImageVerificationResult(false, offset + Math.Min(leftRead, rightRead), compared);
            }

            if (left.AsSpan(0, length).SequenceEqual(right.AsSpan(0, length)))
            {
                compared += length;
                continue;
            }

            for (var inner = 0; inner < length; inner += sectorSize)
            {
                var count = Math.Min(sectorSize, length - inner);
                var position = offset + inner;

                if (IsExcluded(excluded, position))
                {
                    continue;
                }

                if (!left.AsSpan(inner, count).SequenceEqual(right.AsSpan(inner, count)))
                {
                    var difference = left.AsSpan(inner, count).CommonPrefixLength(right.AsSpan(inner, count));

                    return new ImageVerificationResult(false, position + difference, compared + inner);
                }
            }

            compared += length;
        }

        return new ImageVerificationResult(true, -1, compared);
    }

    private static bool IsExcluded(IReadOnlyList<BadRange> excluded, long position)
    {
        foreach (var range in excluded)
        {
            if (position >= range.Offset && position < range.Offset + range.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<int> ReadFullAsync(IBlockSource source, long offset, Memory<byte> memory, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < memory.Length)
        {
            var read = await source.ReadAsync(offset + total, memory[total..], cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

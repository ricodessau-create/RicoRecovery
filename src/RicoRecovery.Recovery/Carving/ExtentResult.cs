using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

internal sealed record ExtentResult(long Length, IntegrityStatus Integrity, string? Note, string? Extension = null);

internal static class ExtentHelpers
{
    public static long Limit(IBlockSource source, long start, FileSignature signature) =>
        Math.Min(source.Length, start + signature.MaxSize);

    public static ExtentResult Truncated(long length, string note) => new(length, IntegrityStatus.Truncated, note);

    public static ExtentResult Damaged(long length, string note) => new(length, IntegrityStatus.Damaged, note);

    public static async Task<long> FindAsync(IBlockSource source, long from, long limit, byte[] pattern, CancellationToken cancellationToken)
    {
        const int block = 1 << 20;

        var position = from;

        while (position + pattern.Length <= limit)
        {
            var length = (int)Math.Min(block + pattern.Length - 1, limit - position);
            var data = await SourceReader.ReadAsync(source, position, length, cancellationToken).ConfigureAwait(false);

            if (data is null)
            {
                return -1;
            }

            var index = data.AsSpan().IndexOf(pattern);

            if (index >= 0)
            {
                return position + index;
            }

            position += block;
        }

        return -1;
    }
}

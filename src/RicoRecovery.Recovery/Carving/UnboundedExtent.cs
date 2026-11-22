using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

internal static class UnboundedExtent
{
    private const int BlockBytes = 4 * 1024 * 1024;

    public static async Task<ExtentResult?> ResolveAsync(
        IBlockSource source,
        long start,
        FileSignature signature,
        CarveContext context,
        CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var alignment = context.Alignment;
        var window = context.Matcher.WindowBytes;
        var position = start + alignment;

        while (position < limit)
        {
            var length = (int)Math.Min(BlockBytes + window, source.Length - position);
            var data = await SourceReader.ReadAsync(source, position, length, cancellationToken).ConfigureAwait(false);

            if (data is null)
            {
                break;
            }

            var usable = (int)Math.Min(BlockBytes, limit - position);

            for (var offset = 0; offset < usable; offset += alignment)
            {
                if (context.Matcher.Match(data.AsSpan(offset)) is not null)
                {
                    return new ExtentResult(position + offset - start, IntegrityStatus.Unverified, "Dateiende anhand des nächsten Headers geschätzt.");
                }
            }

            position += usable;
        }

        return new ExtentResult(Math.Min(position, limit) - start, IntegrityStatus.Unverified, "Dateiende nicht bestimmbar, Länge bis zur Obergrenze angenommen.");
    }
}

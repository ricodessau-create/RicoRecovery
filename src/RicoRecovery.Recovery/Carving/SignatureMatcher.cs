using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

public sealed class SignatureMatcher
{
    private readonly Dictionary<int, FileSignature[][]> tables = [];

    public SignatureMatcher(IReadOnlyList<FileSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);

        foreach (var group in signatures.GroupBy(signature => signature.HeaderOffset))
        {
            var table = new FileSignature[256][];

            for (var value = 0; value < 256; value++)
            {
                table[value] = group
                    .Where(signature => signature.Header[0] == value)
                    .OrderByDescending(signature => signature.Header.Length)
                    .ToArray();
            }

            tables[group.Key] = table;
        }

        WindowBytes = signatures.Count == 0 ? 0 : signatures.Max(signature => signature.HeaderOffset + signature.Header.Length);
    }

    public int WindowBytes { get; }

    public FileSignature? Match(ReadOnlySpan<byte> window)
    {
        FileSignature? best = null;

        foreach (var (offset, table) in tables)
        {
            if (offset >= window.Length)
            {
                continue;
            }

            foreach (var candidate in table[window[offset]])
            {
                if (best is not null && candidate.Header.Length <= best.Header.Length)
                {
                    break;
                }

                if (offset + candidate.Header.Length <= window.Length
                    && window.Slice(offset, candidate.Header.Length).SequenceEqual(candidate.Header))
                {
                    best = candidate;
                    break;
                }
            }
        }

        return best;
    }
}

using System.Buffers.Binary;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

public sealed class UnknownSignatureProfiler
{
    private const int MaxEntries = 200_000;

    private readonly Dictionary<uint, Occurrence> occurrences = [];

    public void Observe(ReadOnlySpan<byte> window, long offset)
    {
        if (window.Length < 4)
        {
            return;
        }

        var zeros = 0;
        var ones = 0;

        for (var index = 0; index < 4; index++)
        {
            if (window[index] == 0x00)
            {
                zeros++;
            }
            else if (window[index] == 0xFF)
            {
                ones++;
            }
        }

        if (zeros >= 3 || ones >= 3)
        {
            return;
        }

        var key = BinaryPrimitives.ReadUInt32BigEndian(window);

        if (occurrences.TryGetValue(key, out var existing))
        {
            existing.Count++;

            return;
        }

        if (occurrences.Count >= MaxEntries)
        {
            Prune();
        }

        occurrences[key] = new Occurrence(offset);
    }

    public IReadOnlyList<UnknownSignatureCandidate> Candidates(int minimumCount, int take) =>
        occurrences
            .Where(pair => pair.Value.Count >= minimumCount)
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Value.FirstOffset)
            .Take(take)
            .Select(pair => new UnknownSignatureCandidate(Convert.ToHexString(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder((int)pair.Key))), pair.Value.Count, pair.Value.FirstOffset))
            .ToList();

    private void Prune()
    {
        foreach (var key in occurrences.Where(pair => pair.Value.Count == 1).Select(pair => pair.Key).ToList())
        {
            occurrences.Remove(key);
        }

        if (occurrences.Count >= MaxEntries)
        {
            occurrences.Clear();
        }
    }

    private sealed class Occurrence(long firstOffset)
    {
        public long Count { get; set; } = 1;

        public long FirstOffset { get; } = firstOffset;
    }
}

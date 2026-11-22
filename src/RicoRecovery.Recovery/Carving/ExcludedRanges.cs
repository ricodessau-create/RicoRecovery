using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

internal sealed class ExcludedRanges
{
    private readonly long[] starts;
    private readonly long[] ends;

    public ExcludedRanges(IReadOnlyList<DataRun> runs)
    {
        var mergedStarts = new List<long>();
        var mergedEnds = new List<long>();

        foreach (var run in runs.Where(candidate => candidate.Length > 0).OrderBy(candidate => candidate.Offset))
        {
            var runEnd = run.Offset + run.Length;

            if (mergedStarts.Count > 0 && run.Offset <= mergedEnds[^1])
            {
                mergedEnds[^1] = Math.Max(mergedEnds[^1], runEnd);
            }
            else
            {
                mergedStarts.Add(run.Offset);
                mergedEnds.Add(runEnd);
            }
        }

        starts = mergedStarts.ToArray();
        ends = mergedEnds.ToArray();
    }

    public long NextFree(long position)
    {
        var index = Array.BinarySearch(starts, position);

        if (index < 0)
        {
            index = ~index - 1;
        }

        return index >= 0 && position < ends[index] ? ends[index] : position;
    }
}

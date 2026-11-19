using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems;

internal sealed class RunMap
{
    private readonly DataRun[] runs;
    private readonly long[] starts;

    public RunMap(IReadOnlyList<DataRun> source)
    {
        runs = source.Where(run => run.Length > 0).ToArray();
        starts = new long[runs.Length];
        long position = 0;

        for (var index = 0; index < runs.Length; index++)
        {
            starts[index] = position;
            position += runs[index].Length;
        }

        Length = position;
    }

    public long Length { get; }

    public bool TryTranslate(long position, out long physicalOffset, out long contiguousBytes)
    {
        physicalOffset = 0;
        contiguousBytes = 0;

        if (position < 0 || position >= Length)
        {
            return false;
        }

        var index = Array.BinarySearch(starts, position);

        if (index < 0)
        {
            index = ~index - 1;
        }

        var run = runs[index];

        if (run.IsSparse)
        {
            return false;
        }

        var within = position - starts[index];
        physicalOffset = run.Offset + within;
        contiguousBytes = run.Length - within;

        return true;
    }
}

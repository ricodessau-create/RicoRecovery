using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Reconstruction;

internal sealed class RunBlockSource : IBlockSource
{
    private readonly IBlockSource inner;
    private readonly DataRun[] runs;
    private readonly long[] starts;

    public RunBlockSource(IBlockSource inner, IReadOnlyList<DataRun> runs)
    {
        this.inner = inner;
        this.runs = runs.Where(run => run.Length > 0).ToArray();
        starts = new long[this.runs.Length];

        long position = 0;

        for (var index = 0; index < this.runs.Length; index++)
        {
            starts[index] = position;
            position += this.runs[index].Length;
        }

        Length = position;
    }

    public string DisplayName => inner.DisplayName;

    public long Length { get; }

    public int SectorSize => inner.SectorSize;

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }

        var total = 0;

        while (total < buffer.Length && offset + total < Length)
        {
            var position = offset + total;
            var index = Array.BinarySearch(starts, position);

            if (index < 0)
            {
                index = ~index - 1;
            }

            var run = runs[index];
            var within = position - starts[index];
            var count = (int)Math.Min(buffer.Length - total, run.Length - within);

            if (run.IsSparse)
            {
                buffer.Slice(total, count).Span.Clear();
                total += count;
                continue;
            }

            var read = await inner.ReadAsync(run.Offset + within, buffer.Slice(total, count), cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }

            total += read;

            if (read < count)
            {
                break;
            }
        }

        return total;
    }

    public void Dispose()
    {
    }
}

using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Fat;

internal sealed class ClusterRunBuilder
{
    private readonly Func<uint, long> offsetOf;
    private readonly long clusterBytes;
    private readonly List<DataRun> runs = [];
    private long currentOffset = -1;
    private long currentLength;

    public ClusterRunBuilder(Func<uint, long> offsetOf, long clusterBytes)
    {
        this.offsetOf = offsetOf;
        this.clusterBytes = clusterBytes;
    }

    public long ClusterCount { get; private set; }

    public void Add(uint cluster)
    {
        var offset = offsetOf(cluster);

        if (currentOffset >= 0 && offset == currentOffset + currentLength)
        {
            currentLength += clusterBytes;
        }
        else
        {
            Flush();
            currentOffset = offset;
            currentLength = clusterBytes;
        }

        ClusterCount++;
    }

    public List<DataRun> Build()
    {
        Flush();

        return runs;
    }

    private void Flush()
    {
        if (currentOffset < 0)
        {
            return;
        }

        runs.Add(new DataRun(currentOffset, currentLength, false));
        currentOffset = -1;
        currentLength = 0;
    }
}

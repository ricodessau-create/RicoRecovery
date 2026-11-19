using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Fat;

internal sealed record ChainResult(List<DataRun> Runs, long Clusters, bool Complete);

internal static class FatChain
{
    public static async Task<ChainResult> FollowAsync(
        FatTable table,
        uint first,
        long maxClusters,
        Func<uint, long> offsetOf,
        long clusterBytes,
        CancellationToken cancellationToken)
    {
        var builder = new ClusterRunBuilder(offsetOf, clusterBytes);
        var current = first;
        var complete = false;

        while (maxClusters > 0)
        {
            builder.Add(current);

            if (builder.ClusterCount >= maxClusters)
            {
                complete = true;
                break;
            }

            var step = await table.ReadAsync(current, cancellationToken).ConfigureAwait(false);

            if (step.Link != FatLink.Next)
            {
                break;
            }

            current = step.Next;
        }

        return new ChainResult(builder.Build(), builder.ClusterCount, complete);
    }

    public static ChainResult Contiguous(uint first, long count, Func<uint, long> offsetOf, long clusterBytes)
    {
        if (count <= 0)
        {
            return new ChainResult([], 0, true);
        }

        var run = new DataRun(offsetOf(first), count * clusterBytes, false);

        return new ChainResult([run], count, true);
    }
}

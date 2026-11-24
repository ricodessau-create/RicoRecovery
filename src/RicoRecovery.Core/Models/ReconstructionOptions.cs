namespace RicoRecovery.Core.Models;

public sealed record ReconstructionOptions(
    int ClusterSize,
    int MaxGapClusters,
    int MaxBreakCandidates,
    int MaxAttempts,
    IReadOnlyList<DataRun> ExcludedRuns)
{
    public static ReconstructionOptions Default { get; } = new(0, 1024, 256, 100_000, []);
}

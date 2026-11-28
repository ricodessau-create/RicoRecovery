namespace RicoRecovery.Core.Models;

public sealed record ImageCreationOptions(
    int BlockSize,
    int MaxRetries,
    int RetryDelayMilliseconds,
    int SkipAfterConsecutiveBadSectors,
    int ProgressSaveIntervalBlocks,
    bool Resume,
    bool ComputeHash)
{
    public static ImageCreationOptions Default { get; } = new(1 << 20, 3, 100, 128, 16, true, false);
}

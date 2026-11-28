namespace RicoRecovery.Core.Models;

public sealed record ImageCreationState(
    int Version,
    long SourceLength,
    int SectorSize,
    long NextOffset,
    bool Completed,
    IReadOnlyList<BadRange> BadRanges)
{
    public const int CurrentVersion = 1;
}

namespace RicoRecovery.Core.Models;

public sealed record ImageCreationResult(
    long SourceLength,
    long BytesCopied,
    long BytesSkipped,
    long BadSectors,
    IReadOnlyList<BadRange> BadRanges,
    bool Completed,
    bool Resumed,
    string? Sha256);

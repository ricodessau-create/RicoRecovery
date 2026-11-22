namespace RicoRecovery.Core.Models;

public sealed record DeepScanResult(
    IReadOnlyList<CarvedFile> Files,
    IReadOnlyList<UnknownSignatureCandidate> UnknownCandidates,
    long FilesFound,
    long BytesScanned,
    long BadSectors,
    IReadOnlyList<string> Warnings);

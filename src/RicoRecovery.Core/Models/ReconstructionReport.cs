namespace RicoRecovery.Core.Models;

public sealed record ReconstructionReport(IReadOnlyList<ReconstructedFile> Files, int Attempted, int Skipped);

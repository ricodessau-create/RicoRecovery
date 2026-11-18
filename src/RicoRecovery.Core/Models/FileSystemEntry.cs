namespace RicoRecovery.Core.Models;

public sealed record FileSystemEntry(
    string Name,
    string Path,
    long Size,
    bool IsDirectory,
    bool IsDeleted,
    DateTimeOffset? Modified,
    FileSystemType FileSystem,
    DataAvailability Availability,
    IReadOnlyList<DataRun> DataRuns,
    byte[]? ResidentData,
    string? Note);

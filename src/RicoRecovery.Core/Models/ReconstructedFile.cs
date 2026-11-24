namespace RicoRecovery.Core.Models;

public sealed record ReconstructedFile(
    IReadOnlyList<DataRun> Runs,
    long Length,
    FileSignature Signature,
    string Extension,
    IntegrityStatus Integrity,
    ReconstructionMethod Method,
    string Note)
{
    public FileSystemEntry ToEntry(string name, string path) =>
        new(name, path, Length, false, false, null, FileSystemType.Unknown, DataAvailability.Unknown, Runs, null, Note);
}

using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Reconstruction;

public sealed class ReconstructionEngine
{
    private readonly FragmentReconstructor reconstructor;
    private readonly IScanLogger? logger;

    public ReconstructionEngine(IScanLogger? logger = null, IReadOnlyList<FileSignature>? signatures = null)
    {
        this.logger = logger;
        reconstructor = new FragmentReconstructor(signatures);
    }

    public async Task<ReconstructionReport> ReconstructAsync(
        IBlockSource source,
        IEnumerable<CarvedFile> files,
        ReconstructionOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(files);

        var all = files.ToList();
        var candidates = all
            .Where(file => (file.Integrity is IntegrityStatus.Truncated or IntegrityStatus.Damaged) && FragmentReconstructor.IsSupported(file.Signature))
            .ToList();
        var reconstructed = new List<ReconstructedFile>();

        await LogAsync("Info", $"Rekonstruktion gestartet: {candidates.Count} von {all.Count} Dateien sind Kandidaten.", cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await reconstructor.ReconstructAsync(source, candidates[index], options, cancellationToken).ConfigureAwait(false);

            if (result is not null)
            {
                reconstructed.Add(result);
                await LogAsync("Info", $"Datei bei Offset {candidates[index].Offset} rekonstruiert: {result.Note}", cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ScanProgress(index + 1, candidates.Count, reconstructed.Count));
        }

        await LogAsync("Info", $"Rekonstruktion beendet: {reconstructed.Count} von {candidates.Count} Kandidaten wiederhergestellt.", cancellationToken).ConfigureAwait(false);

        return new ReconstructionReport(reconstructed, candidates.Count, all.Count - candidates.Count);
    }

    private Task LogAsync(string level, string message, CancellationToken cancellationToken) =>
        logger is null
            ? Task.CompletedTask
            : logger.WriteAsync(new ScanLogEntry(DateTimeOffset.Now, level, message), cancellationToken);
}

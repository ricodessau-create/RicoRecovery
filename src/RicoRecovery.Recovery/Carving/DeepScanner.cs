using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

public sealed class DeepScanner
{
    private readonly SignatureCarver carver;
    private readonly IScanLogger? logger;

    public DeepScanner(IScanLogger? logger = null, IReadOnlyList<FileSignature>? signatures = null)
    {
        this.logger = logger;
        carver = new SignatureCarver(signatures);
    }

    public async Task<DeepScanResult> ScanAsync(
        IBlockSource source,
        DeepScanOptions? options = null,
        Func<CarvedFile, CancellationToken, ValueTask>? sink = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var effective = options ?? DeepScanOptions.Whole;
        var files = new List<CarvedFile>();
        var warnings = new List<string>();
        var profiler = new UnknownSignatureProfiler();

        await LogAsync("Info", $"Tiefenscan gestartet: {source.DisplayName}, {source.Length} Bytes, {effective.ExcludedRuns.Count} ausgeschlossene Bereiche.", cancellationToken).ConfigureAwait(false);

        var summary = await carver.CarveAsync(
            source,
            effective,
            async (file, token) =>
            {
                if (sink is not null)
                {
                    await sink(file, token).ConfigureAwait(false);
                }
                else
                {
                    files.Add(file);
                }
            },
            profiler,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (summary.BadSectors > 0)
        {
            var warning = $"{summary.BadSectors} nicht lesbare Bereiche wurden übersprungen. Dateien in diesen Bereichen können nicht gefunden werden.";
            warnings.Add(warning);
            await LogAsync("Warnung", warning, cancellationToken).ConfigureAwait(false);
        }

        await LogAsync("Info", $"Tiefenscan beendet: {summary.FilesFound} Dateien gefunden, {summary.BytesScanned} Bytes untersucht.", cancellationToken).ConfigureAwait(false);

        return new DeepScanResult(files, profiler.Candidates(3, 50), summary.FilesFound, summary.BytesScanned, summary.BadSectors, warnings);
    }

    private Task LogAsync(string level, string message, CancellationToken cancellationToken) =>
        logger is null
            ? Task.CompletedTask
            : logger.WriteAsync(new ScanLogEntry(DateTimeOffset.Now, level, message), cancellationToken);
}

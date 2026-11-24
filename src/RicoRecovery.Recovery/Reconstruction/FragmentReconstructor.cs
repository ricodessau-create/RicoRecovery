using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.Recovery.Carving;

namespace RicoRecovery.Recovery.Reconstruction;

public sealed class FragmentReconstructor
{
    private static readonly HashSet<ExtentStrategy> Supported =
    [
        ExtentStrategy.Png,
        ExtentStrategy.Zip,
        ExtentStrategy.SevenZip,
        ExtentStrategy.Mp4,
        ExtentStrategy.Ogg,
        ExtentStrategy.Gif,
        ExtentStrategy.Jpeg
    ];

    private readonly SignatureMatcher matcher;

    public FragmentReconstructor(IReadOnlyList<FileSignature>? signatures = null)
    {
        matcher = new SignatureMatcher(signatures ?? SignatureCatalog.All);
    }

    public static bool IsSupported(FileSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        return Supported.Contains(signature.Strategy);
    }

    public Task<ReconstructedFile?> ReconstructAsync(
        IBlockSource source,
        CarvedFile carved,
        ReconstructionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carved);

        return ReconstructCoreAsync(source, carved.Offset, carved.Signature, carved.Length, null, options ?? ReconstructionOptions.Default, cancellationToken);
    }

    public Task<ReconstructedFile?> ReconstructAsync(
        IBlockSource source,
        long start,
        FileSignature signature,
        long firstFragmentMax,
        ReconstructionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ReconstructCoreAsync(source, start, signature, firstFragmentMax, null, options ?? ReconstructionOptions.Default, cancellationToken);

    public async Task<ReconstructedFile?> ReconstructAsync(
        IBlockSource source,
        FileSystemEntry entry,
        ReconstructionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsDirectory || entry.ResidentData is not null || entry.Size <= 0 || entry.DataRuns.Count == 0 || entry.DataRuns[0].IsSparse)
        {
            return null;
        }

        var first = entry.DataRuns[0];
        var window = await SourceReader.ReadAvailableAsync(source, first.Offset, matcher.WindowBytes, cancellationToken).ConfigureAwait(false);
        var signature = matcher.Match(window);

        return signature is null
            ? null
            : await ReconstructCoreAsync(
                source,
                first.Offset,
                signature,
                Math.Min(entry.Size, first.Length),
                entry.Size,
                options ?? ReconstructionOptions.Default,
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReconstructedFile?> ReconstructCoreAsync(
        IBlockSource source,
        long start,
        FileSignature signature,
        long firstFragmentMax,
        long? expectedLength,
        ReconstructionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signature);

        if (!IsSupported(signature) || start < 0 || start >= source.Length)
        {
            return null;
        }

        var cluster = options.ClusterSize > 0 ? options.ClusterSize : Math.Max(512, source.SectorSize);
        var context = new CarveContext(matcher, cluster);
        var excluded = new ExcludedRanges(options.ExcludedRuns);
        var direct = await ExtentResolver.ResolveAsync(source, start, signature, context, cancellationToken).ConfigureAwait(false);

        if (direct is not null && direct.Integrity == IntegrityStatus.Valid && (expectedLength is null || direct.Length == expectedLength))
        {
            return new ReconstructedFile(
                [new DataRun(start, direct.Length, false)],
                direct.Length,
                signature,
                direct.Extension ?? signature.Extension,
                IntegrityStatus.Valid,
                ReconstructionMethod.ContiguousValidated,
                "Die Datei liegt zusammenhängend vor und ist vollständig validiert.");
        }

        var available = source.Length - start;
        var longest = AlignDown(Math.Min(firstFragmentMax, available), cluster);
        var attempts = 0;
        var candidates = 0;

        for (var first = longest; first >= cluster && candidates < options.MaxBreakCandidates; first -= cluster, candidates++)
        {
            for (var gap = 1; gap <= options.MaxGapClusters; gap++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (attempts++ >= options.MaxAttempts)
                {
                    return null;
                }

                var secondStart = start + first + (long)gap * cluster;

                if (secondStart >= source.Length)
                {
                    break;
                }

                if (excluded.NextFree(secondStart) > secondStart)
                {
                    continue;
                }

                var secondLength = Math.Min(source.Length - secondStart, signature.MaxSize);

                using var assembled = new RunBlockSource(
                    source,
                    [new DataRun(start, first, false), new DataRun(secondStart, secondLength, false)]);

                var result = await ExtentResolver.ResolveAsync(assembled, 0, signature, context, cancellationToken).ConfigureAwait(false);

                if (result is null || result.Integrity != IntegrityStatus.Valid || result.Length <= first || result.Length > first + secondLength)
                {
                    continue;
                }

                if (expectedLength is not null && result.Length != expectedLength)
                {
                    continue;
                }

                return new ReconstructedFile(
                    [new DataRun(start, first, false), new DataRun(secondStart, result.Length - first, false)],
                    result.Length,
                    signature,
                    result.Extension ?? signature.Extension,
                    signature.Strategy == ExtentStrategy.Jpeg ? IntegrityStatus.Plausible : IntegrityStatus.Valid,
                    ReconstructionMethod.BifragmentGap,
                    BuildNote(signature, gap));
            }
        }

        return null;
    }

    private static string BuildNote(FileSignature signature, int gap)
    {
        var note = $"Aus zwei Fragmenten zusammengesetzt, Lücke von {gap} Cluster.";

        return signature.Strategy == ExtentStrategy.Jpeg
            ? note + " Die Fragmentierung wurde nur anhand der Markerstruktur geprüft."
            : note;
    }

    private static long AlignDown(long value, int alignment) => value / alignment * alignment;
}

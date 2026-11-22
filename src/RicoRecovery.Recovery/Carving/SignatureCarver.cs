using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

public sealed class SignatureCarver
{
    private const int ChunkBytes = 8 * 1024 * 1024;
    private const int OverlapBytes = 1024;

    private readonly SignatureMatcher matcher;

    public SignatureCarver(IReadOnlyList<FileSignature>? signatures = null)
    {
        matcher = new SignatureMatcher(signatures ?? SignatureCatalog.All);
    }

    public async Task<CarveSummary> CarveAsync(
        IBlockSource source,
        DeepScanOptions options,
        Func<CarvedFile, CancellationToken, ValueTask> sink,
        UnknownSignatureProfiler? profiler,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        var alignment = options.Alignment > 0 ? options.Alignment : Math.Max(512, source.SectorSize);
        var start = AlignUp(Math.Max(0, options.StartOffset), alignment);
        var available = Math.Max(0, source.Length - options.StartOffset);
        var end = options.StartOffset + Math.Min(options.Length, available);
        var excluded = new ExcludedRanges(options.ExcludedRuns);
        var context = new CarveContext(matcher, alignment);
        var tracker = new ScanProgressTracker(Math.Max(0, end - start));
        var signatureSource = source;
        var chunkStart = start;
        var nextAllowed = start;
        long reported = 0;
        long badSectors = 0;
        long found = 0;

        while (chunkStart < end)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkEnd = Math.Min(chunkStart + ChunkBytes, end);
            var readLength = (int)(Math.Min(chunkEnd + OverlapBytes, signatureSource.Length) - chunkStart);
            var read = await ReadTolerantAsync(signatureSource, chunkStart, readLength, alignment, cancellationToken).ConfigureAwait(false);
            badSectors += read.Bad;

            foreach (var hit in ScanChunk(read.Data, chunkStart, chunkEnd, alignment, excluded, profiler))
            {
                if (hit.Offset < nextAllowed)
                {
                    continue;
                }

                var extent = await ExtentResolver.ResolveAsync(source, hit.Offset, hit.Signature, context, cancellationToken).ConfigureAwait(false);

                if (extent is null || extent.Length < hit.Signature.MinSize)
                {
                    continue;
                }

                var file = new CarvedFile(hit.Offset, extent.Length, hit.Signature, extent.Extension ?? hit.Signature.Extension, extent.Integrity, extent.Note);

                await sink(file, cancellationToken).ConfigureAwait(false);
                tracker.RegisterFile();
                found++;

                nextAllowed = extent.Integrity is IntegrityStatus.Valid or IntegrityStatus.Plausible
                    ? AlignUp(hit.Offset + extent.Length, alignment)
                    : hit.Offset + alignment;
            }

            chunkStart = Math.Max(chunkStart + ChunkBytes, AlignUp(nextAllowed, alignment));

            var processed = Math.Min(chunkStart, end) - start;
            tracker.Advance(processed - reported);
            reported = processed;
            progress?.Report(tracker.Snapshot());
        }

        return new CarveSummary(Math.Max(0, end - start), badSectors, found);
    }

    private List<Hit> ScanChunk(
        byte[] buffer,
        long chunkStart,
        long chunkEnd,
        int alignment,
        ExcludedRanges excluded,
        UnknownSignatureProfiler? profiler)
    {
        var hits = new List<Hit>();
        var position = chunkStart;

        while (position < chunkEnd)
        {
            var free = excluded.NextFree(position);

            if (free > position)
            {
                position = AlignUp(free, alignment);
                continue;
            }

            var window = buffer.AsSpan((int)(position - chunkStart));
            var signature = matcher.Match(window);

            if (signature is not null)
            {
                hits.Add(new Hit(position, signature));
            }
            else
            {
                profiler?.Observe(window, position);
            }

            position += alignment;
        }

        return hits;
    }

    private static async Task<(byte[] Data, long Bad)> ReadTolerantAsync(
        IBlockSource source,
        long offset,
        int length,
        int unit,
        CancellationToken cancellationToken)
    {
        var whole = await SourceReader.ReadAsync(source, offset, length, cancellationToken).ConfigureAwait(false);

        if (whole is not null)
        {
            return (whole, 0);
        }

        var data = new byte[length];
        long bad = 0;

        for (var position = 0; position < length; position += unit)
        {
            var count = Math.Min(unit, length - position);
            var part = await SourceReader.ReadAsync(source, offset + position, count, cancellationToken).ConfigureAwait(false);

            if (part is null)
            {
                bad++;
            }
            else
            {
                part.CopyTo(data, position);
            }
        }

        return (data, bad);
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private readonly record struct Hit(long Offset, FileSignature Signature);
}

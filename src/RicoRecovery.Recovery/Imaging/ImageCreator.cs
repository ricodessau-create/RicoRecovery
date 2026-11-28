using System.Buffers;
using System.Security.Cryptography;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Imaging;

public sealed class ImageCreator
{
    private const int HashChunkBytes = 1 << 20;

    private readonly IScanLogger? logger;

    public ImageCreator(IScanLogger? logger = null)
    {
        this.logger = logger;
    }

    public async Task<ImageCreationResult> CreateAsync(
        IBlockSource source,
        string imagePath,
        ImageCreationOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var effective = Normalize(options ?? ImageCreationOptions.Default);
        var fullPath = Path.GetFullPath(imagePath);
        var sectorSize = source.SectorSize;
        var blockSize = Math.Max(sectorSize, effective.BlockSize / sectorSize * sectorSize);
        var badRanges = new List<BadRange>();
        long position = 0;
        var resumed = false;

        if (effective.Resume)
        {
            var previous = await ImageStateStore.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);

            if (previous is not null
                && !previous.Completed
                && previous.SourceLength == source.Length
                && previous.SectorSize == sectorSize
                && previous.NextOffset > 0
                && previous.NextOffset <= source.Length
                && File.Exists(fullPath)
                && new FileInfo(fullPath).Length == source.Length)
            {
                position = previous.NextOffset;
                badRanges.AddRange(previous.BadRanges);
                resumed = true;
            }
        }

        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var handle = File.OpenHandle(
            fullPath,
            resumed ? FileMode.Open : FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous);

        if (!resumed)
        {
            RandomAccess.SetLength(handle, source.Length);
        }

        await LogAsync(
            "Info",
            resumed
                ? $"Abbilderstellung fortgesetzt ab Offset {position}: {source.DisplayName}, {source.Length} Bytes."
                : $"Abbilderstellung gestartet: {source.DisplayName}, {source.Length} Bytes, Sektorgröße {sectorSize}, Ziel {fullPath}.",
            cancellationToken).ConfigureAwait(false);

        var tracker = new ScanProgressTracker(source.Length);
        tracker.Advance(position);

        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        var completed = false;
        var blocksSinceSave = 0;

        try
        {
            while (position < source.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = (int)Math.Min(blockSize, source.Length - position);
                var attempt = await TryReadAsync(source, position, buffer.AsMemory(0, length), 0, 0, cancellationToken).ConfigureAwait(false);

                if (!attempt.Success)
                {
                    var found = await ReadSectorsAsync(source, position, buffer, length, sectorSize, effective, cancellationToken).ConfigureAwait(false);

                    foreach (var range in found)
                    {
                        AddBad(badRanges, range.Offset, (int)range.Length, range.Attempts);
                    }
                }

                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, length), position, cancellationToken).ConfigureAwait(false);

                position += length;
                tracker.Advance(length);
                progress?.Report(tracker.Snapshot());

                if (++blocksSinceSave >= effective.ProgressSaveIntervalBlocks)
                {
                    RandomAccess.FlushToDisk(handle);
                    await SaveStateAsync(fullPath, source, sectorSize, position, false, badRanges, CancellationToken.None).ConfigureAwait(false);
                    blocksSinceSave = 0;
                }
            }

            completed = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            RandomAccess.FlushToDisk(handle);
            await SaveStateAsync(fullPath, source, sectorSize, position, completed, badRanges, CancellationToken.None).ConfigureAwait(false);
        }

        string? hash = null;

        if (effective.ComputeHash)
        {
            hash = await ComputeHashAsync(handle, source.Length, cancellationToken).ConfigureAwait(false);
        }

        var skipped = badRanges.Sum(range => range.Length);

        await LogAsync(
            skipped > 0 ? "Warnung" : "Info",
            $"Abbilderstellung beendet: {source.Length - skipped} Bytes gelesen, {skipped} Bytes nicht lesbar ({skipped / sectorSize} Sektoren in {badRanges.Count} Bereichen).",
            cancellationToken).ConfigureAwait(false);

        return new ImageCreationResult(
            source.Length,
            source.Length - skipped,
            skipped,
            skipped / sectorSize,
            badRanges.ToArray(),
            true,
            resumed,
            hash);
    }

    public async Task<ImageCreationResult> RetryBadRangesAsync(
        IBlockSource source,
        string imagePath,
        ImageCreationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var effective = Normalize(options ?? ImageCreationOptions.Default);
        var fullPath = Path.GetFullPath(imagePath);
        var state = await ImageStateStore.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);

        if (state is null || state.SourceLength != source.Length || state.SectorSize != source.SectorSize || !File.Exists(fullPath))
        {
            throw new InvalidOperationException("Zu diesem Abbild liegt keine passende Fortschrittsdatei vor.");
        }

        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

        var sectorSize = state.SectorSize;
        var buffer = new byte[sectorSize];
        var remaining = new List<BadRange>();
        long recovered = 0;

        await LogAsync("Info", $"Erneuter Leseversuch für {state.BadRanges.Count} fehlerhafte Bereiche gestartet.", cancellationToken).ConfigureAwait(false);

        foreach (var range in state.BadRanges)
        {
            for (var offset = range.Offset; offset < range.Offset + range.Length; offset += sectorSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var count = (int)Math.Min(sectorSize, range.Offset + range.Length - offset);
                var attempt = await TryReadAsync(source, offset, buffer.AsMemory(0, count), effective.MaxRetries, effective.RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);

                if (attempt.Success)
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, count), offset, cancellationToken).ConfigureAwait(false);
                    recovered += count;
                }
                else
                {
                    AddBad(remaining, offset, count, range.Attempts + attempt.Attempts);
                }
            }
        }

        RandomAccess.FlushToDisk(handle);
        await ImageStateStore.SaveAsync(fullPath, state with { BadRanges = remaining.ToArray() }, CancellationToken.None).ConfigureAwait(false);

        var skipped = remaining.Sum(range => range.Length);

        await LogAsync(
            "Info",
            $"Erneuter Leseversuch beendet: {recovered} Bytes gerettet, {skipped} Bytes weiterhin nicht lesbar.",
            cancellationToken).ConfigureAwait(false);

        return new ImageCreationResult(
            source.Length,
            source.Length - skipped,
            skipped,
            skipped / sectorSize,
            remaining.ToArray(),
            state.Completed,
            false,
            null);
    }

    private static ImageCreationOptions Normalize(ImageCreationOptions options)
    {
        if (options.BlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Die Blockgröße muss positiv sein.");
        }

        if (options.MaxRetries < 0 || options.RetryDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Wiederholungsversuche und Wartezeit dürfen nicht negativ sein.");
        }

        return options with { ProgressSaveIntervalBlocks = Math.Max(1, options.ProgressSaveIntervalBlocks) };
    }

    private static async Task<(bool Success, int Attempts)> TryReadAsync(
        IBlockSource source,
        long offset,
        Memory<byte> memory,
        int maxRetries,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        var attempts = 0;

        while (true)
        {
            attempts++;

            try
            {
                var total = 0;

                while (total < memory.Length)
                {
                    var read = await source.ReadAsync(offset + total, memory[total..], cancellationToken).ConfigureAwait(false);

                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                if (total == memory.Length)
                {
                    return (true, attempts);
                }
            }
            catch (IOException)
            {
            }

            if (attempts > maxRetries)
            {
                return (false, attempts);
            }

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<BadRange>> ReadSectorsAsync(
        IBlockSource source,
        long blockOffset,
        byte[] buffer,
        int length,
        int sectorSize,
        ImageCreationOptions options,
        CancellationToken cancellationToken)
    {
        var found = new List<BadRange>();
        var consecutive = 0;

        for (var offset = 0; offset < length; offset += sectorSize)
        {
            var count = Math.Min(sectorSize, length - offset);
            var memory = buffer.AsMemory(offset, count);
            var skip = options.SkipAfterConsecutiveBadSectors > 0 && consecutive >= options.SkipAfterConsecutiveBadSectors;

            var result = skip
                ? (Success: false, Attempts: 0)
                : await TryReadAsync(source, blockOffset + offset, memory, options.MaxRetries, options.RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                consecutive = 0;
                continue;
            }

            memory.Span.Clear();
            consecutive++;
            AddBad(found, blockOffset + offset, count, result.Attempts);
        }

        foreach (var range in found)
        {
            await LogAsync(
                "Warnung",
                $"Nicht lesbarer Bereich bei Offset {range.Offset}, {range.Length} Bytes, {range.Attempts} Versuche.",
                cancellationToken).ConfigureAwait(false);
        }

        return found;
    }

    private static void AddBad(List<BadRange> ranges, long offset, int length, int attempts)
    {
        if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == offset)
        {
            var last = ranges[^1];
            ranges[^1] = last with { Length = last.Length + length, Attempts = Math.Max(last.Attempts, attempts) };

            return;
        }

        ranges.Add(new BadRange(offset, length, attempts));
    }

    private static Task SaveStateAsync(
        string imagePath,
        IBlockSource source,
        int sectorSize,
        long position,
        bool completed,
        List<BadRange> badRanges,
        CancellationToken cancellationToken) =>
        ImageStateStore.SaveAsync(
            imagePath,
            new ImageCreationState(ImageCreationState.CurrentVersion, source.Length, sectorSize, position, completed, badRanges.ToArray()),
            cancellationToken);

    private static async Task<string> ComputeHashAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashChunkBytes];
        long offset = 0;

        while (offset < length)
        {
            var count = (int)Math.Min(HashChunkBytes, length - offset);
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, count), offset, cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private Task LogAsync(string level, string message, CancellationToken cancellationToken) =>
        logger is null
            ? Task.CompletedTask
            : logger.WriteAsync(new ScanLogEntry(DateTimeOffset.Now, level, message), cancellationToken);
}

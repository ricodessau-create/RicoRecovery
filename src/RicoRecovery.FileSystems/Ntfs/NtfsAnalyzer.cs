using System.Runtime.CompilerServices;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ntfs;

public sealed class NtfsAnalyzer : IFileSystemAnalyzer, IFileSystemScanner
{
    public FileSystemType Type => FileSystemType.Ntfs;

    public async Task<bool> ProbeAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await NtfsBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        return volume is not null;
    }

    public async Task<VolumeInfo?> ReadVolumeInfoAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await NtfsBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null)
        {
            return null;
        }

        string? label = null;
        string? note = null;
        var mft = await NtfsMft.OpenAsync(source, partitionOffset, volume, cancellationToken).ConfigureAwait(false);

        if (mft is null)
        {
            note = "Die Master File Table konnte weder aus dem Original noch aus der Spiegelung gelesen werden.";
        }
        else
        {
            var volumeRecord = await mft.ReadRecordAsync(3, cancellationToken).ConfigureAwait(false);
            var nameAttribute = volumeRecord?.Attributes.FirstOrDefault(attribute =>
                attribute.Type == 0x60 && !attribute.NonResident && attribute.Value is not null);

            if (nameAttribute?.Value is { Length: > 0 } value)
            {
                label = Encoding.Unicode.GetString(value);
            }
        }

        return new VolumeInfo(Type, label, volume.TotalSectors * volume.BytesPerSector, volume.ClusterBytes, note);
    }

    public async IAsyncEnumerable<FileSystemEntry> EnumerateAsync(
        IBlockSource source,
        long partitionOffset,
        FileSystemScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var volume = await NtfsBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null)
        {
            yield break;
        }

        var mft = await NtfsMft.OpenAsync(source, partitionOffset, volume, cancellationToken).ConfigureAwait(false);

        if (mft is null)
        {
            yield break;
        }

        var builder = await NtfsEntryBuilder.CreateAsync(source, partitionOffset, volume, mft, cancellationToken).ConfigureAwait(false);
        long number = 0;

        while (number < mft.RecordCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = await mft.ReadChunkAsync(number, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < chunk.Count; index++)
            {
                var record = chunk.Buffer is null
                    ? await mft.ReadRecordAsync(number + index, cancellationToken).ConfigureAwait(false)
                    : mft.ParseRecord(chunk.Buffer, index, number + index);

                if (record is null)
                {
                    continue;
                }

                var entry = await builder.BuildAsync(record, cancellationToken).ConfigureAwait(false);

                if (entry is null)
                {
                    continue;
                }

                if (entry.IsDeleted ? options.IncludeDeleted : options.IncludeActive)
                {
                    yield return entry;
                }
            }

            number += chunk.Count;
        }
    }
}

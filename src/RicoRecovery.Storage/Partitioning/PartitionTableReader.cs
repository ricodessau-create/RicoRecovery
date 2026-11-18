using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Storage.Partitioning;

public static class PartitionTableReader
{
    private const ushort MbrSignature = 0xAA55;
    private const int MbrTableOffset = 446;
    private const int MbrEntrySize = 16;
    private const byte ProtectiveType = 0xEE;
    private const ulong GptSignature = 0x5452415020494645;
    private const int MaxLogicalPartitions = 128;
    private const uint MaxGptEntries = 4096;
    private const uint MaxGptEntrySize = 4096;

    private static readonly Dictionary<Guid, string> GptTypes = new()
    {
        [new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")] = "EFI-Systempartition",
        [new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")] = "Microsoft Reserved",
        [new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")] = "Basisdaten (NTFS / FAT / exFAT)",
        [new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")] = "Windows-Wiederherstellung",
        [new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4")] = "Linux-Dateisystem",
        [new Guid("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F")] = "Linux Swap",
        [new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928")] = "Linux LVM",
        [new Guid("7C3457EF-0000-11AA-AA11-00306543ECAC")] = "Apple APFS",
        [new Guid("48465300-0000-11AA-AA11-00306543ECAC")] = "Apple HFS+"
    };

    public static async Task<PartitionTableInfo> ReadAsync(IBlockSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var warnings = new List<string>();
        var sector = await ReadBytesAsync(source, 0, source.SectorSize, cancellationToken).ConfigureAwait(false);

        if (sector is null)
        {
            warnings.Add("Der erste Sektor konnte nicht gelesen werden.");
            return new PartitionTableInfo(PartitionScheme.None, [], warnings, false);
        }

        var hasSignature = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510)) == MbrSignature;
        var mbrEntries = hasSignature ? ParseMbrEntries(sector) : Array.Empty<MbrEntry>();
        var isProtective = mbrEntries.Any(entry => entry.Type == ProtectiveType);

        if (isProtective || !hasSignature)
        {
            var gpt = await ReadGptAsync(source, warnings, cancellationToken).ConfigureAwait(false);

            if (gpt is not null)
            {
                return gpt;
            }

            if (isProtective)
            {
                warnings.Add("Schutz-MBR gefunden, aber keine gültige GPT lesbar.");
            }
        }

        if (!hasSignature)
        {
            warnings.Add("Keine gültige Partitionstabelle gefunden (MBR-Signatur fehlt).");
            return new PartitionTableInfo(PartitionScheme.None, [], warnings, false);
        }

        var partitions = new List<PartitionEntry>();

        foreach (var entry in mbrEntries)
        {
            if (entry.Type == 0 || entry.Type == ProtectiveType || entry.SectorCount == 0)
            {
                continue;
            }

            if (IsExtended(entry.Type))
            {
                await WalkExtendedAsync(source, entry, partitions, warnings, cancellationToken).ConfigureAwait(false);
                continue;
            }

            partitions.Add(CreateMbrPartition(partitions.Count + 1, entry.StartLba, entry, false, source, warnings));
        }

        return new PartitionTableInfo(PartitionScheme.Mbr, partitions, warnings, false);
    }

    private static async Task WalkExtendedAsync(
        IBlockSource source,
        MbrEntry extended,
        List<PartitionEntry> partitions,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var sectorSize = source.SectorSize;
        var extendedStart = (long)extended.StartLba;
        var current = extendedStart;
        var visited = new HashSet<long>();

        for (var step = 0; step < MaxLogicalPartitions; step++)
        {
            if (!visited.Add(current))
            {
                warnings.Add("Zyklus in der Kette der erweiterten Partitionen erkannt.");
                return;
            }

            var ebr = await ReadBytesAsync(source, current * sectorSize, sectorSize, cancellationToken).ConfigureAwait(false);

            if (ebr is null || BinaryPrimitives.ReadUInt16LittleEndian(ebr.AsSpan(510)) != MbrSignature)
            {
                warnings.Add($"Der erweiterte Partitionseintrag bei Sektor {current} ist nicht lesbar oder ungültig.");
                return;
            }

            var table = ParseMbrEntries(ebr);
            var logical = table[0];
            var link = table[1];

            if (logical.Type != 0 && logical.SectorCount > 0)
            {
                partitions.Add(CreateMbrPartition(partitions.Count + 1, current + logical.StartLba, logical, true, source, warnings));
            }

            if (!IsExtended(link.Type) || link.SectorCount == 0)
            {
                return;
            }

            current = extendedStart + link.StartLba;
        }

        warnings.Add("Die maximale Anzahl logischer Partitionen wurde erreicht.");
    }

    private static PartitionEntry CreateMbrPartition(
        int index,
        long startSector,
        MbrEntry entry,
        bool logical,
        IBlockSource source,
        List<string> warnings)
    {
        var startOffset = startSector * source.SectorSize;
        var length = entry.SectorCount * (long)source.SectorSize;

        if (startOffset + length > source.Length)
        {
            warnings.Add($"Partition {index} reicht über das Ende des Datenträgers hinaus.");
        }

        return new PartitionEntry(index, startOffset, length, DescribeMbrType(entry.Type), null, entry.Active, logical);
    }

    private static MbrEntry[] ParseMbrEntries(byte[] sector)
    {
        var entries = new MbrEntry[4];

        for (var slot = 0; slot < entries.Length; slot++)
        {
            var offset = MbrTableOffset + slot * MbrEntrySize;

            entries[slot] = new MbrEntry(
                sector[offset] == 0x80,
                sector[offset + 4],
                BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(offset + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(offset + 12)));
        }

        return entries;
    }

    private static bool IsExtended(byte type) => type is 0x05 or 0x0F or 0x85;

    private static string DescribeMbrType(byte type) => type switch
    {
        0x01 => "FAT12",
        0x04 => "FAT16 (klein)",
        0x05 => "Erweitert",
        0x06 => "FAT16",
        0x07 => "NTFS / exFAT / HPFS",
        0x0B => "FAT32 (CHS)",
        0x0C => "FAT32 (LBA)",
        0x0E => "FAT16 (LBA)",
        0x0F => "Erweitert (LBA)",
        0x27 => "Windows-Wiederherstellung",
        0x82 => "Linux Swap",
        0x83 => "Linux",
        0x8E => "Linux LVM",
        0xEF => "EFI-Systempartition",
        _ => $"Unbekannt (0x{type:X2})"
    };

    private static string DescribeGptType(Guid type) =>
        GptTypes.TryGetValue(type, out var description) ? description : $"Unbekannt ({type})";

    private static async Task<PartitionTableInfo?> ReadGptAsync(
        IBlockSource source,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var primary = await ReadGptAtAsync(source, 1, cancellationToken).ConfigureAwait(false);

        if (primary.Entries is not null)
        {
            warnings.AddRange(primary.EntryWarnings);
            return new PartitionTableInfo(PartitionScheme.Gpt, primary.Entries, warnings, false);
        }

        var lastLba = source.Length / source.SectorSize - 1;
        var backup = lastLba > 1
            ? await ReadGptAtAsync(source, lastLba, cancellationToken).ConfigureAwait(false)
            : GptOutcome.Absent;

        if (backup.Entries is not null)
        {
            if (primary.Problem is not null)
            {
                warnings.Add(primary.Problem);
            }

            warnings.Add("Die primäre GPT ist nicht verwendbar. Die Sicherungs-GPT am Ende des Datenträgers wird verwendet.");
            warnings.AddRange(backup.EntryWarnings);

            return new PartitionTableInfo(PartitionScheme.Gpt, backup.Entries, warnings, true);
        }

        if (primary.Problem is not null)
        {
            warnings.Add(primary.Problem);
        }

        if (backup.Problem is not null)
        {
            warnings.Add(backup.Problem);
        }

        return null;
    }

    private static async Task<GptOutcome> ReadGptAtAsync(IBlockSource source, long headerLba, CancellationToken cancellationToken)
    {
        var sectorSize = source.SectorSize;
        var header = await ReadBytesAsync(source, headerLba * sectorSize, sectorSize, cancellationToken).ConfigureAwait(false);

        if (header is null || BinaryPrimitives.ReadUInt64LittleEndian(header) != GptSignature)
        {
            return GptOutcome.Absent;
        }

        var label = headerLba == 1 ? "Primäre GPT" : "Sicherungs-GPT";
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));

        if (headerSize < 92 || headerSize > (uint)sectorSize)
        {
            return GptOutcome.Invalid($"{label}: ungültige Headergröße.");
        }

        var storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var headerCopy = header.AsSpan(0, (int)headerSize).ToArray();
        Array.Clear(headerCopy, 16, 4);

        if (Crc32.Compute(headerCopy) != storedHeaderCrc)
        {
            return GptOutcome.Invalid($"{label}: Prüfsumme des Headers ungültig.");
        }

        var entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        var storedEntriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88));

        var parametersValid = count > 0
            && count <= MaxGptEntries
            && entrySize >= 128
            && entrySize <= MaxGptEntrySize
            && entrySize % 8 == 0
            && entriesLba > 0
            && entriesLba <= source.Length / sectorSize;

        if (!parametersValid)
        {
            return GptOutcome.Invalid($"{label}: ungültige Angaben zur Partitionseintragstabelle.");
        }

        var dataLength = (int)((long)count * entrySize);
        var data = await ReadBytesAsync(source, entriesLba * sectorSize, dataLength, cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            return GptOutcome.Invalid($"{label}: Partitionseinträge nicht lesbar.");
        }

        if (Crc32.Compute(data) != storedEntriesCrc)
        {
            return GptOutcome.Invalid($"{label}: Prüfsumme der Partitionseinträge ungültig.");
        }

        var entries = new List<PartitionEntry>();
        var entryWarnings = new List<string>();
        var limit = long.MaxValue / sectorSize;

        for (var index = 0; index < count; index++)
        {
            var span = data.AsSpan(index * (int)entrySize, (int)entrySize);
            var typeGuid = new Guid(span[..16]);

            if (typeGuid == Guid.Empty)
            {
                continue;
            }

            var first = BinaryPrimitives.ReadInt64LittleEndian(span[32..]);
            var last = BinaryPrimitives.ReadInt64LittleEndian(span[40..]);
            var attributes = BinaryPrimitives.ReadUInt64LittleEndian(span[48..]);
            var name = Encoding.Unicode.GetString(span.Slice(56, 72)).TrimEnd('\0');

            if (first < 0 || last < first || last >= limit)
            {
                entryWarnings.Add($"GPT-Eintrag {index + 1} hat ungültige Grenzen und wird übersprungen.");
                continue;
            }

            var startOffset = first * sectorSize;
            var length = (last - first + 1) * sectorSize;

            if (startOffset + length > source.Length)
            {
                entryWarnings.Add($"GPT-Eintrag {index + 1} reicht über das Ende des Datenträgers hinaus.");
            }

            entries.Add(new PartitionEntry(
                entries.Count + 1,
                startOffset,
                length,
                DescribeGptType(typeGuid),
                string.IsNullOrEmpty(name) ? null : name,
                (attributes & 4) != 0,
                false));
        }

        return new GptOutcome(entries, null, true, entryWarnings);
    }

    private static async Task<byte[]?> ReadBytesAsync(IBlockSource source, long offset, int length, CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0 || offset + length > source.Length)
        {
            return null;
        }

        var buffer = new byte[length];
        var read = await source.ReadAsync(offset, buffer, cancellationToken).ConfigureAwait(false);

        return read == length ? buffer : null;
    }

    private readonly record struct MbrEntry(bool Active, byte Type, uint StartLba, uint SectorCount);

    private sealed record GptOutcome(
        IReadOnlyList<PartitionEntry>? Entries,
        string? Problem,
        bool SignatureFound,
        IReadOnlyList<string> EntryWarnings)
    {
        public static GptOutcome Absent { get; } = new(null, null, false, []);

        public static GptOutcome Invalid(string problem) => new(null, problem, true, []);
    }
}

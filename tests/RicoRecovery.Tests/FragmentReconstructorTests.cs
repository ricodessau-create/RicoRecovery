using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems.Fat;
using RicoRecovery.Recovery.Carving;
using RicoRecovery.Recovery.Reconstruction;
using RicoRecovery.Storage.Partitioning;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class FragmentReconstructorTests
{
    private static readonly ReconstructionOptions Options = new(512, 64, 16, 10_000, []);

    [Fact]
    public async Task FragmentedPngIsReassembledAfterDeepScan()
    {
        var png = BuildPng();
        var image = Fragment(png, 2048, 4, 4);
        var source = new MemoryBlockSource(image);

        var deep = await new DeepScanner().ScanAsync(source);
        var carved = deep.Files.Single(file => file.Offset == 4 * 512);

        Assert.True(carved.Integrity is IntegrityStatus.Truncated or IntegrityStatus.Damaged);

        var result = await new FragmentReconstructor().ReconstructAsync(source, carved, Options);

        Assert.NotNull(result);
        Assert.Equal(ReconstructionMethod.BifragmentGap, result.Method);
        Assert.Equal(IntegrityStatus.Valid, result.Integrity);
        Assert.Equal(png.Length, result.Length);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(12 * 512L, result.Runs[1].Offset);
        Assert.Equal(png, await ScanHelper.ReadContentAsync(image, result.ToEntry("a.png", "/a.png")));
    }

    [Fact]
    public async Task ContiguousValidFileIsReportedWithoutReassembly()
    {
        var png = BuildPng();
        var image = new byte[64 * 512];
        png.CopyTo(image, 4 * 512);
        var source = new MemoryBlockSource(image);

        var result = await new FragmentReconstructor().ReconstructAsync(source, 4 * 512, PngSignature(), png.Length, Options);

        Assert.NotNull(result);
        Assert.Equal(ReconstructionMethod.ContiguousValidated, result.Method);
        Assert.Single(result.Runs);
        Assert.Equal(png.Length, result.Length);
    }

    [Fact]
    public async Task FragmentedZipIsReassembledFromTheCentralDirectory()
    {
        var zip = BuildZip();
        var image = Fragment(zip, 2048, 4, 4);
        var source = new MemoryBlockSource(image);
        var signature = SignatureCatalog.All.Single(candidate => candidate.Name.StartsWith("ZIP", StringComparison.Ordinal));

        var result = await new FragmentReconstructor().ReconstructAsync(source, 4 * 512, signature, 2048, Options);

        Assert.NotNull(result);
        Assert.Equal(ReconstructionMethod.BifragmentGap, result.Method);
        Assert.Equal("zip", result.Extension);
        Assert.Equal(zip.Length, result.Length);
        Assert.Equal(zip, await ScanHelper.ReadContentAsync(image, result.ToEntry("a.zip", "/a.zip")));
    }

    [Fact]
    public async Task MissingSecondFragmentIsNotReassembled()
    {
        var png = BuildPng();
        var image = new byte[64 * 512];
        png.AsSpan(0, 2048).CopyTo(image.AsSpan(4 * 512));
        var source = new MemoryBlockSource(image);

        var result = await new FragmentReconstructor().ReconstructAsync(source, 4 * 512, PngSignature(), 2048, Options);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExcludedClustersAreNotUsedAsSecondFragment()
    {
        var png = BuildPng();
        var image = Fragment(png, 2048, 4, 4);
        var source = new MemoryBlockSource(image);
        var options = new ReconstructionOptions(512, 64, 16, 10_000, [new DataRun(12 * 512L, 4096, false)]);

        var result = await new FragmentReconstructor().ReconstructAsync(source, 4 * 512, PngSignature(), 2048, options);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeletedFatFileWithFragmentsIsReassembled()
    {
        var png = BuildPng();
        var builder = new Fat16ImageBuilder();
        builder.WriteData(5, png[..2048]);
        builder.WriteData(9, Enumerable.Repeat((byte)0xAA, 2048).ToArray());
        builder.WriteData(13, png[2048..]);
        builder.AddEntry(0, "PIC.PNG", 0x20, 5, png.Length, true);

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);
        var entry = Assert.Single(entries);

        var result = await new FragmentReconstructor().ReconstructAsync(new MemoryBlockSource(builder.Image), entry, Options);

        Assert.NotNull(result);
        Assert.Equal(ReconstructionMethod.BifragmentGap, result.Method);
        Assert.Equal(png.Length, result.Length);
        Assert.Equal(Fat16ImageBuilder.ClusterOffset(13), result.Runs[1].Offset);
        Assert.Equal(png, await ScanHelper.ReadContentAsync(builder.Image, result.ToEntry("PIC.PNG", "/PIC.PNG")));
    }

    [Fact]
    public async Task EngineReconstructsOnlyDamagedOrTruncatedFiles()
    {
        var png = BuildPng();
        var image = Fragment(png, 2048, 4, 4);
        SampleFiles.Gif().CopyTo(image, 30 * 512);
        var source = new MemoryBlockSource(image);

        var deep = await new DeepScanner().ScanAsync(source);
        var report = await new ReconstructionEngine().ReconstructAsync(source, deep.Files, Options);

        Assert.Equal(1, report.Attempted);
        Assert.Equal(1, report.Skipped);

        var file = Assert.Single(report.Files);
        Assert.Equal(png.Length, file.Length);
        Assert.Equal("png", file.Extension);
    }

    [Fact]
    public void OnlyFormatsWithStrongValidationAreSupported()
    {
        var bmp = SignatureCatalog.All.Single(candidate => candidate.Name == "BMP");

        Assert.True(FragmentReconstructor.IsSupported(PngSignature()));
        Assert.False(FragmentReconstructor.IsSupported(bmp));
    }

    [Fact]
    public async Task UnsupportedFormatIsNotReconstructed()
    {
        var bmp = SignatureCatalog.All.Single(candidate => candidate.Name == "BMP");
        var image = new byte[64 * 512];
        SampleFiles.Bmp().CopyTo(image, 4 * 512);

        var result = await new FragmentReconstructor().ReconstructAsync(new MemoryBlockSource(image), 4 * 512, bmp, 512, Options);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReconstructionDoesNotModifyTheImage()
    {
        var png = BuildPng();
        var image = Fragment(png, 2048, 4, 4);
        var hashBefore = SHA256.HashData(image);

        await new FragmentReconstructor().ReconstructAsync(new MemoryBlockSource(image), 4 * 512, PngSignature(), 2048, Options);

        Assert.Equal(hashBefore, SHA256.HashData(image));
    }

    private static FileSignature PngSignature() => SignatureCatalog.All.Single(candidate => candidate.Name == "PNG");

    private static byte[] Fragment(byte[] file, int firstLength, int startSector, int gapSectors)
    {
        var image = new byte[64 * 512];
        var gapStart = (startSector + firstLength / 512) * 512;
        var secondSector = startSector + firstLength / 512 + gapSectors;

        file.AsSpan(0, firstLength).CopyTo(image.AsSpan(startSector * 512));
        file.AsSpan(firstLength).CopyTo(image.AsSpan(secondSector * 512));
        Array.Fill(image, (byte)0xAA, gapStart, gapSectors * 512);

        return image;
    }

    private static byte[] BuildPng()
    {
        var parts = new List<byte[]>
        {
            SampleFiles.Bytes(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            Chunk("IHDR", SampleFiles.Bytes(0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0))
        };

        for (var index = 0; index < 10; index++)
        {
            var shift = index;
            parts.Add(Chunk("IDAT", ScanHelper.Pattern(300).Select(value => (byte)(value + shift)).ToArray()));
        }

        parts.Add(Chunk("IEND", []));

        return SampleFiles.Concat(parts.ToArray());
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        var crc = new byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(SampleFiles.Concat(typeBytes, data)));

        return SampleFiles.Concat(length, typeBytes, data, crc);
    }

    private static byte[] BuildZip()
    {
        var content = new byte[3000];
        new Random(7).NextBytes(content);

        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("data.bin", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }

        return stream.ToArray();
    }
}

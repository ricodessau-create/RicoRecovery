using System.Security.Cryptography;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.Recovery;
using RicoRecovery.Recovery.Imaging;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class ImageCreatorTests : IDisposable
{
    private static readonly ImageCreationOptions FastOptions = new(4096, 2, 0, 0, 4, true, true);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ricorecovery-images-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompleteCopyIsIdenticalToTheSource()
    {
        var data = ScanHelper.Pattern(64 * 1024 + 1536);
        var path = ImagePath("komplett.img");

        var result = await new ImageCreator().CreateAsync(new MemoryBlockSource(data), path, FastOptions);

        Assert.True(result.Completed);
        Assert.False(result.Resumed);
        Assert.Empty(result.BadRanges);
        Assert.Equal(0, result.BadSectors);
        Assert.Equal(data.Length, result.BytesCopied);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), result.Sha256);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UnreadableSectorsAreZeroFilledAndRecorded()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var path = ImagePath("fehler.img");
        var source = new FaultyBlockSource(new MemoryBlockSource(data), (20 * 512L, 22 * 512L));

        var result = await new ImageCreator().CreateAsync(source, path, FastOptions);

        var range = Assert.Single(result.BadRanges);
        Assert.Equal(20 * 512L, range.Offset);
        Assert.Equal(1024, range.Length);
        Assert.Equal(3, range.Attempts);
        Assert.Equal(2, result.BadSectors);
        Assert.Equal(data.Length - 1024, result.BytesCopied);

        var expected = (byte[])data.Clone();
        Array.Clear(expected, 20 * 512, 1024);

        Assert.Equal(expected, await File.ReadAllBytesAsync(path));

        var state = await ImageStateStore.LoadAsync(path);

        Assert.NotNull(state);
        Assert.True(state.Completed);
        Assert.Equal(data.Length, state.NextOffset);
        Assert.Single(state.BadRanges);
    }

    [Fact]
    public async Task LongRunsOfBadSectorsAreSkippedWithoutFurtherAttempts()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var path = ImagePath("lang.img");
        var source = new FaultyBlockSource(new MemoryBlockSource(data), (16 * 512L, 24 * 512L));
        var options = new ImageCreationOptions(4096, 2, 0, 2, 4, true, false);

        var result = await new ImageCreator().CreateAsync(source, path, options);

        var range = Assert.Single(result.BadRanges);
        Assert.Equal(16 * 512L, range.Offset);
        Assert.Equal(4096, range.Length);
        Assert.Equal(3, range.Attempts);
        Assert.Equal(8, result.BadSectors);
    }

    [Fact]
    public async Task InterruptedCreationIsResumed()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var path = ImagePath("fortsetzen.img");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImageCreator().CreateAsync(new CancelAfterSource(new MemoryBlockSource(data), cancellation, 5), path, FastOptions, null, cancellation.Token));

        var state = await ImageStateStore.LoadAsync(path);

        Assert.NotNull(state);
        Assert.False(state.Completed);
        Assert.InRange(state.NextOffset, 1, data.Length - 1);

        var result = await new ImageCreator().CreateAsync(new MemoryBlockSource(data), path, FastOptions);

        Assert.True(result.Resumed);
        Assert.True(result.Completed);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), result.Sha256);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ResumeIsIgnoredWhenTheSourceChanged()
    {
        var first = ScanHelper.Pattern(64 * 1024);
        var second = ScanHelper.Pattern(32 * 1024).Select(value => (byte)(value + 1)).ToArray();
        var path = ImagePath("geaendert.img");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImageCreator().CreateAsync(new CancelAfterSource(new MemoryBlockSource(first), cancellation, 5), path, FastOptions, null, cancellation.Token));

        var result = await new ImageCreator().CreateAsync(new MemoryBlockSource(second), path, FastOptions);

        Assert.False(result.Resumed);
        Assert.Equal(second, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RetryRecoversPreviouslyUnreadableSectors()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var path = ImagePath("retry.img");
        var faulty = new FaultyBlockSource(new MemoryBlockSource(data), (20 * 512L, 22 * 512L));

        await new ImageCreator().CreateAsync(faulty, path, FastOptions);

        var result = await new ImageCreator().RetryBadRangesAsync(new MemoryBlockSource(data), path, FastOptions);

        Assert.Empty(result.BadRanges);
        Assert.Equal(0, result.BadSectors);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));

        var state = await ImageStateStore.LoadAsync(path);

        Assert.NotNull(state);
        Assert.Empty(state.BadRanges);
    }

    [Fact]
    public async Task RetryKeepsSectorsThatAreStillUnreadable()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var path = ImagePath("retry-fehler.img");
        var faulty = new FaultyBlockSource(new MemoryBlockSource(data), (20 * 512L, 22 * 512L));

        await new ImageCreator().CreateAsync(faulty, path, FastOptions);

        var result = await new ImageCreator().RetryBadRangesAsync(faulty, path, FastOptions);

        var range = Assert.Single(result.BadRanges);
        Assert.Equal(20 * 512L, range.Offset);
        Assert.Equal(1024, range.Length);
        Assert.True(range.Attempts > 3);
    }

    [Fact]
    public async Task RetryWithoutStateFileThrows()
    {
        var data = ScanHelper.Pattern(8 * 1024);
        var path = ImagePath("ohne-zustand.img");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(path, data);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ImageCreator().RetryBadRangesAsync(new MemoryBlockSource(data), path, FastOptions));
    }

    [Fact]
    public async Task ProgressEndsAtOneHundredPercent()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var progress = new CollectingProgress();

        await new ImageCreator().CreateAsync(new MemoryBlockSource(data), ImagePath("fortschritt.img"), FastOptions, progress);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);
    }

    [Fact]
    public async Task SourceDataIsNotModified()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var hashBefore = SHA256.HashData(data);

        await new ImageCreator().CreateAsync(new MemoryBlockSource(data), ImagePath("unveraendert.img"), FastOptions);

        Assert.Equal(hashBefore, SHA256.HashData(data));
    }

    [Fact]
    public async Task EmptySourceCreatesEmptyImage()
    {
        var path = ImagePath("leer.img");

        var result = await new ImageCreator().CreateAsync(new MemoryBlockSource([]), path, FastOptions);

        Assert.True(result.Completed);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public async Task InvalidArgumentsAreRejected()
    {
        var creator = new ImageCreator();

        await Assert.ThrowsAsync<ArgumentNullException>(() => creator.CreateAsync(null!, ImagePath("a.img")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => creator.CreateAsync(new MemoryBlockSource([]), " "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            creator.CreateAsync(new MemoryBlockSource([]), ImagePath("b.img"), new ImageCreationOptions(0, 3, 0, 0, 1, true, false)));
    }

    [Fact]
    public void StatePathAppendsTheProgressSuffix()
    {
        Assert.Equal(@"C:\Abbilder\x.img.fortschritt", ImageStateStore.StatePath(@"C:\Abbilder\x.img"));
    }

    [Fact]
    public async Task ImageCanBeAnalyzedAsAnIndependentSource()
    {
        var builder = new Fat16ImageBuilder();
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.BIN", 0x20, 2, 10, false);
        var path = ImagePath("fat.img");

        await new ImageCreator().CreateAsync(new MemoryBlockSource(builder.Image), path, FastOptions);

        using var imageSource = await ImageOpener.OpenAsync(path);
        var result = await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(imageSource);

        Assert.Equal("/KEEP.BIN", Assert.Single(result.Items).Entry.Path);
    }

    [Fact]
    public async Task VerifierAcceptsIdenticalContent()
    {
        var data = ScanHelper.Pattern(64 * 1024);

        var result = await ImageVerifier.CompareAsync(new MemoryBlockSource(data), new MemoryBlockSource((byte[])data.Clone()), []);

        Assert.True(result.Identical);
        Assert.Equal(-1, result.FirstMismatch);
        Assert.Equal(data.Length, result.BytesCompared);
    }

    [Fact]
    public async Task VerifierReportsTheFirstMismatch()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var copy = (byte[])data.Clone();
        copy[10_000] ^= 0xFF;

        var result = await ImageVerifier.CompareAsync(new MemoryBlockSource(data), new MemoryBlockSource(copy), []);

        Assert.False(result.Identical);
        Assert.Equal(10_000, result.FirstMismatch);
    }

    [Fact]
    public async Task VerifierIgnoresKnownBadRanges()
    {
        var data = ScanHelper.Pattern(64 * 1024);
        var copy = (byte[])data.Clone();
        copy[10_000] ^= 0xFF;

        var result = await ImageVerifier.CompareAsync(
            new MemoryBlockSource(data),
            new MemoryBlockSource(copy),
            [new BadRange(19 * 512L, 512, 3)]);

        Assert.True(result.Identical);
    }

    [Fact]
    public async Task VerifierDetectsDifferentLengths()
    {
        var result = await ImageVerifier.CompareAsync(
            new MemoryBlockSource(new byte[2048]),
            new MemoryBlockSource(new byte[1024]),
            []);

        Assert.False(result.Identical);
        Assert.Equal(1024, result.FirstMismatch);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private string ImagePath(string name) => Path.Combine(directory, name);

    private sealed class CollectingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = [];

        public void Report(ScanProgress value) => Reports.Add(value);
    }
}

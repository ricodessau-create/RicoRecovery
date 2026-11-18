using System.Security.Cryptography;
using RicoRecovery.Storage;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class ImageFileBlockSourceTests : IDisposable
{
    private readonly string imagePath;
    private readonly byte[] content;

    public ImageFileBlockSourceTests()
    {
        imagePath = Path.Combine(Path.GetTempPath(), $"ricorecovery-test-{Guid.NewGuid():N}.img");
        content = new byte[8192];
        new Random(42).NextBytes(content);
        File.WriteAllBytes(imagePath, content);
    }

    [Fact]
    public async Task ReadReturnsExactBytesAtOffset()
    {
        using var source = new ImageFileBlockSource(imagePath);
        var buffer = new byte[512];

        var read = await source.ReadAsync(1024, buffer);

        Assert.Equal(512, read);
        Assert.Equal(content.AsSpan(1024, 512).ToArray(), buffer);
    }

    [Fact]
    public async Task ReadBeyondEndReturnsZero()
    {
        using var source = new ImageFileBlockSource(imagePath);
        var buffer = new byte[512];

        var read = await source.ReadAsync(source.Length, buffer);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ReadAcrossEndReturnsPartialData()
    {
        using var source = new ImageFileBlockSource(imagePath);
        var buffer = new byte[1024];

        var read = await source.ReadAsync(source.Length - 100, buffer);

        Assert.Equal(100, read);
    }

    [Fact]
    public async Task ReadingDoesNotModifyTheSourceFile()
    {
        var hashBefore = SHA256.HashData(File.ReadAllBytes(imagePath));
        var writeTimeBefore = File.GetLastWriteTimeUtc(imagePath);

        using (var source = new ImageFileBlockSource(imagePath))
        {
            var buffer = new byte[4096];

            for (long offset = 0; offset < source.Length; offset += buffer.Length)
            {
                await source.ReadAsync(offset, buffer);
            }
        }

        var hashAfter = SHA256.HashData(File.ReadAllBytes(imagePath));

        Assert.Equal(hashBefore, hashAfter);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(imagePath));
    }

    [Fact]
    public void OpenSourceRejectsWriteAccessFromOthers()
    {
        using var source = new ImageFileBlockSource(imagePath);

        Assert.Throws<IOException>(() => File.OpenWrite(imagePath));
    }

    [Fact]
    public void InvalidSectorSizeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFileBlockSource(imagePath, 1000));
    }

    [Fact]
    public async Task ReadAfterDisposeThrows()
    {
        var source = new ImageFileBlockSource(imagePath);
        source.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await source.ReadAsync(0, new byte[16]));
    }

    public void Dispose()
    {
        if (File.Exists(imagePath))
        {
            File.Delete(imagePath);
        }
    }
}

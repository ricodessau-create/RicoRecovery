using RicoRecovery.Storage;

namespace RicoRecovery.Recovery.Imaging;

public static class ImageOpener
{
    public static async Task<ImageFileBlockSource> OpenAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var fullPath = Path.GetFullPath(imagePath);
        var state = await ImageStateStore.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);

        return new ImageFileBlockSource(fullPath, state?.SectorSize ?? 512);
    }
}

using System.Text.Json;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Imaging;

public static class ImageStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string StatePath(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        return imagePath + ".fortschritt";
    }

    public static async Task<ImageCreationState?> LoadAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var path = StatePath(imagePath);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<ImageCreationState>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);

            return state is { Version: ImageCreationState.CurrentVersion } ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(string imagePath, ImageCreationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = StatePath(imagePath);
        var temporary = path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, true);
    }
}

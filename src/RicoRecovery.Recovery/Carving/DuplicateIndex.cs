namespace RicoRecovery.Recovery.Carving;

public sealed class DuplicateIndex
{
    private readonly Dictionary<string, string> firstSeen = [];

    public int UniqueCount => firstSeen.Count;

    public string? Register(string hash, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(path);

        if (firstSeen.TryGetValue(hash, out var existing))
        {
            return existing;
        }

        firstSeen[hash] = path;

        return null;
    }
}

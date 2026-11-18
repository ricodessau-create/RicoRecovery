using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems;

public sealed class FileSystemRegistry
{
    private readonly List<IFileSystemAnalyzer> analyzers = [];

    public IReadOnlyCollection<FileSystemType> SupportedTypes =>
        analyzers.Select(analyzer => analyzer.Type).ToArray();

    public void Register(IFileSystemAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        if (analyzers.Any(existing => existing.Type == analyzer.Type))
        {
            throw new InvalidOperationException($"Für {analyzer.Type} ist bereits ein Analysemodul registriert.");
        }

        analyzers.Add(analyzer);
    }

    public async Task<IFileSystemAnalyzer?> DetectAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var analyzer in analyzers)
        {
            if (await analyzer.ProbeAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false))
            {
                return analyzer;
            }
        }

        return null;
    }
}

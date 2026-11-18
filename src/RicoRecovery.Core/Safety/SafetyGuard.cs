namespace RicoRecovery.Core.Safety;

public static class SafetyGuard
{
    public static SafetyAssessment EvaluateDestination(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
        var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationPath));

        if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            return SafetyAssessment.Unsafe(
                "Quell- und Ziellaufwerk sind identisch. Wiederhergestellte Dateien dürfen nicht auf dem Quelldatenträger gespeichert werden, da sie noch nicht gesicherte Daten überschreiben könnten.");
        }

        return SafetyAssessment.Safe;
    }
}

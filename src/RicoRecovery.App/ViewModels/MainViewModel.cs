using System.Reflection;

namespace RicoRecovery.App.ViewModels;

public sealed class MainViewModel
{
    public string ProductName => "RicoRecovery";

    public string VersionText =>
        $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";

    public string Tagline => "Datenrettung für Windows 10 und Windows 11";

    public IReadOnlyList<string> SafetyPrinciples { get; } =
    [
        "Originaldatenträger werden ausschließlich lesend geöffnet.",
        "Wiederhergestellte Dateien werden niemals auf dem Quelldatenträger gespeichert.",
        "Bei SSDs können TRIM und Garbage Collection gelöschte Daten bereits unwiederbringlich entfernt haben.",
        "Eine erfolgreiche Wiederherstellung wird nie garantiert."
    ];
}

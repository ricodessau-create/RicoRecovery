namespace RicoRecovery.Core.Models;

public sealed record CarvedFile(
    long Offset,
    long Length,
    FileSignature Signature,
    string Extension,
    IntegrityStatus Integrity,
    string? Note)
{
    private static readonly Dictionary<string, FileCategory> Categories = BuildCategories();

    public FileCategory Category => Categories.TryGetValue(Extension, out var category) ? category : Signature.Category;

    public FileSystemEntry ToEntry()
    {
        var name = $"Datei_{Offset:X12}.{Extension}";
        var description = $"Carving ({Signature.Name}), Integrität: {Describe(Integrity)}.";

        return new FileSystemEntry(
            name,
            $"/(Carving)/{Category}/{name}",
            Length,
            false,
            false,
            null,
            FileSystemType.Unknown,
            DataAvailability.Unknown,
            [new DataRun(Offset, Length, false)],
            null,
            Note is null ? description : description + " " + Note);
    }

    private static string Describe(IntegrityStatus integrity) => integrity switch
    {
        IntegrityStatus.Valid => "Struktur vollständig geprüft",
        IntegrityStatus.Plausible => "Struktur plausibel, Inhalt nicht geprüft",
        IntegrityStatus.Truncated => "Dateiende nicht gefunden, vermutlich unvollständig",
        IntegrityStatus.Damaged => "Prüfsummen- oder Strukturfehler",
        _ => "Dateigröße nur geschätzt"
    };

    private static Dictionary<string, FileCategory> BuildCategories()
    {
        var map = new Dictionary<string, FileCategory>();

        Add(map, FileCategory.Image, "jpg", "png", "gif", "bmp", "tif", "cr2", "raf");
        Add(map, FileCategory.Video, "mp4", "mov", "m4v", "3gp", "avi", "mkv", "webm", "mpg");
        Add(map, FileCategory.Audio, "mp3", "wav", "flac", "aac", "ogg", "m4a");
        Add(map, FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "rtf", "txt");
        Add(map, FileCategory.Archive, "zip", "rar", "7z", "tar", "apk");
        Add(map, FileCategory.Database, "sqlite");

        return map;
    }

    private static void Add(Dictionary<string, FileCategory> map, FileCategory category, params string[] extensions)
    {
        foreach (var extension in extensions)
        {
            map[extension] = category;
        }
    }
}

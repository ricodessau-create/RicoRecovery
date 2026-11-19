using System.Text;

namespace RicoRecovery.FileSystems;

internal static class EntryNames
{
    private const string InvalidCharacters = "\"*/:<>?\\|";

    public static string Clean(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            builder.Append(char.IsControl(character) || InvalidCharacters.Contains(character) ? '_' : character);
        }

        var cleaned = builder.ToString().Trim();

        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            return "_";
        }

        return cleaned;
    }
}

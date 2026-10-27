using System.Text;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

/// <summary>Portable Windows folder-leaf rules shared by subscription storage and downloads.</summary>
public static class SubscriptionNames
{
    private static readonly Regex DeviceName = new(
        @"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:[ .]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Clean(string? value)
    {
        var text = new StringBuilder();
        for (int index = 0; index < (value?.Length ?? 0); index++)
        {
            char character = value![index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    text.Append(character).Append(value[++index]);
                else text.Append('_');
            }
            else if (char.IsLowSurrogate(character) || char.IsControl(character) || "<>:\"/\\|?*".Contains(character))
                text.Append('_');
            else text.Append(character);
        }
        string name = Regex.Replace(text.ToString(), @"\s+", " ").Trim(' ', '.');
        if (DeviceName.IsMatch(name)) name = "_" + name;
        if (name.Length > 80)
            name = name[..(char.IsHighSurrogate(name[79]) ? 79 : 80)].TrimEnd(' ', '.');
        return name;
    }

    public static string Normalize(string? value, string fallback = "Подписка")
    {
        string name = Clean(value);
        if (name.Length == 0) name = Clean(fallback);
        return name.Length > 0 ? name : "Подписка";
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Equals(Clean(value), StringComparison.Ordinal);

    public static string ResolveOutputDirectory(string baseDirectory, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(folder);
        string root = System.IO.Path.GetFullPath(baseDirectory);
        if (folder.Length == 0) return root;
        if (!IsValid(folder)) throw new InvalidDataException("Краткое имя подписки должно быть безопасным именем одной папки.");
        return System.IO.Path.Combine(root, folder);
    }
}

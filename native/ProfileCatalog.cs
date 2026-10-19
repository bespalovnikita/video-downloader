namespace VideoDownloader.Native;

/// <summary>Russian UI captions with stable English IDs for v1 session compatibility.</summary>
public static class ProfileCatalog
{
    private static readonly (string Id, string Caption)[] Items =
    [
        ("Universal", "Универсальный"),
        ("4K archive", "Архив 4K"),
        ("MP4 compatibility", "Совместимый MP4"),
        ("Music MP3", "Музыка MP3"),
        ("Subtitles archive", "Архив с субтитрами")
    ];

    public static string[] DisplayNames => Items.Select(p => p.Caption).ToArray();

    public static string Display(string? value)
    {
        foreach (var item in Items)
            if (string.Equals(value, item.Id, StringComparison.Ordinal) ||
                string.Equals(value, item.Caption, StringComparison.Ordinal))
                return item.Caption;
        return Items[0].Caption;
    }

    public static string ToId(string? value)
    {
        foreach (var item in Items)
            if (string.Equals(value, item.Id, StringComparison.Ordinal) ||
                string.Equals(value, item.Caption, StringComparison.Ordinal))
                return item.Id;
        return Items[0].Id;
    }
}

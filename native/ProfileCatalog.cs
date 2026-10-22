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

    public static void Apply(DownloadOptions options,string name)
    {
        string id=ToId(name);
        if (name!=id && name!=Display(id)) return;
        options.Profile=id;
        options.Quality="Best";options.Codec="Auto";options.Container="Auto";
        options.AudioOnly=false;options.Archive=false;
        options.WriteSubs=false;options.AutoSubs=false;options.EmbedSubs=false;
        options.EmbedThumbnail=false;options.EmbedMetadata=true;options.EmbedChapters=true;
        switch(id)
        {
            case "4K archive":
                options.Quality="2160";options.Container="MKV";
                options.Archive=true;options.EmbedThumbnail=true;
                break;
            case "MP4 compatibility":
                options.Quality="1080";options.Codec="H264";options.Container="MP4";
                options.EmbedThumbnail=true;
                break;
            case "Music MP3":
                options.AudioOnly=true;options.EmbedThumbnail=true;
                break;
            case "Subtitles archive":
                options.Container="MKV";options.WriteSubs=true;options.AutoSubs=true;options.EmbedSubs=true;
                break;
        }
    }
}

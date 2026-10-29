namespace VideoDownloader.Native;

public enum JournalProfile { Errors, Standard, Progress, Detailed }
public enum JournalLevel { Error, Warning, Information, Progress, Verbose }

/// <summary>Immutable worker event. Identifiers are separate from display text and URL secrets.</summary>
public sealed record JournalRecord(JournalLevel Level,string Category,string Message,
    string Session="",string Job="",string Worker="");

public static class JournalProfileCatalog
{
    public static string[] DisplayNames => ["Только ошибки","Обычный","Прогресс загрузок","Подробный yt-dlp"];
    public static JournalProfile Parse(string? value) => value switch
    {
        "Errors" or "Только ошибки" => JournalProfile.Errors,
        "Progress" or "Прогресс загрузок" => JournalProfile.Progress,
        "Detailed" or "Подробный yt-dlp" => JournalProfile.Detailed,
        _ => JournalProfile.Standard
    };
    public static string ToId(JournalProfile value) => value switch
    {
        JournalProfile.Errors => "Errors",JournalProfile.Progress => "Progress",
        JournalProfile.Detailed => "Detailed",_ => "Standard"
    };
    public static string Display(string? value) => Display(Parse(value));
    public static string Display(JournalProfile value) => value switch
    {
        JournalProfile.Errors => "Только ошибки",JournalProfile.Progress => "Прогресс загрузок",
        JournalProfile.Detailed => "Подробный yt-dlp",_ => "Обычный"
    };
}

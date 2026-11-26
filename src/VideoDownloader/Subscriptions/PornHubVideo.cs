using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal static class PornHubVideo
{
    internal const string ProfileExtractor = "videodownloader:pornhub:profiles";
    private const string Site = @"pornhub(?:premium)?\.(?:com|net|org)";
    private const string FileId = @"(?:(?:ph)?[0-9a-f]{13}|[0-9]{6,12})";
    private static readonly Regex Host = new(@"\A(?:[a-z0-9-]+\.)?(" + Site + @")\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BareId = new(@"\A" + FileId + @"\z", RegexOptions.CultureInvariant);
    private static readonly Regex BracketedId = new(@"\[(" + FileId + @")\](?:\.[a-zA-Z0-9]{1,8})?\z", RegexOptions.CultureInvariant);
    private static readonly Regex ViewKey = new(@"\A[a-z0-9]{1,64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ProfilePath = new(@"\A/(?:model|pornstar)/[a-zA-Z0-9_-]{1,128}\z", RegexOptions.CultureInvariant);

    internal static string? WatchUrl(string input)
    {
        string value = input.Trim().Trim('"');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var host = Host.Match(uri.Host);
            if (!host.Success || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                uri.AbsolutePath is not ("/view_video.php" or "/video/show")) return null;
            var keys = uri.Query.TrimStart('?').Split('&').Where(part => part.Split('=', 2)[0] == "viewkey").ToArray();
            if (keys.Length != 1 || !keys[0].StartsWith("viewkey=", StringComparison.Ordinal)) return null;
            string key = keys[0]["viewkey=".Length..];
            return ViewKey.IsMatch(key) ? BuildWatchUrl(host.Groups[1].Value.ToLowerInvariant(), key) : null;
        }
        if (value.Contains("://", StringComparison.Ordinal)) return null;
        string id = BareId.IsMatch(value) ? value : BracketedId.Match(value).Groups[1].Value;
        return id.Length > 0 ? BuildWatchUrl("pornhub.com", id) : null;
    }

    private static string BuildWatchUrl(string site, string id) => $"https://www.{site}/view_video.php?viewkey={id}";

    internal static string[] ParseProfiles(string json, string watchUrl)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var video = new Uri(watchUrl);
        const string invalid = "Некорректный ответ поиска моделей Pornhub.";
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() != video.Query["?viewkey=".Length..] ||
            !root.TryGetProperty("vd_ph_profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array ||
            profiles.GetArrayLength() is < 1 or > 50)
            throw new InvalidDataException(invalid);
        string origin = $"https://{video.Host}";
        var urls = new List<string>();
        foreach (var profile in profiles.EnumerateArray())
        {
            string? url = profile.ValueKind == JsonValueKind.String ? profile.GetString() : null;
            if (url is null || !url.StartsWith(origin, StringComparison.Ordinal) ||
                !ProfilePath.IsMatch(url[origin.Length..])) throw new InvalidDataException(invalid);
            urls.Add(url);
        }
        return urls.Distinct(StringComparer.Ordinal).ToArray();
    }
}

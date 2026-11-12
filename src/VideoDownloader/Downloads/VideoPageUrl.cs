namespace VideoDownloader.Native;

// Only an extractor's public webpage_url may become a material link. Never
// substitute format.url, manifests or the private headers used to fetch media.
internal static class VideoPageUrl
{
    internal static string FromExtractor(string? value,string? extractor)
    {
        // Generic/direct extractors can call a signed media path webpage_url.
        // Without a site extractor there is no evidence of a public video page.
        if(string.IsNullOrWhiteSpace(extractor) || extractor.StartsWith("generic",StringComparison.OrdinalIgnoreCase) ||
            extractor.StartsWith("html5",StringComparison.OrdinalIgnoreCase) || extractor.Equals("direct",StringComparison.OrdinalIgnoreCase))return "";
        return PublicOrEmpty(value);
    }
    internal static string PublicOrEmpty(string? value)=>value is {Length:>0 and <=4096} &&
        value==value.Trim() && !value.Any(character=>char.IsWhiteSpace(character) || char.IsControl(character)) &&
        Uri.TryCreate(value,UriKind.Absolute,out var address) && address.IsWellFormedOriginalString() &&
        address.Scheme is "http" or "https" && address.Host.Length>0 && address.UserInfo.Length==0 &&
        string.Equals(JournalSanitizer.Redact(value),value,StringComparison.Ordinal)?value:"";
}

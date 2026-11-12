using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

// Only explicit watch-page preview requests select the packaged adapter. Its
// routing decision never changes the URL used for queue or cache identity.
internal static class PornHubPreview
{
    // Match the original text, not Uri's case/escape normalization: the
    // explicitly selected upstream IE must also consider this URL suitable.
    private static readonly Regex WatchPage=new(@"^https?://(?:[a-zA-Z0-9-]+\.)?pornhub(?:premium)?\.(?:com|net|org)/view_video\.php\?viewkey=[a-z0-9]+(?:[&#]|$)",RegexOptions.CultureInvariant);
    internal static bool Supports(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var source) &&
        source.Scheme is "http" or "https" && source.UserInfo.Length==0 && source.IsDefaultPort &&
        WatchPage.IsMatch(url);

    internal static void AddArguments(System.Diagnostics.ProcessStartInfo start,string url)
    {
        if(!Supports(url))return;
        string file=Path.Combine(PornHubChannel.PluginDirectory,"videodownloader","yt_dlp_plugins","extractor","vd_pornhub_preview.py");
        if(!File.Exists(file))throw new FileNotFoundException("Не найден обработчик превью Pornhub. Распакуйте полный архив приложения.",file);
        foreach(string argument in new[]{"--no-plugin-dirs","--plugin-dirs",PornHubChannel.PluginDirectory,
            "--use-extractors","videodownloader:pornhub:preview"})start.ArgumentList.Add(argument);
    }
}

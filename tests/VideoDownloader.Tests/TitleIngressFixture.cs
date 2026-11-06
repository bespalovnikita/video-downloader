using System.IO;
using System.Text;
using System.Text.Json;

internal static class TitleIngressFixture
{
    // This test host becomes a metadata-only yt-dlp fixture only in children
    // of the isolated cold-ingress WPF scenario. No shell or PowerShell is used.
    public static bool TryRunFixture(string[] args)
    {
        string? log=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        if(string.IsNullOrWhiteSpace(log)) return false;
        string url=args.LastOrDefault()??"";
        bool metadataOnly=new[]{"--ignore-config","--dump-single-json","--skip-download"}
            .All(required=>args.Contains(required,StringComparer.Ordinal));
        File.AppendAllText(log,JsonSerializer.Serialize(new {
            Url=url,Arguments=args,ProcessId=Environment.ProcessId,MetadataOnly=metadataOnly
        })+Environment.NewLine,Encoding.UTF8);
        var titles=new Dictionary<string,string>(StringComparer.Ordinal) {
            ["https://title-ingress.invalid/Video?token=AbC"]="Название из URL",
            ["https://title-ingress.invalid/video?token=AbC"]="Название из файла",
            ["https://title-ingress.invalid/Shortcut?token=XyZ"]="Название из ярлыка",
            ["https://title-ingress.invalid/Subscription?token=AbC"]="Название из подписки",
            ["https://title-ingress.invalid/SubscriptionPreview?token=AbC"]="Название успешной подписки"
        };
        Console.OutputEncoding=new UTF8Encoding(false);
        if(!metadataOnly || !titles.TryGetValue(url,out string? title))
        {
            Console.Error.WriteLine("Unexpected non-metadata title fixture invocation.");
            Environment.ExitCode=2;
        }
        else if(url=="https://title-ingress.invalid/Subscription?token=AbC" &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(log)!,"subscription-recovered")))
        {
            Console.Error.WriteLine("Cold subscription fixture metadata unavailable");
            Environment.ExitCode=7;
        }
        else
        {
            bool alternate=url=="https://title-ingress.invalid/video?token=AbC";
            object[] formats=alternate
                ? [new {format_id="248",ext="webm",width=1920,height=1080,fps=60,vcodec="vp9",acodec="none",dynamic_range="HDR10",filesize_approx=4096L},
                   new {format_id="251",ext="webm",vcodec="none",acodec="opus",language="en"}]
                : [new {format_id="137",ext="mp4",width=1920,height=1080,fps=30,vcodec="avc1.640028",acodec="none",dynamic_range="SDR",filesize=2048L},
                   new {format_id="140",ext="m4a",vcodec="none",acodec="mp4a.40.2",language="ru"},
                   new {format_id="22",ext="mp4",width=1280,height=720,fps=30,vcodec="avc1.64001f",acodec="mp4a.40.2",filesize_approx=8192L}];
            Console.WriteLine(JsonSerializer.Serialize(new {
                title,uploader="Cold metadata fixture",duration=42,formats,
                thumbnail=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_THUMBNAIL")??""
            }));
        }
        return true;
    }
}

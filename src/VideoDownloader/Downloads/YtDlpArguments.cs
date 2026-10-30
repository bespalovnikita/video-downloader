using System.Globalization;

namespace VideoDownloader.Native;

/// <summary>Builds the request passed to a download child, including quality fallbacks.</summary>
internal sealed class YtDlpArguments(DownloadOptions options)
{
    internal IEnumerable<string> QualityLevels() => options.Quality switch
    {
        "2160"=>["2160","1440","1080","720"],
        "1440"=>["1440","1080","720"],
        "1080"=>["1080","720"],
        "720"=>["720"],
        _=>["best"]
    };

    private string FormatSelector(string quality)
    {
        string code=options.Codec switch {"H264"=>"avc1","VP9"=>"vp9","AV1"=>"av01",_=>""};
        string filter=quality=="best" ? "" : $"[height<={quality}]";
        if (code.Length==0) return $"bv*{filter}+ba/b{filter}";
        return $"bv*{filter}[vcodec^={code}]+ba/b{filter}[vcodec^={code}]/bv*{filter}+ba/b{filter}";
    }

    internal IEnumerable<string> Build(string url,string stage,string quality)
    {
        string[] standard=["--ignore-config","--encoding","utf-8","--newline","--progress","--no-color","--no-quiet",
            "--impersonate","chrome","-N",options.FragmentCount.ToString(CultureInfo.InvariantCulture),
            "-o",Path.Combine(stage,options.FilenameTemplate),
            "--progress-template","download:__VD_PROGRESS__|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(info.id)s:%(info.format_id)s|%(info.vcodec)s|%(info.acodec)s",
            "--print","before_dl:__VD_TITLE__:%(title)s",
            // Keep only IDs/counts: a complete requested_formats dump includes
            // signed media URLs and is unsuitable for diagnostic retention.
            "--print","before_dl:__VD_PLAN__:%(.{id,format_id,requested_formats.:.format_id,playlist_autonumber,n_entries})j",
            "--print","after_move:__VD_FILE__:%(filepath)s"];
        var args=new List<string>(standard);
        string ffmpeg=DependencyCenter.Executable("ffmpeg.exe");
        if(File.Exists(ffmpeg)) args.AddRange(["--ffmpeg-location",ffmpeg]);
        // Explicitly enable file:// only for local offline integration fixtures;
        // GUI URL validation still restricts user-entered links to HTTP/HTTPS.
        if (Uri.TryCreate(url,UriKind.Absolute,out var source) && source.Scheme=="file")
            args.Add("--enable-file-urls");
        if (File.Exists(options.Cookies)) args.AddRange(["--cookies",options.Cookies]);
        if (options.RateLimit.Length>0 && options.RateLimit!="Unlimited") args.AddRange(["--limit-rate",options.RateLimit]);
        if (options.Sponsor) args.AddRange(["--sponsorblock-remove","sponsor"]);
        if (options.AudioOnly) args.AddRange(["-x","--audio-format","mp3"]);
        else
        {
            args.AddRange(["-f",FormatSelector(quality)]);
            if (options.Container!="Auto") args.AddRange(["--merge-output-format",options.Container.ToLowerInvariant(),
                "--remux-video",options.Container.ToLowerInvariant()]);
        }
        if (options.WriteSubs || options.EmbedSubs) args.Add("--write-subs");
        if (options.WriteSubs || options.EmbedSubs || options.AutoSubs)
            args.AddRange(["--sub-langs",options.SubtitleLangs]);
        if (options.AutoSubs) args.Add("--write-auto-subs");
        if (options.EmbedSubs) args.Add("--embed-subs");
        if (options.EmbedThumbnail) args.AddRange(["--write-thumbnail","--embed-thumbnail"]);
        if (options.EmbedMetadata) args.Add("--embed-metadata");
        if (options.EmbedChapters) args.Add("--embed-chapters");
        args.Add(url);
        return args;
    }
}

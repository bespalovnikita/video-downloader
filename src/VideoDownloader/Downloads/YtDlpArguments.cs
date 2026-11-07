using System.Globalization;

namespace VideoDownloader.Native;

/// <summary>Builds the request passed to a download child, including quality fallbacks.</summary>
internal sealed class YtDlpArguments(DownloadOptions options)
{
    internal IEnumerable<string> QualityLevels(DownloadSelection? selection=null) => selection?.Format is not null ? ["manual"] : options.Quality switch
    {
        "2160"=>["2160","1440","1080","720"],
        "1440"=>["1440","1080","720"],
        "1080"=>["1080","720"],
        "720"=>["720"],
        _=>["best"]
    };

    internal IEnumerable<string> Build(string url,string stage,string quality,DownloadSelection? selection=null)
    {
        selection?.Validate(url,options);
        string? format=selection?.FormatSelector??AudioTrackSelection.FormatSelector(options,quality);
        string[] standard=["--ignore-config","--encoding","utf-8","--newline","--progress","--no-color","--no-quiet",
            "--impersonate","chrome","-N",options.FragmentCount.ToString(CultureInfo.InvariantCulture),
            "-o",Path.Combine(stage,OutputTemplate(selection)),
            "--progress-template","download:__VD_PROGRESS__|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(info.id)s:%(info.format_id)s|%(info.vcodec)s|%(info.acodec)s"+
                (selection?.EffectiveSections.Count is >1?"|%(info.section_start)s|%(info.section_end)s":""),
            "--print","before_dl:__VD_TITLE__:%(title)s",
            // Keep only IDs/counts: a complete requested_formats dump includes
            // signed media URLs and is unsuitable for diagnostic retention.
            "--print",selection?.EffectiveSections.Count is >1?
                "before_dl:__VD_PLAN__:%(.{id,format_id,requested_formats.:.format_id,playlist_autonumber,n_entries,section_start,section_end})j":
                "before_dl:__VD_PLAN__:%(.{id,format_id,requested_formats.:.format_id,playlist_autonumber,n_entries})j",
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
        if(selection?.IsPartial==true)
        {
            foreach(var section in selection.EffectiveSections)args.AddRange(["--download-sections",section.Argument]);
            args.Add("--force-keyframes-at-cuts");
        }
        if (format!=null) args.AddRange(["-f",format]);
        if (options.AudioOnly) args.AddRange(["-x","--audio-format","mp3"]);
        else
        {
            if(AudioTrackSelection.Multiple(options))args.Add("--audio-multistreams");
            string container=AudioTrackSelection.Container(options);
            if (container!="Auto") args.AddRange(["--merge-output-format",container.ToLowerInvariant(),
                "--remux-video",container.ToLowerInvariant()]);
        }
        if (options.WriteSubs || options.EmbedSubs) args.Add("--write-subs");
        if (options.WriteSubs || options.EmbedSubs || options.AutoSubs)
            args.AddRange(["--sub-langs",options.SubtitleLangs]);
        if (options.AutoSubs) args.Add("--write-auto-subs");
        if(selection?.IsPartial==true && (options.WriteSubs || options.AutoSubs || options.EmbedSubs))
        {
            // yt-dlp cuts AV, but leaves subtitle timestamps on the source
            // timeline. The coordinator clips these owned sidecars before
            // embedding. Project only keys/paths/extensions, never signed URLs.
            args.AddRange(["--sub-format","vtt/srt/ass/ssa/best","--print",
                "after_move:__VD_SUBTITLES__:%(requested_subtitles)l|%(.{filepath,section_start,section_end,requested_subtitles.:.ext,requested_subtitles.:.filepath})j"]);
        }
        else if (options.EmbedSubs) args.Add("--embed-subs");
        if (options.EmbedThumbnail) args.AddRange(["--write-thumbnail","--embed-thumbnail"]);
        if (options.EmbedMetadata) args.Add("--embed-metadata");
        if (options.EmbedChapters) args.Add("--embed-chapters");
        args.Add(url);
        return args;
    }
    private string OutputTemplate(DownloadSelection? selection)
    {
        if(selection?.EffectiveSections.Count is not >1)return options.FilenameTemplate;
        // Numeric ranges have no section_number in yt-dlp. A millisecond prefix
        // gives each output and its sidecars a unique name without changing a
        // user's filename template or allowing a later range to reuse a file.
        string prefix="fragment-%(section_start*1000+0.5)013d-%(section_end*1000+0.5)013dms ";
        return Path.Combine(Path.GetDirectoryName(options.FilenameTemplate)??"",prefix+Path.GetFileName(options.FilenameTemplate));
    }
}

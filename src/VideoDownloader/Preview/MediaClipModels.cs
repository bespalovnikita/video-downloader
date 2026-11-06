namespace VideoDownloader.Native;

internal sealed record MediaClipRequest(string Url,double StartSeconds,double EndSeconds,string? VideoFormatId=null,
    string? AudioFormatId=null,double? DurationSeconds=null,bool FromEnd=false,bool AudioOnly=false,
    string AudioTrack="Default",string AudioLanguages="");

internal sealed record MediaClipSources(MediaFrameSource? Video,MediaFrameSource? Audio);

internal sealed record MediaClipResult(string Url,double RequestedStart,double RequestedEnd,MediaClipPreview? Clip,string Error)
{
    internal bool Success=>Clip is not null;
}

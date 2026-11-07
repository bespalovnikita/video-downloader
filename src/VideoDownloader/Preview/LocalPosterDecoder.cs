using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native;

// A checkpoint's published ownership manifest is required. A history path,
// arbitrary existing file, or downloader staging path never grants access.
internal sealed record LocalPosterInput(string Url,string Path,string OutputRoot,string[] OwnedPaths,string Title)
{
    private static readonly StringComparer Paths=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
    internal static LocalPosterInput? Capture(DownloadItem row)
    {
        if(row.Status!="Completed" || !row.AttemptFinished || row.PendingRemoval || row.CleanupError.Length!=0 ||
            row.CleanupPaths.Count!=0 || row.StageDir.Length!=0 || !AppPersistence.ValidUrl(row.Url) ||
            string.IsNullOrWhiteSpace(row.Path) || string.IsNullOrWhiteSpace(row.OutputRoot) ||
            !row.OwnedPaths.Contains(row.Path,Paths) || Demuxer(row.Path) is null)return null;
        return new(row.Url,row.Path,row.OutputRoot,row.OwnedPaths.ToArray(),row.Title);
    }
    internal bool Matches(DownloadItem row)=>Capture(row) is {} current && Url==current.Url &&
        Paths.Equals(Path,current.Path) && Paths.Equals(OutputRoot,current.OutputRoot) && OwnedPaths.SequenceEqual(current.OwnedPaths,Paths);
    internal static string? Demuxer(string path)=>System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".mov" or ".3gp" or ".3g2"=>"mov",
        ".mkv" or ".webm"=>"matroska",
        ".avi"=>"avi", ".wmv" or ".asf"=>"asf", ".ts" or ".m2ts" or ".mts"=>"mpegts",
        ".mpg" or ".mpeg" or ".vob"=>"mpeg", ".flv"=>"flv", ".ogv"=>"ogg", _=>null
    };
}

internal sealed record LocalPosterFrame(byte[] Bytes,string Fingerprint,FileStream? ReadLease=null):IDisposable
{
    public void Dispose()=>ReadLease?.Dispose();
}

/// <summary>Reads only a verified published regular video; owns every native child until joined.</summary>
internal sealed class LocalPosterDecoder
{
    internal static readonly TimeSpan Timeout=TimeSpan.FromSeconds(20);
    internal const int MaxProbeBytes=128*1024;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly TimeSpan _timeout;
    internal LocalPosterDecoder(string ffmpeg,string? ffprobe=null,TimeSpan? timeout=null)
    {
        _ffmpeg=ffmpeg;
        _ffprobe=ffprobe??System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ffmpeg)!,OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
        _timeout=timeout??Timeout;
        if(_timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    internal static string Fingerprint(LocalPosterInput input)
    {
        string path=System.IO.Path.GetFullPath(input.Path);
        if(!System.IO.Path.IsPathFullyQualified(input.Path) || !System.IO.Path.IsPathFullyQualified(input.OutputRoot) ||
            !OwnedFiles.Inside(input.OutputRoot,path) || !input.OwnedPaths.Contains(input.Path,
                OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal) || LocalPosterInput.Demuxer(path) is null)
            throw new IOException("Local preview requires a published owned video.");
        OwnedFiles.RegularOrAbsent(path);
        var file=new FileInfo(path);
        if(!file.Exists || file.Length<=0)throw new IOException("Published video is unavailable.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path+"\n"+
            file.Length.ToString(CultureInfo.InvariantCulture)+"\n"+file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))));
    }
    internal async Task<LocalPosterFrame?> DecodeAsync(LocalPosterInput input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string stamp=Fingerprint(input);
        if(!File.Exists(_ffmpeg) || !File.Exists(_ffprobe))return null;
        // Windows denies replacement, deletion and writes for this brief read
        // lease. The UI owner cancels/joins it before removing published media.
        // Keep it through dispatcher admission, not just through ffmpeg exit.
        var lease=new FileStream(input.Path,FileMode.Open,FileAccess.Read,FileShare.Read,1,FileOptions.None);
        try
        {
            if(Fingerprint(input)!=stamp){lease.Dispose();return null;}
            var frame=await DecodeOpenedAsync(input,stamp,token).ConfigureAwait(false);
            if(frame is null){lease.Dispose();return null;}
            return frame with{ReadLease=lease};
        }
        catch{lease.Dispose();throw;}
    }
    private async Task<LocalPosterFrame?> DecodeOpenedAsync(LocalPosterInput input,string stamp,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        string demuxer=LocalPosterInput.Demuxer(input.Path)!;
        var probe=Start(_ffprobe,demuxer);
        foreach(string argument in new[]{"-i",input.Path,"-select_streams","v","-show_entries",
            "stream=index,width,height:stream_disposition=attached_pic:format=duration","-of","json"})probe.ArgumentList.Add(argument);
        var metadata=await Run(probe,MaxProbeBytes,deadline.Token).ConfigureAwait(false);
        if(metadata is null)return null;
        using var document=JsonDocument.Parse(metadata);
        if(!document.RootElement.TryGetProperty("streams",out var streams) || streams.ValueKind!=JsonValueKind.Array)return null;
        int stream=-1;
        foreach(var video in streams.EnumerateArray())
        {
            if(video.TryGetProperty("disposition",out var disposition) && disposition.TryGetProperty("attached_pic",out var cover) && cover.GetInt32()!=0)continue;
            if(!video.TryGetProperty("width",out var width) || !video.TryGetProperty("height",out var height) ||
                width.GetInt32()<=0 || height.GetInt32()<=0 || (long)width.GetInt32()*height.GetInt32()>16_777_216)continue;
            stream=video.GetProperty("index").GetInt32();break;
        }
        if(stream<0)return null;
        double seconds=0;
        if(document.RootElement.TryGetProperty("format",out var format) && format.TryGetProperty("duration",out var duration) &&
            double.TryParse(duration.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double length) && double.IsFinite(length) && length>0)
            seconds=Math.Min(3,length*.1);
        foreach(double seek in seconds>0?new[]{seconds,0}:new[]{0d})
        {
            deadline.Token.ThrowIfCancellationRequested();
            if(Fingerprint(input)!=stamp)return null;
            var decode=Start(_ffmpeg,demuxer);
            foreach(string argument in new[]{"-ss",seek.ToString("0.######",CultureInfo.InvariantCulture),"-i",input.Path,
                "-map","0:"+stream.ToString(CultureInfo.InvariantCulture),"-frames:v","1","-an","-sn","-dn","-vf",
                "scale='min(640,iw)':'min(360,ih)':force_original_aspect_ratio=decrease","-c:v","png","-threads","1","-f","image2pipe","pipe:1"})
                decode.ArgumentList.Add(argument);
            var bytes=await Run(decode,MediaFrameService.MaxFrameBytes,deadline.Token).ConfigureAwait(false);
            if(bytes is not null && MediaFrameService.IsBoundedPng(bytes) && Fingerprint(input)==stamp)
                return new(bytes,stamp);
        }
        return null;
    }
    private static ProcessStartInfo Start(string executable,string demuxer)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        // Fixed demuxers prohibit playlists even when a manifest is renamed to
        // .mp4. MOV references cannot open adjacent files. No network protocols.
        foreach(string argument in new[]{"-hide_banner","-loglevel","error","-max_alloc","67108864","-threads","2",
            "-protocol_whitelist","file","-format_whitelist",demuxer,"-probesize","2097152","-analyzeduration","3000000",
            "-max_pixels","16777216","-f",demuxer})start.ArgumentList.Add(argument);
        if(System.IO.Path.GetFileNameWithoutExtension(executable).Equals("ffmpeg",StringComparison.OrdinalIgnoreCase))
        {start.ArgumentList.Add("-nostdin");start.ArgumentList.Add("-filter_threads");start.ArgumentList.Add("1");}
        if(demuxer=="mov")
            foreach(string argument in new[]{"-enable_drefs","0","-use_absolute_path","0"})start.ArgumentList.Add(argument);
        return start;
    }
    private static async Task<byte[]?> Run(ProcessStartInfo start,int bound,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var child=ManagedChildProcess.Start(start);
        var output=await BoundedProcessOutput.ReadAsync(child,bound,MediaFrameService.MaxErrorBytes,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return child.Process.ExitCode==0?output.StandardOutput:null;
    }
}

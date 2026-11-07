using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal sealed record SubtitleCue(long Start,long End,string Text);

/// <summary>Rebases local subtitle cues before the coordinator's existing publication boundary.</summary>
internal sealed partial class SubtitleTimelineService
{
    internal const int MaximumBytes=8*1024*1024;
    private const int MaximumTracks=32;
    private readonly string _ffmpeg,_ffprobe;
    private readonly Func<ProcessStartInfo,int,int,CancellationToken,Task<FragmentMergeToolOutput>> _run;
    private readonly Func<bool> _paused;
    private readonly TimeSpan _timeout;
    private sealed record Track(string Language,string Extension,string Path);
    private sealed record Manifest(string MediaPath,(double Start,double End) Bounds,Track[] Tracks);
    private sealed record Prepared(string Language,string Path,SubtitleCue[] Cues);

    internal SubtitleTimelineService(string ffmpeg,string ffprobe,
        Func<ProcessStartInfo,int,int,CancellationToken,Task<FragmentMergeToolOutput>>? run=null,
        Func<bool>? paused=null,TimeSpan? timeout=null)
    {_ffmpeg=ffmpeg;_ffprobe=ffprobe;_run=run??RunOwnedAsync;_paused=paused??(()=>false);_timeout=timeout??TimeSpan.FromMinutes(10);}

    internal async Task ProcessAsync(string stage,IReadOnlyList<string> media,IReadOnlyList<DownloadSection> sections,
        IReadOnlyList<string> markers,bool embed,CancellationToken token,IReadOnlyList<(double Start,double End)>? timelineBounds=null)
    {
        token.ThrowIfCancellationRequested();
        if(media.Count!=sections.Count || media.Count is <1 or >DownloadSectionSet.MaximumCount || markers.Count!=media.Count)
            throw Invalid("Missing or incomplete selected-fragment subtitle manifest.");
        timelineBounds??=sections.Select(section=>(section.StartSeconds,section.EndSeconds)).ToArray();
        if(timelineBounds.Count!=sections.Count)throw Invalid("Incomplete subtitle timeline bounds.");
        stage=Path.GetFullPath(stage);
        OwnedFiles.NoReparseAncestors(Path.Combine(stage,"check"));
        var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        var manifests=markers.Select(ParseManifest).ToArray();
        if(manifests.Select(value=>Path.GetFullPath(value.MediaPath)).Distinct(comparer).Count()!=media.Count)
            throw Invalid("Duplicate selected-fragment subtitle manifest.");
        for(int index=0;index<media.Count;index++)
        {
            LocalFile(stage,media[index]);sections[index].Validate();
            ValidateBounds(timelineBounds[index]);
            var manifest=manifests.SingleOrDefault(value=>comparer.Equals(Path.GetFullPath(value.MediaPath),Path.GetFullPath(media[index])))
                ??throw Invalid("Subtitle manifest does not identify a reported media file.");
            if(Math.Abs(manifest.Bounds.Start-timelineBounds[index].Start)>.000001 ||
                Math.Abs(manifest.Bounds.End-timelineBounds[index].End)>.000001)
                throw Invalid("Subtitle manifest does not match the selected timeline.");
            foreach(var track in manifest.Tracks)
            {
                LocalFile(stage,track.Path);
                if(media.Any(path=>comparer.Equals(Path.GetFullPath(path),Path.GetFullPath(track.Path))))throw Invalid("A subtitle path identifies media.");
                _=SubtitleDemuxer(track.Extension);
                if(new FileInfo(track.Path).Length>MaximumBytes)throw Invalid("Subtitle input exceeds the 8 MiB limit.");
                if(track.Extension.Equals("vtt",StringComparison.OrdinalIgnoreCase))
                {
                    using var file=File.OpenRead(track.Path);byte[] header=new byte[16];int count=file.Read(header);
                    string signature=Encoding.UTF8.GetString(header,0,count).TrimStart('\uFEFF');
                    if(!signature.StartsWith("WEBVTT",StringComparison.Ordinal) || signature.Length>6 && signature[6] is not (' ' or '\t' or '\r' or '\n'))
                        throw Invalid("The selected WebVTT file has an invalid header.");
                }
            }
        }
        if(manifests.All(value=>value.Tracks.Length==0))return;
        if(!File.Exists(_ffmpeg) || embed && !File.Exists(_ffprobe))throw Invalid("FFmpeg/FFprobe are required for clipped subtitles.");
        string work=Path.Combine(stage,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        await using var budget=new ActiveBudget(token,_timeout,_paused);
        try
        {
            var normalized=new Dictionary<string,SubtitleCue[]>(comparer);
            var replacements=new List<(string Source,string Destination)>();
            var originals=new HashSet<string>(comparer);
            var destinations=new HashSet<string>(comparer);
            long totalBytes=0,normalizedBytes=0;
            for(int index=0;index<media.Count;index++)
            {
                var manifest=manifests.Single(value=>comparer.Equals(Path.GetFullPath(value.MediaPath),Path.GetFullPath(media[index])));
                var prepared=new List<Prepared>();
                foreach(var track in manifest.Tracks)
                {
                    string source=Path.GetFullPath(track.Path);originals.Add(source);
                    if(!normalized.TryGetValue(source,out var cues))
                    {
                        var start=Start(_ffmpeg);Add(start,"-hide_banner","-loglevel","error","-nostdin");
                        Input(start,stage,source,SubtitleDemuxer(track.Extension));
                        Add(start,"-map","0:s:0","-c:s","srt","-f","srt","pipe:1");
                        var output=await RunAsync(start,MaximumBytes,budget.Token).ConfigureAwait(false);
                        totalBytes+=output.Length;normalizedBytes+=output.Length;
                        if(normalizedBytes>32L*1024*1024)throw Invalid("Subtitle batch exceeds the 32 MiB normalization limit.");
                        normalized[source]=cues=ParseSrt(output);
                    }
                    var clipped=ClipBounds(cues,timelineBounds[index]);
                    string target=Path.ChangeExtension(media[index],null)+"."+track.Language+".vtt";
                    if(!destinations.Add(Path.GetFullPath(target)))throw Invalid("Clipped subtitle filenames collide.");
                    string temporary=Path.Combine(work,Guid.NewGuid().ToString("N")+".vtt");
                    byte[] bytes=Encoding.UTF8.GetBytes(WebVtt(clipped));
                    totalBytes+=bytes.Length;if(totalBytes>64L*1024*1024)throw Invalid("Clipped subtitle batch exceeds the 64 MiB limit.");
                    await WriteNewAsync(temporary,bytes,budget.Token).ConfigureAwait(false);
                    replacements.Add((temporary,target));
                    // An empty VTT sidecar represents a selected language with no
                    // overlapping cue. Empty tracks cannot be muxed by FFmpeg.
                    if(embed && clipped.Length>0)
                    {
                        // mov_text cannot represent simultaneous cues. Split
                        // at every boundary and retain all visible dialogue.
                        var visible=Flatten(clipped);
                        string embedded=Path.Combine(work,Guid.NewGuid().ToString("N")+".vtt");
                        byte[] embeddedBytes=Encoding.UTF8.GetBytes(WebVtt(visible));totalBytes+=embeddedBytes.Length;
                        if(totalBytes>64L*1024*1024)throw Invalid("Clipped subtitle batch exceeds the 64 MiB limit.");
                        await WriteNewAsync(embedded,embeddedBytes,budget.Token).ConfigureAwait(false);
                        prepared.Add(new(track.Language,embedded,visible));
                    }
                }
                if(embed && manifest.Tracks.Length>0)
                {
                    string temporary=Path.Combine(work,Guid.NewGuid().ToString("N")+Path.GetExtension(media[index]));
                    await EmbedAsync(stage,media[index],temporary,prepared,budget.Token).ConfigureAwait(false);
                    await VerifyAsync(stage,media[index],temporary,prepared,timelineBounds[index],budget.Token).ConfigureAwait(false);
                    replacements.Add((temporary,media[index]));
                }
            }
            // All source timelines remain available until every range and every
            // embedded result has passed. Scratch never reaches OwnedFiles.Publish.
            foreach(var replacement in replacements)
            {
                budget.Token.ThrowIfCancellationRequested();
                OwnedFiles.RegularOrAbsent(replacement.Destination);
                if(File.Exists(replacement.Destination) && !originals.Contains(Path.GetFullPath(replacement.Destination)) &&
                    !media.Any(path=>comparer.Equals(Path.GetFullPath(path),Path.GetFullPath(replacement.Destination))))
                    throw Invalid("A clipped subtitle destination already exists.");
                File.Move(replacement.Source,replacement.Destination,true);
            }
            foreach(string original in originals)
                if(!destinations.Contains(original)){OwnedFiles.RegularOrAbsent(original);File.Delete(original);}
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested && budget.Expired)
        {throw Invalid("Clipped subtitle processing exceeded its active-time limit.");}
        finally {OwnedFiles.DeleteStage(work,stage);}
    }

    private static Manifest ParseManifest(string marker)
    {
        if(marker.Length>65536)throw Invalid("Subtitle manifest exceeds its limit.");
        int separator=marker.IndexOf('|');if(separator<0)throw Invalid("Invalid subtitle manifest.");
        try
        {
            using var document=JsonDocument.Parse(marker[(separator+1)..],new JsonDocumentOptions {MaxDepth=8});
            var root=document.RootElement;
            string path=root.GetProperty("filepath").GetString()??"";
            var bounds=(Start:root.GetProperty("section_start").GetDouble(),End:root.GetProperty("section_end").GetDouble());ValidateBounds(bounds);
            string languages=marker[..separator];
            if(languages is "NA" or "N/A" or "" && !root.TryGetProperty("requested_subtitles.:.filepath",out _))return new(path,bounds,[]);
            string[] names=languages.Split(", ",StringSplitOptions.None);
            if(names.Length>MaximumTracks || names.Distinct(StringComparer.Ordinal).Count()!=names.Length ||
                names.Any(name=>name.Length is <1 or >64 || name.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
                throw Invalid("Unsupported subtitle language identifier or track count.");
            var extensions=root.GetProperty("requested_subtitles.:.ext").EnumerateArray().Select(value=>value.GetString()??"").ToArray();
            var paths=root.GetProperty("requested_subtitles.:.filepath").EnumerateArray().Select(value=>value.GetString()??"").ToArray();
            if(extensions.Length!=names.Length || paths.Length!=names.Length)throw Invalid("Incomplete selected subtitle files.");
            return new(path,bounds,names.Select((name,index)=>new Track(name,extensions[index],paths[index])).ToArray());
        }
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {throw Invalid("Invalid selected-fragment subtitle metadata.",error);}
    }
    private static string SubtitleDemuxer(string extension)=>extension.ToLowerInvariant() switch
    {"vtt"=>"webvtt","srt"=>"srt","ass" or "ssa"=>"ass",_=>throw Invalid("Unsupported clipped subtitle format: "+extension+". Supported formats: WebVTT, SRT, ASS and SSA.")};
    private static void ValidateBounds((double Start,double End) bounds)
    {
        // Native export may restore exact frame fractions from persisted
        // millisecond labels. This is not a persisted DownloadSection.
        if(!double.IsFinite(bounds.Start) || !double.IsFinite(bounds.End) || bounds.Start<0 || bounds.End<=bounds.Start || bounds.End>=TimeSpan.MaxValue.TotalSeconds)
            throw Invalid("Invalid native subtitle timeline bounds.");
    }
    private static void LocalFile(string stage,string path)
    {
        if(string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !OwnedFiles.Inside(stage,path))throw Invalid("Subtitle processing requires an owned local staging file.");
        OwnedFiles.RegularOrAbsent(path);
        if(!File.Exists(path))throw Invalid("A selected subtitle or media file is missing.");
    }
    private static void Input(ProcessStartInfo start,string stage,string path,string demuxer)
    {
        LocalFile(stage,path);
        Add(start,"-max_alloc","268435456","-threads","2","-protocol_whitelist","file","-format_whitelist",demuxer,"-f",demuxer);
        if(demuxer=="mov")Add(start,"-enable_drefs","0","-use_absolute_path","0");
        Add(start,"-i",path);
    }
    private static ProcessStartInfo Start(string executable)=>new(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    private static void Add(ProcessStartInfo start,params string[] values){foreach(string value in values)start.ArgumentList.Add(value);}
    private async Task<byte[]> RunAsync(ProcessStartInfo start,int maximum,CancellationToken token)
    {
        var output=await _run(start,maximum,128*1024,token).ConfigureAwait(false);
        if(output.ExitCode!=0)throw Invalid("FFmpeg/FFprobe could not process clipped subtitles (exit "+output.ExitCode+"). "+Encoding.UTF8.GetString(output.StandardError).Trim());
        return output.StandardOutput;
    }
    private static async Task<FragmentMergeToolOutput> RunOwnedAsync(ProcessStartInfo start,int stdout,int stderr,CancellationToken token)
    {
        using var child=ManagedChildProcess.Start(start);
        var captured=await BoundedProcessOutput.ReadAsync(child,stdout,stderr,token).ConfigureAwait(false);
        return new(child.Process.ExitCode,captured.StandardOutput,captured.StandardError);
    }
    private static async Task WriteNewAsync(string path,byte[] bytes,CancellationToken token)
    {
        OwnedFiles.RegularOrAbsent(path);
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,true);
        await file.WriteAsync(bytes,token).ConfigureAwait(false);
    }
    private static InvalidDataException Invalid(string message,Exception? inner=null)=>new("Clipped subtitles: "+message,inner);

    private sealed class ActiveBudget:IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly Task _watch;
        internal bool Expired {get;private set;}
        internal CancellationToken Token=>_stop.Token;
        internal ActiveBudget(CancellationToken token,TimeSpan timeout,Func<bool> paused)
        {
            _stop=CancellationTokenSource.CreateLinkedTokenSource(token);_watch=Watch();
            async Task Watch()
            {
                double active=0;long last=Stopwatch.GetTimestamp();
                try
                {
                    while(true)
                    {
                        await Task.Delay(100,_stop.Token).ConfigureAwait(false);
                        long now=Stopwatch.GetTimestamp();if(!paused())active+=Stopwatch.GetElapsedTime(last,now).TotalMilliseconds;last=now;
                        if(active>=timeout.TotalMilliseconds){Expired=true;_stop.Cancel();return;}
                    }
                }
                catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            }
        }
        public async ValueTask DisposeAsync(){_stop.Cancel();await _watch.ConfigureAwait(false);_stop.Dispose();}
    }
}

using System.IO;
using System.Text;
using VideoDownloader.Native;

// Actual yt-dlp output and both actual FFmpeg PNGs are decoded to a source-frame
// barcode. A duration/flag assertion cannot detect an extra or shifted frame.
internal static class FragmentFrameConsistencyTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-frame-grid-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            foreach(var (rate,fps) in new[]{("24",24d),("25",25d),("30",30d),("30000/1001",30000d/1001),("60",60d)})
                await CheckRate(root,rate,fps);
            Console.WriteLine("PASS: 40 actual yt-dlp exports, 80 boundary PNGs and 10 played clips agree with source-frame IDs at 24/25/30/30000:1001/60 FPS");
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task CheckRate(string root,string rate,double fps)
    {
        string directory=Path.Combine(root,rate.Replace('/','-'));Directory.CreateDirectory(directory);
        int count=(int)Math.Ceiling(6*fps),keyframe=(int)Math.Round(3*fps);
        string source=await FragmentFrameFixture.Source(directory,rate,count,keyframe);
        string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        Require((await FragmentFrameFixture.Decode(ffmpeg,source)).SequenceEqual(Enumerable.Range(0,count)),"The source barcode itself must decode without loss at "+rate);
        await using var origin=new FragmentFrameFixture.Origin(await File.ReadAllBytesAsync(source));
        double duration=count/fps;
        int[] indices=[0,1,2,keyframe-1,keyframe,keyframe+1,count-2,count-1];
        var ranges=indices.Select(index=>new DownloadSection(FragmentTimelineTiming.Milliseconds(index/fps),
            index==count-1?FragmentTimelineTiming.LastMillisecond(duration):FragmentTimelineTiming.Step(index/fps,1,fps,0,duration))).ToArray();
        var selection=new DownloadSelection(origin.Url,ranges[0],DurationSeconds:duration)
            {Sections=new DownloadSectionSet(ranges),FrameRate=fps};
        var options=new DownloadOptions{Output=directory,Container="MP4",Threads="1",EmbedMetadata=false,EmbedChapters=false};
        byte[] output=await FragmentFrameFixture.Tool(DependencyCenter.Executable("yt-dlp.exe"),new YtDlpArguments(options).Build(origin.Url,directory,"best",selection));
        string[] paths=Encoding.UTF8.GetString(output).Split('\n').Where(line=>line.StartsWith("__VD_FILE__:",StringComparison.Ordinal))
            .Select(line=>line[12..].TrimEnd('\r')).ToArray();
        Require(paths.Length==indices.Length,"Every requested range must produce its own media file at "+rate);
        using var frames=new MediaFrameService((request,token)=>Task.FromResult(new MediaFrameSource(new Uri(origin.Url),"fixture",
            new Dictionary<string,string>(),"","","",fps,duration,160,90)),ffmpeg);
        using var clips=new MediaClipService((request,token)=>Task.FromResult(new MediaClipSources(new MediaFrameSource(new Uri(origin.Url),"fixture",
            new Dictionary<string,string>(),"","","",fps,duration,160,90),null)),Path.Combine(directory,"preview"),ffmpeg,
            DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe"));
        for(int index=0;index<indices.Length;index++)
        {
            int expected=indices[index];
            int[] exported=await FragmentFrameFixture.Decode(ffmpeg,paths[index]);
            Require(exported.SequenceEqual([expected]),$"{rate} [{ranges[index].StartSeconds},{ranges[index].EndSeconds}) exported [{string.Join(',',exported)}], expected [{expected}]");
            foreach(bool isEnd in new[]{false,true})
            {
                double seconds=isEnd?ranges[index].EndSeconds:ranges[index].StartSeconds;
                var result=await frames.GetFrameAsync(new(origin.Url,seconds,DurationSeconds:duration,IsEnd:isEnd,FrameRate:fps),CancellationToken.None);
                Require(result.Success,"Actual boundary PNG failed at "+rate+": "+result.Error);
                string png=Path.Combine(directory,"boundary.png");await File.WriteAllBytesAsync(png,result.PngBytes);
                int[] actual=await FragmentFrameFixture.Decode(ffmpeg,png);
                Require(actual.SequenceEqual([expected]),$"{rate} {(isEnd?"end":"start")} PNG [{string.Join(',',actual)}] disagrees with export [{expected}]");
            }
            if(index is 1 or 2)
            {
                var bounds=selection.SectionBounds(ranges[index]);
                var played=await clips.GetClipAsync(new(origin.Url,bounds.Start,bounds.End,DurationSeconds:duration),CancellationToken.None);
                Require(played.Success,"Played grid clip failed at "+rate+": "+played.Error);
                using var clip=played.Clip!;
                Require((await FragmentFrameFixture.Decode(ffmpeg,clip.Path)).SequenceEqual([expected]),
                    "The played clip must show the same source-frame ID as the boundary cards and export at "+rate);
            }
        }
        Console.WriteLine("PASS: "+rate+" FPS decoded exports and both boundary cards at source IDs "+string.Join(',',indices));
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Frame grid consistency: "+message);}
}

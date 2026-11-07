using System.Globalization;
using System.IO;
using System.Text.Json;
using VideoDownloader.Native;

internal static partial class FragmentMergeTests
{
    private static async Task ActualOpus(string root)
    {
        string stage=Path.Combine(root,"opus-stage");Directory.CreateDirectory(stage);
        string first=Path.Combine(stage,"first.opus"),second=Path.Combine(stage,"second.opus"),different=Path.Combine(stage,"different.opus");
        async Task Make(string path,int frequency,int rate)=>await Tool(ToolPath("ffmpeg"),new[]{
            "-hide_banner","-loglevel","error","-f","lavfi","-i","sine=frequency="+frequency+":sample_rate="+rate+":duration=1",
            "-c:a","libopus","-ar",rate.ToString(CultureInfo.InvariantCulture),path});
        await Make(first,440,48000);await Make(second,880,48000);await Make(different,880,24000);
        var service=new FragmentMergeService(ToolPath("ffmpeg"),ToolPath("ffprobe"));
        async Task Verify(FragmentMergeResult result,bool reencoded)
        {
            using var json=JsonDocument.Parse(await Tool(ToolPath("ffprobe"),new[]{"-v","error","-show_format","-show_streams","-of","json",result.MediaPath}));
            var streams=json.RootElement.GetProperty("streams");
            Require(result.Reencoded==reencoded && json.RootElement.GetProperty("format").GetProperty("format_name").GetString()=="ogg" &&
                streams.GetArrayLength()==1 && streams[0].GetProperty("codec_type").GetString()=="audio" &&
                streams[0].GetProperty("codec_name").GetString()=="opus" && streams[0].GetProperty("channels").GetInt32()==1 &&
                Math.Abs(result.DurationSeconds-2)<.15,"Actual .opus joins must verify the Ogg container, complete audio and duration");
            Require(Directory.GetFiles(result.PublishStage,"*",SearchOption.AllDirectories).SequenceEqual(new[]{result.MediaPath}),
                "An Opus join must expose one verified file to publication");
            await AssertAudio(result.MediaPath,.2,440);await AssertAudio(result.MediaPath,1.2,880);
        }
        await Verify(await service.MergeAsync(stage,new[]{first,second},false,false,false,CancellationToken.None),false);
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{first,different},false,false,false,CancellationToken.None),
            "Different actual Opus header parameters require explicit fallback consent");
        await Verify(await service.MergeAsync(stage,new[]{first,different},true,false,false,CancellationToken.None),true);
    }
}

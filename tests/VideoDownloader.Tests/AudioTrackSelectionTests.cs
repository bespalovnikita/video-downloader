using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static class AudioTrackSelectionTests
{
    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("Audio selection: "+message);
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-audio-tracks-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacy=JsonSerializer.Deserialize<DownloadOptions>("{}",AppPersistence.Json)!;
            Require(legacy.AudioTrack=="Default","Legacy settings retain automatic selection");
            foreach(string language in new[]{"","ru,","ru.*","ru]+ba","en\n","--exec","ru,en,fr,de,it,es,ja,ko,zh"})
            {
                bool refused=false;
                try{AudioTrackSelection.Validate(new(){AudioTrack="Languages",AudioLanguages=language});}
                catch(ArgumentException){refused=true;}
                Require(refused,"Invalid language must fail before native launch: "+JsonSerializer.Serialize(language));
            }
            foreach(var invalid in new[]{new DownloadOptions{AudioTrack="unknown"},
                new DownloadOptions{AudioTrack="Languages",AudioLanguages="ru,en",AudioOnly=true},
                new DownloadOptions{AudioTrack="Languages",AudioLanguages="ru,en",Container="MP4"}})
            {
                bool refused=false;
                try{AudioTrackSelection.Validate(invalid);}catch(ArgumentException){refused=true;}
                Require(refused,"Invalid mode/container combination must be refused");
            }
            var selected=new DownloadOptions{AudioTrack="Languages",AudioLanguages="ru,en"};
            var store=new AppPersistence(Path.Combine(root,"profile"));
            store.Save([],selected);
            Require(store.Restore()!.Settings.AudioLanguages=="ru,en","Track selection survives checkpoint restore");
            ProfileCatalog.Apply(selected,"4K archive");
            Require(selected.AudioTrack=="Languages" && selected.AudioLanguages=="ru,en","Profiles preserve explicit audio choice");

            string fixtures=Path.Combine(AppContext.BaseDirectory,"fixtures","audio-tracks");
            using var cases=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures,"cases.json")));
            foreach(var item in cases.RootElement.EnumerateArray())
            {
                string name=item.GetProperty("name").GetString()!;
                string Value(string key,string fallback)=>item.TryGetProperty(key,out var value)?value.GetString()!:fallback;
                var options=new DownloadOptions{
                    AudioTrack=Value("audioTrack","Default"),AudioLanguages=Value("audioLanguages","ru"),
                    Codec=Value("codec","Auto"),AudioOnly=item.TryGetProperty("audioOnly",out var audioOnly)&&audioOnly.GetBoolean(),
                    EmbedMetadata=false,EmbedChapters=false
                };
                var metadata=JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures,"formats.json")))!;
                if(item.TryGetProperty("formats",out var subset))
                {
                    var ids=subset.EnumerateArray().Select(id=>id.GetString()).ToHashSet(StringComparer.Ordinal);
                    metadata["formats"]=new JsonArray(metadata["formats"]!.AsArray()
                        .Where(format=>ids.Contains(format!["format_id"]!.GetValue<string>())).Select(format=>format!.DeepClone()).ToArray());
                }
                string info=Path.Combine(root,name+".json");
                File.WriteAllText(info,metadata.ToJsonString());
                var start=new ProcessStartInfo(DependencyCenter.Executable("yt-dlp.exe")){
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
                };
                var arguments=new YtDlpArguments(options).Build("https://audio-track-fixture.invalid/video",root,Value("quality","best")).SkipLast(1);
                foreach(string argument in arguments.Concat(new[]{"--simulate","--load-info-json",info,"--print","video:__AUDIO_SELECTION__:%(format_id)s"}))
                    start.ArgumentList.Add(argument);
                using var child=ManagedChildProcess.Start(start);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var stdout=child.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr=child.StandardError.ReadToEndAsync(deadline.Token);
                await Task.WhenAll(stdout,stderr,child.Process.WaitForExitAsync(deadline.Token));
                string? expected=item.GetProperty("expected").GetString();
                Require(expected==null?child.Process.ExitCode!=0 && stderr.Result.Contains("Requested format",StringComparison.Ordinal):
                    child.Process.ExitCode==0 && stdout.Result.Split('\n').Any(line=>line.Trim()=="__AUDIO_SELECTION__:"+expected),
                    name+" must select the requested audio without substitution. "+stdout.Result+stderr.Result);
            }
            Console.WriteLine("PASS: audio selection, legacy settings and real yt-dlp offline format selection");
        }
        finally { Directory.Delete(root,true); }
    }
}

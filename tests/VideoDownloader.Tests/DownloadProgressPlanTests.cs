using VideoDownloader.Native;

internal static class DownloadProgressPlanTests
{
    internal static void Run()
    {
        static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
        static YtDlpOutputEvent.Plan Plan(string json)=>(YtDlpOutputEvent.Plan)YtDlpOutputParser.Parse("__VD_PLAN__:"+json)!;
        static YtDlpOutputEvent.Progress Sample(string component,double percent)=>
            (YtDlpOutputEvent.Progress)YtDlpOutputParser.Parse($"__VD_PROGRESS__|{percent.ToString(System.Globalization.CultureInfo.InvariantCulture)}%|1KiB/s|1|50|100|{component}|none|mp4a")!;

        var plan=Plan("{\"id\":\"test\",\"format_id\":\"v+a\",\"requested_formats.:.format_id\":[\"v\",\"a\"]}");
        Require(plan.FormatIds.SequenceEqual(new[]{"v","a"}),"Filtered yt-dlp JSON must preserve the selected component count");
        var progress=new DownloadProgressPlan();progress.Begin(plan);
        Require(progress.Observe(Sample("test:v",100),0).Percent==49.5,"A complete first component is half the transfer, not a complete file");
        Require(progress.Observe(Sample("test:a",0),49.5).Percent==49.5,"Audio start must not reset a completed video stream");
        Require(progress.Observe(Sample("test:a",50),49.5).Percent==74.25,"Both component fractions must contribute");
        var unknown=Sample("test:a",0) with{PercentKnown=false};
        Require(progress.Observe(unknown,74.25).Percent==74.25,"Unknown input must retain the known aggregate");
        Require(progress.Observe(Sample("test:a",20),74.25).Percent==59.4,"A real component counter regression must remain visible");
        Require(progress.Observe(Sample("test:a",100),59.4).Percent==99,"100 is reserved for publication");

        progress.Begin(Plan("{\"id\":\"second\",\"format_id\":\"single\",\"playlist_autonumber\":2,\"n_entries\":4}"));
        Require(progress.Observe(Sample("second:single",0),0).Percent==24.75,"A known collection must include completed prior material fractions");
        Require(progress.Observe(Sample("second:single",100),0).Percent==49.5,"Known collection progress must not hit 100 after its second of four items");
        progress.Reset();
        Require(progress.Observe(Sample("next:single",20),99).Percent==20,"A new attempt without a plan must discard the previous plan and keep its real reset");
        foreach(string bad in new[]{"not-json","[]","{\"n_entries\":\"bad\"}","{\"n_entries\":null}"})
            Require(YtDlpOutputParser.Parse("__VD_PLAN__:"+bad)==null,"Malformed optional plans must not break a download: "+bad);
        foreach(string diagnostic in new[]{"[Merger] Merging formats", "[VideoRemuxer] Remuxing", "[FixupM4a] Correcting container", "ERROR: Postprocessing: connection reset", "ERROR: ffmpeg exited with code 1"})
            Require(YtDlpOutputParser.Parse(diagnostic) is YtDlpOutputEvent.Diagnostic{Postprocessing:true},"Postprocessing failure must be distinguished from transfer failure: "+diagnostic);
        Console.WriteLine("PASS: structured component/collection progress, honest resets, optional plan parsing and postprocessing boundaries");
    }
}

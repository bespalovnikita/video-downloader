using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using VideoDownloader.Native;

internal static class DiskSpaceTests
{
    internal static async Task RunAsync()
    {
        Estimates();Pressure();
        await ActualLocalPaths();
        await ProviderBounds();
        await AssessmentStates();
        Console.WriteLine("PASS: disk-space estimates, temporary/output volumes, unknown sizes, bounded probes and pressure transitions");
    }
    private static MediaFormat Format(string id,long? size,bool approximate=false)=>
        new(id,"mp4",160,90,25,"avc1","mp4a","",size,approximate);
    private static void Estimates()
    {
        const string url="https://disk-space.invalid/Video";
        var selection=new DownloadSelection(url,Format:Format("18",4000),DurationSeconds:100);
        DiskSpaceCandidate[] Copies(DownloadSelection? value,IReadOnlyList<DownloadSection>? ranges=null)=>
            [new(url,"output",value,ranges),new(url,"temporary",value,ranges,ReservedCopies:2)];
        var whole=DiskSpaceEstimator.Estimate(Copies(selection));
        Require(whole.KnownPayloadBytes==4000 && whole.RequiredBytes==12000 && whole.UnknownItems==0,
            "A shared volume reserves two temporary copies and one published copy without counting the source twice");
        var segment=DiskSpaceEstimator.Estimate(Copies(selection with {Section=new(25,50)}));
        Require(segment.KnownPayloadBytes==1000 && segment.RequiredBytes==3000 && segment.Approximate,
            "Known source duration scales a fragment's planning estimate and labels it approximate");
        var multipleSelection=selection with {Section=new(0,10),Sections=new DownloadSectionSet([new(0,10),new(90,100)])};
        var multiple=DiskSpaceEstimator.Estimate(Copies(multipleSelection));
        Require(multiple.KnownPayloadBytes==800 && multiple.RequiredBytes==2400 && multiple.Approximate,
            "A real immutable multi-fragment selection reserves the sum of all ranges rather than only its legacy first section");
        var capturedRanges=DiskSpaceEstimator.Estimate(Copies(multipleSelection,multipleSelection.EffectiveSections));
        Require(capturedRanges==multiple,"Explicit UI-captured ranges and direct selection estimates must agree");
        DownloadSection[] twoFullRanges=[new(0,100),new(0,100)];
        var repeated=DiskSpaceEstimator.Estimate(Copies(selection,twoFullRanges));
        Require(repeated.KnownPayloadBytes==8000 && repeated.RequiredBytes==24000 && repeated.Approximate,
            "The captured-ranges contract sums independent output files without capping their total at one source");
        var noDuration=DiskSpaceEstimator.Estimate(Copies(selection with {Section=new(0,10),DurationSeconds=null}));
        Require(noDuration.KnownPayloadBytes==4000 && noDuration.Approximate,
            "An unknown total duration must not invent a tiny fraction of a known full stream");
        var multipleNoDuration=DiskSpaceEstimator.Estimate(Copies(multipleSelection with {DurationSeconds=null}));
        Require(multipleNoDuration.KnownPayloadBytes==8000 && multipleNoDuration.RequiredBytes==24000 && multipleNoDuration.Approximate,
            "A real multi-fragment selection with unknown duration reserves a full stream for every output range");
        var missing=DiskSpaceEstimator.Estimate(Copies(null));
        Require(missing.KnownPayloadBytes==0 && missing.RequiredBytes==0 && missing.UnknownItems==1 && !missing.HasKnownSize,
            "An automatic format's unknown size is one unknown URL, not a zero-sized download");
        var audioUnknown=selection with {Format=Format("137",3000) with {AudioCodec="none"},
            AudioFormat=new("140","m4a",0,0,0,"none","mp4a","en",null,false)};
        var partial=DiskSpaceEstimator.Estimate(Copies(audioUnknown));
        Require(partial.KnownPayloadBytes==3000 && partial.RequiredBytes==9000 && partial.UnknownItems==1,
            "A missing audio size keeps both the known lower bound and the unknown item");
        var huge=selection with {Format=Format("huge",long.MaxValue) with {AudioCodec="none"},
            AudioFormat=Format("other",long.MaxValue) with {VideoCodec="none"}};
        var saturated=DiskSpaceEstimator.Estimate(Copies(huge));
        Require(saturated.KnownPayloadBytes==long.MaxValue && saturated.RequiredBytes==long.MaxValue,
            "Large extractor sizes must saturate instead of overflowing to a negative reserve");
        var largestPlan=huge with {DurationSeconds=null,Sections=new DownloadSectionSet(
            Enumerable.Range(0,DownloadSectionSet.MaximumCount).Select(index=>new DownloadSection(index*2,index*2+1)))};
        largestPlan.Validate(url,new DownloadOptions());
        var saturatedPlan=DiskSpaceEstimator.Estimate(Copies(largestPlan));
        var saturatedRepeated=DiskSpaceEstimator.Estimate(Copies(huge,twoFullRanges));
        Require(saturatedPlan.KnownPayloadBytes==long.MaxValue && saturatedPlan.RequiredBytes==long.MaxValue &&
            saturatedRepeated.KnownPayloadBytes==long.MaxValue && saturatedRepeated.RequiredBytes==long.MaxValue,
            "Maximum actual ranges and repeated full-range snapshots with huge video/audio sizes must saturate before converting to long");
        var empty=DiskSpaceEstimator.Estimate([new("","output",null,ProbeOnly:true)]);
        Require(empty.UnknownItems==0 && empty.RequiredBytes==0,"Checking an empty destination must not invent an unknown queue row");
    }
    private static void Pressure()
    {
        var pressure=new DiskSpacePressure();
        DiskSpaceSnapshot Space(string volume,long? bytes,DiskSpaceAvailability state=DiskSpaceAvailability.Available)=>
            new(volume+"folder",volume,bytes,state,"");
        var low=Space("volume-a",DiskSpaceAssessment.LowSpaceBytes-1);
        Require(pressure.Observe(low) && !pressure.Observe(low),"An unchanged low volume must warn once");
        Require(!pressure.Observe(Space("volume-a",null,DiskSpaceAvailability.Unavailable)) && !pressure.Observe(low),
            "An unavailable measurement must not invent recovery and trigger another warning");
        Require(!pressure.Observe(Space("volume-a",DiskSpaceAssessment.LowSpaceBytes+1)) && !pressure.Observe(low),
            "A tiny threshold fluctuation must not rearm a warning");
        Require(!pressure.Observe(Space("volume-a",DiskSpaceAssessment.LowSpaceBytes*2)) && pressure.Observe(low),
            "Enough recovered space must allow one later warning");
        Require(pressure.Observe(Space("volume-b",0)),"A separate full volume must have its own warning");
        pressure.Reset();Require(pressure.Observe(low),"A new session must own a new warning cycle");
    }
    private static async Task ActualLocalPaths()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-disk-space-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider=new LocalDiskSpaceProvider();
            string absent=Path.Combine(root,"archive","subscription");
            var measured=await provider.ReadAsync(absent,CancellationToken.None);
            Require(measured.Availability==DiskSpaceAvailability.Available && measured.AvailableBytes is >=0 && measured.Volume.Length>0,
                "A missing child folder must use its real existing local volume");
            Require(measured.Destination==Path.GetFullPath(absent) && !Directory.Exists(absent),
                "A disk check must preserve the intended path without creating it");
            string file=Path.Combine(root,"not-a-folder");File.WriteAllText(file,"sentinel");
            var invalid=await provider.ReadAsync(file,CancellationToken.None);
            Require(invalid.Availability==DiskSpaceAvailability.Unavailable && invalid.AvailableBytes is null && File.ReadAllText(file)=="sentinel",
                "A file destination is unavailable and must remain unchanged");
            var network=await provider.ReadAsync(@"\\unavailable.invalid\share\folder",CancellationToken.None);
            Require(network.Availability==DiskSpaceAvailability.Unknown && network.AvailableBytes is null,
                "UNC capacity is explicitly unknown and must not perform a slow network lookup or report zero");
        }
        finally {Directory.Delete(root,true);}
    }
    private static async Task ProviderBounds()
    {
        using var release=new ManualResetEventSlim();
        var bothStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active=0,maximum=0,calls=0;
        var provider=new LocalDiskSpaceProvider(path=>
        {
            Interlocked.Increment(ref calls);int count=Interlocked.Increment(ref active);
            int observed;
            do {observed=Volatile.Read(ref maximum);if(observed>=count)break;}
                while(Interlocked.CompareExchange(ref maximum,count,observed)!=observed);
            if(count==2)bothStarted.TrySetResult();
            try {release.Wait();return new(path,"bounded",123,DiskSpaceAvailability.Available,"");}
            finally {Interlocked.Decrement(ref active);}
        },TimeSpan.FromSeconds(5),2);
        using var activeCancellation=new CancellationTokenSource();
        try
        {
            var cancelledProbe=provider.ReadAsync("cancel-in-flight",activeCancellation.Token);
            var timedOutProbe=provider.ReadAsync("timeout-in-flight",CancellationToken.None);
            // Thread-pool injection on a single-core runner need not start two
            // blocking delegates within a short deadline. Observe actual entry
            // before testing whether either caller can release a native lease.
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            activeCancellation.Cancel();bool activeCancelled=false;
            try{await cancelledProbe;}catch(OperationCanceledException){activeCancelled=true;}
            Require(activeCancelled && Volatile.Read(ref active)==2,
                "Cancelling an active caller must preserve its still-blocked native slot");
            var checks=Enumerable.Range(0,6).Select(index=>provider.ReadAsync("must-not-start-"+index,CancellationToken.None)).Prepend(timedOutProbe).ToArray();
            var results=await Task.WhenAll(checks).WaitAsync(TimeSpan.FromSeconds(15));
            Require(results.All(result=>result.Availability==DiskSpaceAvailability.Unknown && result.AvailableBytes is null) &&
                Volatile.Read(ref maximum)==2 && Volatile.Read(ref calls)==2 && Volatile.Read(ref active)==2,
                "Timed-out and cancelled callers must retain both native slots; later checks cannot start more OS queries");
        }
        finally {release.Set();await Until(()=>Volatile.Read(ref active)==0);}
        var healthy=await provider.ReadAsync("recovered",CancellationToken.None);
        Require(healthy.Availability==DiskSpaceAvailability.Available && healthy.AvailableBytes==123,
            "A completed OS call must release its bounded slot for a later check");
        var denied=new LocalDiskSpaceProvider(_=>throw new UnauthorizedAccessException(),TimeSpan.FromSeconds(1));
        var noAccess=await denied.ReadAsync("denied",CancellationToken.None);
        Require(noAccess.Availability==DiskSpaceAvailability.Unavailable && noAccess.AvailableBytes is null,
            "Access denied must remain unavailable rather than a full or missing disk");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        bool cancelled=false;
        try{await provider.ReadAsync("cancelled",cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
        Require(cancelled,"Caller cancellation must remain cancellation");
    }
    private static async Task AssessmentStates()
    {
        var provider=new AssessmentProvider();
        var service=new DiskSpaceService(provider);
        string root=Path.Combine(Path.GetTempPath(),"disk-assessment");
        var selection=new DownloadSelection("https://disk-space.invalid/a",Format:Format("18",100));
        var result=await service.AssessAsync([
            new(selection.Url,Path.Combine(root,"2026-11-06","subscription-a"),selection),
            new(selection.Url,root,selection,ReservedCopies:2),
            new("https://disk-space.invalid/b",Path.Combine(root,"subscription-b"),null)],CancellationToken.None);
        Require(provider.Calls==1 && result.Volumes.Count==1 && result.Volumes[0].ItemCount==2 &&
            result.Volumes[0].Estimate.RequiredBytes==300 && result.Volumes[0].Estimate.UnknownItems==1,
            "Safe archive/subscription children on one volume must share a measurement and preserve unknown estimates");
        Require(!result.ShouldWarn,"Enough free bytes must not cause a false warning for unknown sizes");
        provider.Space=new(root,"test",512L*1024*1024,DiskSpaceAvailability.Available,"");
        Require(!(await service.AssessAsync([new(selection.Url,root,selection)],CancellationToken.None)).ShouldWarn &&
            (await service.AssessAsync([new(selection.Url,root,null)],CancellationToken.None)).ShouldWarn,
            "A small known download that fits must not receive a modal warning; an unknown download on a low disk must");
        provider.Space=new(root,"test",0,DiskSpaceAvailability.Available,"");
        Require((await service.AssessAsync([new(selection.Url,root,selection)],CancellationToken.None)).ShouldWarn,
            "Measured zero space is valid and must warn");
        provider.Space=new(root,"test",-1,DiskSpaceAvailability.Available,"");
        var invalid=await service.AssessAsync([new(selection.Url,root,selection)],CancellationToken.None);
        Require(!invalid.ShouldWarn && invalid.Volumes[0].Space.AvailableBytes is null &&
            invalid.Volumes[0].Space.Availability==DiskSpaceAvailability.Unknown,
            "A malformed negative provider value must be unknown rather than a fabricated insufficient-space result");
    }
    private sealed class AssessmentProvider:IDiskSpaceProvider
    {
        internal int Calls;
        internal DiskSpaceSnapshot Space=new("","test",100L*1024*1024*1024,DiskSpaceAvailability.Available,"");
        public Task<DiskSpaceSnapshot> ReadAsync(string destination,CancellationToken cancellationToken)
        {cancellationToken.ThrowIfCancellationRequested();Calls++;return Task.FromResult(Space with {Destination=destination});}
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()){Require(clock.Elapsed<TimeSpan.FromSeconds(5),"A bounded disk probe did not return after release");await Task.Delay(10);}
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Disk-space test: "+message);}
}

using System.IO;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class SubscriptionRequeueTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-subscription-requeue-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PreservePreviousResultsAndRetainedAttempt(root);
            RefuseUnsavableRequestsBeforeMutation(root);
            Console.WriteLine("PASS: explicit subscription requeue survives checkpoint without losing result/retained ownership, preserves exact identities and history, and preflights repeat intent before any live mutation");
        }
        finally { Directory.Delete(root,true); }
    }

    private static void PreservePreviousResultsAndRetainedAttempt(string root)
    {
        var data=new AppPersistence(Path.Combine(root,"preserve-profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1"};
        Directory.CreateDirectory(options.Output);
        string media=Path.Combine(options.Output,"old-result.mp4");
        File.WriteAllText(media,"OLD PUBLISHED RESULT");
        const string completedUrl="https://subscription-repeat.invalid/Video?x=%2f";
        var selection=new DownloadSelection(completedUrl,new DownloadSection(4,6));
        var completedSelection=new DownloadSelection(completedUrl,new DownloadSection(1,2));
        var completed=new DownloadItem{Url=completedUrl,Title="Known result",Status="Completed",State="Done",Progress=100,
            Path=media,OwnedPaths=[media],OutputRoot=options.Output,AttemptFinished=true,TransferredBytes=1024,
            SubscriptionId="original-source",SubscriptionName="Original",Selection=selection,CompletedSelection=completedSelection};
        string run=Path.Combine(root,"vd-native-"+Guid.NewGuid().ToString("N"));
        string stage=Path.Combine(run,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        string partial=Path.Combine(stage,"retained.mp4.part");
        byte[] bytes=Encoding.UTF8.GetBytes("RETAINED PARTIAL BYTES");File.WriteAllBytes(partial,bytes);
        var skipped=new DownloadItem{Url="https://subscription-repeat.invalid/skipped",Status="Skipped",State="Skipped",Progress=42,
            OutputRoot=options.Output,RunDir=run,StageDir=stage,AttemptFinished=true,TransferredBytes=bytes.Length,
            Downloaded=bytes.Length+" B",SubscriptionId="retained-source",SubscriptionName="Retained"};
        DownloadResumeStore.Create(skipped,options,null,"Best");
        var retained=skipped.ResumeState! with {ComponentBytes=new Dictionary<string,long>
            {{DownloadResumeStore.ComponentIdentity("video"),bytes.Length}}};
        skipped.ResumeState=retained;
        _=DownloadResumeStore.Validate(skipped);
        byte[] proof=File.ReadAllBytes(stage+".resume.json");
        var running=new DownloadItem{Url="https://subscription-repeat.invalid/running",Status="Running",State="Downloading",Progress=17};
        var blocked=new DownloadItem{Url="https://subscription-repeat.invalid/blocked",Status="Completed",State="Done",
            PendingRemoval=true,CleanupError="Keep its cleanup proof"};
        var owner=new QueueSessionController();owner.AddBatch([completed,skipped,running,blocked],options);
        data.AppendSuccess(completedUrl);data.Save(owner.Queue,options);
        byte[] history=File.ReadAllBytes(data.HistoryPath),before=File.ReadAllBytes(data.SessionPath);
        using(var legacy=JsonDocument.Parse(before))
            Require(legacy.RootElement.GetProperty("Queue").EnumerateArray().All(row=>!row.TryGetProperty("RequeueRequested",out _)),
                "False repeat intent must remain absent from the compatible checkpoint format.");
        Require(data.Restore()!.Queue.All(row=>!row.RequeueRequested),"A checkpoint without repeat intent must not acquire it on restore.");

        var foreign=new DownloadItem{Url=completedUrl,Status="Completed"};
        var duplicate=new DownloadItem{Url=completedUrl,SubscriptionId="replacement-source",SubscriptionName="Replacement"};
        var differentCase=new DownloadItem{Url="https://subscription-repeat.invalid/video?x=%2F"};
        var owned=completed.OwnedPaths;
        var added=owner.AddBatch([duplicate,differentCase],options,
            new Dictionary<DownloadItem,string>{{completed,"Enriched known result"}},[completed,skipped,running,blocked,foreign]);
        Require(added.SequenceEqual(new[]{differentCase}) && owner.Queue.Count==5 && ReferenceEquals(owner.Queue[0],completed) &&
            completed.SubscriptionId=="original-source" && completed.SubscriptionName=="Original" &&
            !duplicate.RequeueRequested && !foreign.RequeueRequested,
            "Only a distinct exact URL is added; a foreign reference or duplicate must not replace the old source or row.");
        Require(completed.RequeueRequested && completed.DeferredToNextStart && skipped.RequeueRequested && skipped.DeferredToNextStart &&
            !running.RequeueRequested && !running.DeferredToNextStart && !blocked.RequeueRequested && !blocked.DeferredToNextStart,
            "Only eligible existing completed/skipped rows may receive deferred repeat intent.");
        Require(completed.Status=="Completed" && completed.State=="Done" && completed.Progress==100 && completed.Path==media &&
            ReferenceEquals(completed.OwnedPaths,owned) && completed.OwnedPaths.SequenceEqual(new[]{media}) &&
            ReferenceEquals(completed.Selection,selection) && ReferenceEquals(completed.CompletedSelection,completedSelection) &&
            completed.TransferredBytes==1024 && completed.AttemptFinished && File.ReadAllText(media)=="OLD PUBLISHED RESULT",
            "Scheduling a repeat must keep the old result, its deletion ownership and both old/future selections until explicit Start.");
        Require(skipped.Status=="Skipped" && skipped.State=="Skipped" && skipped.Progress==42 && skipped.AttemptFinished &&
            skipped.StageDir==stage && skipped.RunDir==run && ReferenceEquals(skipped.ResumeState,retained) &&
            skipped.TransferredBytes==bytes.Length && File.ReadAllBytes(partial).SequenceEqual(bytes) &&
            File.ReadAllBytes(stage+".resume.json").SequenceEqual(proof),
            "Scheduling continuation must preserve the exact partial bytes, owner proof, pinned plan and counter baseline.");
        Require(owner.Coordinator is null && owner.RunningTask is null && owner.StartedAt is null && owner.Downloads.Count==0 &&
            File.ReadAllBytes(data.SessionPath).SequenceEqual(before) && File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
            "Queue admission alone must neither launch a session nor rewrite successful history or save implicitly.");

        data.Save(owner.Queue,options);
        var restored=data.Restore()!;
        var restoredCompleted=restored.Queue.Single(row=>row.Url==completedUrl);
        var restoredSkipped=restored.Queue.Single(row=>row.Url==skipped.Url);
        Require(restoredCompleted.RequeueRequested && restoredCompleted.DeferredToNextStart && restoredCompleted.Status=="Completed" &&
            restoredCompleted.Path==media && restoredCompleted.OwnedPaths.SequenceEqual(new[]{media}) &&
            restoredCompleted.Selection==selection && restoredCompleted.CompletedSelection==completedSelection &&
            restoredSkipped.RequeueRequested && restoredSkipped.DeferredToNextStart && restoredSkipped.Status=="Skipped" &&
            restoredSkipped.ResumeState!.Token==retained.Token && restoredSkipped.ResumeState.Plan==retained.Plan &&
            restoredSkipped.ResumeState.ComponentBytes.Single().Value==bytes.Length && restoredSkipped.TransferredBytes==bytes.Length,
            "A durable restore must retain actionable repeat requests and the original published/retained contexts without resetting them.");
        _=DownloadResumeStore.Validate(restoredSkipped);
        Require(File.ReadAllText(media)=="OLD PUBLISHED RESULT" && File.ReadAllBytes(partial).SequenceEqual(bytes) &&
            File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),"Saving/restoring repeat intent must preserve old files and append-only history.");
    }

    private static void RefuseUnsavableRequestsBeforeMutation(string root)
    {
        var data=new AppPersistence(Path.Combine(root,"bounds-profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"bounds-output"),Threads="1"};
        Directory.CreateDirectory(options.Output);
        string media=Path.Combine(options.Output,"keep.mp4");File.WriteAllText(media,"KEEP OWNED BYTES");
        var existing=new DownloadItem{Url="https://subscription-repeat.invalid/near-cap",Status="Completed",State="Done",Progress=100,
            Path=media,OwnedPaths=[media],OutputRoot=options.Output,AttemptFinished=true};
        var keeper=new DownloadItem{Url="https://subscription-repeat.invalid/keeper",Title="Old keeper title"};
        var owner=new QueueSessionController();owner.AddBatch([existing,keeper],options);
        existing.Order=7;keeper.Order=9;
        // Match the public snapshot shape and the longest UTC timestamp. Eight
        // spare bytes tolerate Save's timestamp but cannot fit repeat intent.
        var snapshot=new SessionSnapshot{Queue=owner.Queue.ToList(),Settings=options,
            SavedAtUtc=new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc).AddTicks(1)};
        int baseline=JsonSerializer.SerializeToUtf8Bytes(snapshot,AppPersistence.Json).Length;
        existing.Title=new string('x',AppPersistence.MaxCheckpointBytes-baseline-8);
        AppPersistence.ValidateCanSave(owner.Queue,options);data.Save(owner.Queue,options);data.AppendSuccess(existing.Url);
        byte[] before=File.ReadAllBytes(data.SessionPath),history=File.ReadAllBytes(data.HistoryPath);
        int changes=0,collections=0;
        existing.PropertyChanged+=(_,_)=>changes++;keeper.PropertyChanged+=(_,_)=>changes++;
        owner.Queue.CollectionChanged+=(_,_)=>collections++;
        string title=existing.Title;
        void Unchanged()
        {
            Require(owner.Queue.Count==2 && ReferenceEquals(owner.Queue[0],existing) && ReferenceEquals(owner.Queue[1],keeper) &&
                existing.Order==7 && keeper.Order==9 && existing.Title==title && keeper.Title=="Old keeper title" &&
                !existing.RequeueRequested && !existing.DeferredToNextStart && !keeper.RequeueRequested &&
                existing.Status=="Completed" && existing.Progress==100 && existing.Path==media && existing.OwnedPaths.SequenceEqual(new[]{media}) &&
                changes==0 && collections==0 && owner.Coordinator is null && owner.RunningTask is null && owner.StartedAt is null &&
                File.ReadAllText(media)=="KEEP OWNED BYTES" && File.ReadAllBytes(data.SessionPath).SequenceEqual(before) &&
                File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
                "An unsavable repeat batch must not change live flags, title, order, collection, ownership, files, session or durable bytes.");
        }
        Refuses(()=>owner.AddBatch([],options,requeues:[existing]),
            "A flag-only repeat must preflight its extra serialized bytes before setting intent or normalizing live order.");
        Unchanged();
        var incoming=new DownloadItem{Url="https://subscription-repeat.invalid/new"};
        Refuses(()=>owner.AddBatch([incoming],options,new Dictionary<DownloadItem,string>{{keeper,"Enriched keeper title"}},[existing]),
            "A combined add/title/repeat must be rejected as one batch rather than partly marking the old result.");
        Unchanged();
        Require(data.Restore()!.Queue.All(row=>!row.RequeueRequested),"Rejected intent must not poison the still-readable original checkpoint.");

        existing.Title="Small result title";
        var accepted=owner.AddBatch([incoming],options,new Dictionary<DownloadItem,string>{{keeper,"Enriched keeper title"}},[existing]);
        data.Save(owner.Queue,options);
        Require(accepted.SequenceEqual(new[]{incoming}) && existing.RequeueRequested && existing.DeferredToNextStart &&
            keeper.Title=="Enriched keeper title" && owner.Queue.Select(row=>row.Order).SequenceEqual(new[]{1,2,3}) &&
            data.Restore()!.Queue.Single(row=>row.Url==existing.Url).RequeueRequested &&
            File.ReadAllText(media)=="KEEP OWNED BYTES" && File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
            "After explicitly reducing size, a valid repeat batch must remain admissible and durable without losing old data.");
    }

    private static void Refuses(Action action,string message)
    {
        try { action(); }
        catch(InvalidDataException) { return; }
        throw new InvalidOperationException("Subscription requeue test failed: "+message);
    }
    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("Subscription requeue test failed: "+message);
    }
}

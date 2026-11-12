using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestLocalVideoPosters(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Local-video-poster WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(8))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static Window View()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);return (Window)XamlReader.Load(reader);
        }
        static string CacheFile(AppPersistence storage,string url)=>Path.Combine(storage.PreviewDir,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        static void Seed(AppPersistence storage,DownloadItem row,string title,string image="")
        {
            var data=new PreviewData(row.Url,title,"Автор","",.4,"fixture",90,5,"mpeg4","",image,
                DateTime.UtcNow.AddMinutes(-5)){FormatsLoaded=true,
                Formats=[new("video","mp4",160,90,5,"mpeg4","none","",1000,false)]};
            AppPersistence.AtomicWrite(CacheFile(storage,row.Url),JsonSerializer.Serialize(new{
                CacheVersion=1,PreviewFetchVersion=3,Success=true,ThumbnailError="",data.Url,data.Title,data.Uploader,
                data.PlaylistTitle,data.Duration,data.Extractor,data.MaxHeight,data.MaxFps,data.Codecs,data.DynamicRange,
                data.ThumbnailBase64,data.ThumbnailsBase64,data.SavedAtUtc,data.Formats,data.FormatsLoaded,
                FutureMetadata="keep me"
            },AppPersistence.Json));
        }
        static bool IsColor(Image image,bool red)
        {
            if(image.Source is not BitmapSource bitmap)return false;
            var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);byte[] pixel=new byte[4];
            converted.CopyPixels(new Int32Rect(bitmap.PixelWidth/2,bitmap.PixelHeight/2,1,1),pixel,4,0);
            return red?pixel[2]>200 && pixel[0]<40:pixel[0]>200 && pixel[2]<40;
        }
        static void MakeVideo(string ffmpeg,string path,string color)
        {
            var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{"-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i",
                "color="+color+":s=160x90:r=5:d=0.4","-an","-c:v","mpeg4","-threads","1","-pix_fmt","yuv420p",path})
                start.ArgumentList.Add(argument);
            using var child=ManagedChildProcess.Start(start);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var output=BoundedProcessOutput.ReadAsync(child,4096,128*1024,deadline.Token).GetAwaiter().GetResult();
            Require(child.Process.ExitCode==0 && File.Exists(path),"The actual FFmpeg color fixture could not be created: "+output.StandardError);
        }

        string ffmpeg=DependencyCenter.Executable("ffmpeg.exe");
        Require(File.Exists(ffmpeg) && File.Exists(DependencyCenter.Executable("ffprobe.exe")),"Actual FFmpeg and ffprobe must be available in Windows CI.");
        string root=Path.Combine(testRoot,"local-video-posters");Directory.CreateDirectory(root);
        string redVideo=Path.Combine(root,"red.mp4"),blueVideo=Path.Combine(root,"blue.mp4");
        MakeVideo(ffmpeg,redVideo,"red");MakeVideo(ffmpeg,blueVideo,"blue");
        var storage=new AppPersistence(Path.Combine(root,"profile"));var window=View();
        NativeWindow? subject=null;
        try
        {
            subject=new NativeWindow(window,storage,"must-not-launch",automaticUpdates:false,localPosterFfmpegPath:ffmpeg);
            window.Show();window.UpdateLayout();
            var queue=(DataGrid)window.FindName("QueueGrid")!;
            var image=(Image)window.FindName("PreviewImage")!;
            DownloadItem Row(string id,string file)=>new(){Url="https://local-poster.invalid/"+id,Title="Название строки "+id,
                Path=file,OutputRoot=root,OwnedPaths=[file],Status="Completed",State="Done",AttemptFinished=true};
            var first=Row("first",redVideo);Seed(storage,first,"Другое название метаданных");
            using var before=JsonDocument.Parse(File.ReadAllText(CacheFile(storage,first.Url)));
            string originalSaved=before.RootElement.GetProperty("SavedAtUtc").GetString()!;
            int notifications=0;subject.Preview.Changed+=(data,_)=>{if(data.Url==first.Url)notifications++;};
            subject.SessionState.TryAdd(first);subject.Reindex();subject.OnRowChanged(first);
            PumpUntil(()=>subject.Preview.GetCached(first.Url)?.ThumbnailBase64.Length>0 && !subject.Preview.HasLocalPosterWork,
                "A completed unselected row must acquire a real local-video image without Start.");
            Require(queue.SelectedItem is null && subject.Preview.SelectedUrl.Length==0 && notifications>0 &&
                subject.SessionState.StartedAt is null && subject.SessionState.Coordinator is null,
                "Local fallback must notify shared-cache subscribers independently of selection and the download session.");
            Require(first.Title=="Название строки first" && first.Status=="Completed" && first.OwnedPaths.SequenceEqual([redVideo]) &&
                !File.Exists(storage.HistoryPath) && Directory.GetFiles(root,"*.png").Length==0 && Directory.GetFiles(root,"*.jpg").Length==0,
                "An optional local image must not change the row title, success/history/ownership or add media-sidecar pictures.");
            using(var saved=JsonDocument.Parse(File.ReadAllText(CacheFile(storage,first.Url))))
                Require(saved.RootElement.GetProperty("SavedAtUtc").GetString()==originalSaved &&
                    saved.RootElement.GetProperty("PreviewFetchVersion").GetInt32()==3 && saved.RootElement.GetProperty("Success").GetBoolean() &&
                    saved.RootElement.GetProperty("FutureMetadata").GetString()=="keep me" &&
                    saved.RootElement.GetProperty("Formats").GetArrayLength()==1,
                    "The image-only update must preserve the metadata TTL, fetch version, success, formats and future cache fields.");
            subject.Preview.Show(first.Url);
            Require(IsColor(image,true),"The existing selected preview gallery must decode the actual red source-video pixels.");

            var second=Row("selected",blueVideo);second.Status="Running";second.AttemptFinished=false;
            Seed(storage,second,second.Title);subject.SessionState.TryAdd(second);
            subject.Preview.Show(second.Url);
            PumpUntil(()=>subject.Preview.GetCached(second.Url) is not null,"Fresh metadata-only cache must load through the shared worker.");
            Require(image.Source is null,"Selecting an unfinished row with no site image must leave the gallery empty.");
            second.Status="Completed";second.AttemptFinished=true;
            subject.OnRowChanged(second);
            PumpUntil(()=>IsColor(image,false) && !subject.Preview.HasLocalPosterWork,
                "Completion must update the already selected gallery automatically, without another selection or Show call.");

            var unowned=Row("unowned",redVideo);unowned.OwnedPaths=[];subject.SessionState.TryAdd(unowned);subject.OnRowChanged(unowned);
            PumpUntil(()=>subject.SessionState.Downloads.Contains(unowned),"The completed legacy row must still enter Downloads.");
            Require(subject.Preview.GetCached(unowned.Url) is null && unowned.Status=="Completed" && File.Exists(redVideo),
                "A path without an ownership manifest must never grant local preview access or change success.");

            var stale=Row("stale",blueVideo);Seed(storage,stale,stale.Title);subject.SessionState.TryAdd(stale);subject.OnRowChanged(stale);
            PumpUntil(()=>subject.Preview.HasLocalPosterWork,"The stale-attempt check must begin actual optional work.");
            stale.Status="Running";stale.AttemptFinished=false;subject.OnRowChanged(stale);
            PumpUntil(()=>!subject.Preview.HasLocalPosterWork,"A changed attempt must cancel and join its optional reader.");
            Require(subject.Preview.GetCached(stale.Url)?.ThumbnailBase64.Length is not >0 && IsColor(image,false),
                "A late image from an obsolete attempt must not enter the shared cache or replace another selected URL.");

            string removeVideo=Path.Combine(root,"remove.mp4");File.Copy(redVideo,removeVideo);
            var remove=Row("remove",removeVideo);Seed(storage,remove,remove.Title);subject.SessionState.TryAdd(remove);subject.Reindex();
            // Keep the existing selected gallery while the normal Remove action
            // cancels an independently scheduled completed-row request.
            queue.SelectedItem=remove;subject.Preview.SelectedUrl=second.Url;
            subject.Preview.ScheduleLocalPoster(remove);
            Require(subject.Preview.HasLocalPosterWork,"Removal must overlap an admitted local request.");
            uint thread=GetCurrentThreadId();bool answered=false;
            var responder=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            responder.Tick+=(_,_)=>
            {
                if(DismissDialog(thread,"Delete downloads",6) || DismissDialog(thread,"Удалить загрузки",6))
                {answered=true;responder.Stop();}
            };
            using(var watchdog=new CancellationTokenSource())
            {
                _=Task.Delay(TimeSpan.FromSeconds(8),watchdog.Token).ContinueWith(task=>
                {if(!task.IsCanceled){DismissDialog(thread,"Delete downloads",7);DismissDialog(thread,"Удалить загрузки",7);}},TaskScheduler.Default);
                responder.Start();try{((Button)window.FindName("RemoveQueueButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
                finally{watchdog.Cancel();responder.Stop();}
            }
            PumpUntil(()=>!subject.SessionState.Queue.Contains(remove) && !subject.Preview.HasLocalPosterWork,
                "The normal Remove continuation must join preview readers before forgetting the row.");
            Require(answered && !File.Exists(removeVideo) && File.Exists(redVideo) && File.Exists(blueVideo),
                "Confirmed Remove must delete only its published media after cancellation, preserving other files.");
            var stopped=subject.Preview.StopAsync();PumpUntil(()=>stopped.IsCompleted,"Owner shutdown must join the shared preview worker.");
            stopped.GetAwaiter().GetResult();
            Require(!subject.Preview.HasLocalPosterWork,"A stopped preview owner must retain no pending local work.");
            TestLocalPosterAdmissionBarrier(root,redVideo,ffmpeg);
            using(var cached=JsonDocument.Parse(File.ReadAllText(CacheFile(storage,first.Url))))
                TestLocalPosterOverflow(root,redVideo,ffmpeg,cached.RootElement.GetProperty("ThumbnailBase64").GetString()!);
            TestClearWhilePosterJoinsAndStartBegins(root,redVideo,ffmpeg);
            Console.WriteLine("PASS: actual FFmpeg local posters, unselected completion, automatic WPF gallery, metadata preservation, ownership, stale attempts and joined Remove/shutdown");
        }
        finally{subject?.Dispose();window.Close();}
    }

    private static void TestLocalPosterAdmissionBarrier(string root,string video,string ffmpeg)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Local-poster maintenance WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(8))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"admission-profile"));
        var row=new DownloadItem{Url="https://local-poster.invalid/maintenance",Title="Сохранённое название",Status="Completed",State="Done",
            AttemptFinished=true,Path=video,OutputRoot=root,OwnedPaths=[video]};
        SeedTitlePreviewCache(storage,new PreviewData(row.Url,"Запоздалое название метаданных","","",.4,"fixture",90,5,"mpeg4","","",DateTime.UtcNow));
        bool allowed=true;int writes=0,notifications=0;
        var rows=new List<DownloadItem>{row};
        using var preview=new PreviewController(window,storage,"must-not-launch",()=>rows,()=>writes++,_=>{},()=>allowed,ffmpeg);
        preview.Changed+=(_,_)=>notifications++;
        try
        {
            window.Show();preview.BackfillLocalPosters();
            Require(preview.HasLocalPosterWork,"The bounded restored-completion pass must admit an unselected owned row.");
            // No dispatcher work runs between scheduling and this barrier.
            // The eventual real frame therefore reaches a closed admission
            // gate even if the native child completed unusually quickly.
            allowed=false;
            PumpUntil(()=>!preview.HasLocalPosterWork,"A denied background result must still join and release its local reader.");
            Require(preview.GetCached(row.Url)?.ThumbnailBase64.Length is not >0 && writes==0 && notifications==0 &&
                row.Title=="Сохранённое название" && ((Image)window.FindName("PreviewImage")!).Source is null,
                "A maintenance barrier must reject a late image without title, checkpoint or displayed-image mutation.");
            allowed=true;preview.SelectedUrl=row.Url;preview.BackfillLocalPosters();
            PumpUntil(()=>preview.GetCached(row.Url)?.ThumbnailBase64.Length>0 && !preview.HasLocalPosterWork,
                "Resuming the bounded backfill must retry the result rejected by maintenance.");
            Require(((Image)window.FindName("PreviewImage")!).Source is BitmapSource && writes==0 && notifications>0 && row.Title=="Сохранённое название",
                "Image-only admission must reach the existing gallery without applying metadata titles or checkpoint writes.");
            string repairedVideo=Path.Combine(root,"repair.mp4");File.WriteAllText(repairedVideo,"damaged video fixture");
            var repaired=new DownloadItem{Url="https://local-poster.invalid/repaired",Title="После восстановления",Status="Completed",State="Done",
                AttemptFinished=true,Path=repairedVideo,OutputRoot=root,OwnedPaths=[repairedVideo]};rows.Add(repaired);
            preview.BackfillLocalPosters();PumpUntil(()=>!preview.HasLocalPosterWork,"A corrupt video must finish its optional failure quietly.");
            Require(preview.GetCached(repaired.Url)?.ThumbnailBase64.Length is not >0 && repaired.Status=="Completed",
                "A decoder failure must leave successful download state intact.");
            File.Copy(video,repairedVideo,true);preview.BackfillLocalPosters();
            Require(!preview.HasLocalPosterWork,"An unchanged row must not automatically retry a failed optional decoder.");
            preview.ResetLocalPosterAttempts();preview.BackfillLocalPosters();
            PumpUntil(()=>preview.GetCached(repaired.Url)?.ThumbnailBase64.Length>0 && !preview.HasLocalPosterWork,
                "An explicit dependency-recovery retry boundary must release failed attempts for another real decode.");
            Require(writes==0 && repaired.Status=="Completed","Retrying local images must not write a checkpoint or change download success.");
            var stopped=preview.StopAsync();PumpUntil(()=>stopped.IsCompleted,"The isolated preview owner must join its worker.");
            stopped.GetAwaiter().GetResult();
        }
        finally{preview.Dispose();window.Close();}
    }

    private static void TestLocalPosterOverflow(string root,string video,string ffmpeg,string image)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Local-poster overflow WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"overflow-profile"));
        var rows=Enumerable.Range(0,330).Select(index=>new DownloadItem{
            Url="https://local-poster.invalid/overflow/"+index,Title="Новая завершённая строка "+index,
            Status="Completed",State="Done",AttemptFinished=true,Path=video,OutputRoot=root,OwnedPaths=[video]}).ToArray();
        foreach(var row in rows)
            SeedTitlePreviewCache(storage,new PreviewData(row.Url,"Название метаданных","","",.4,"fixture",90,5,"mpeg4","",image,DateTime.UtcNow));
        int writes=0;var received=new HashSet<string>(StringComparer.Ordinal);
        using var preview=new PreviewController(window,storage,"must-not-launch",()=>rows,()=>writes++,_=>{},localPosterFfmpegPath:ffmpeg);
        preview.Changed+=(data,_)=>{if(data.ThumbnailBase64==image)received.Add(data.Url);};
        try
        {
            window.Show();
            // No dispatcher turn may drain admission while these explicit new
            // completions overrun sixteen active plus 288 queued row slots.
            foreach(var row in rows)preview.ScheduleLocalPoster(row);
            Require(preview.HasLocalPosterWork,"Synchronous completed-row admission must leave pending shared-worker work.");
            PumpUntil(()=>received.Count==rows.Length && !preview.HasLocalPosterWork,
                "Every completed row beyond both bounded queues, including the final row, must eventually notify the shared preview.");
            Require(received.Contains(rows[^1].Url) && rows.All(row=>received.Contains(row.Url)) && writes==0 &&
                preview.SelectedUrl.Length==0 && ((Image)window.FindName("PreviewImage")!).Source is null &&
                rows.Select((row,index)=>row.Title=="Новая завершённая строка "+index && row.Status=="Completed").All(value=>value),
                "Overflow recovery must remain selection-independent and image-only, preserving every completed row and its known title.");
            Require(!File.Exists(storage.SessionPath) && !File.Exists(storage.HistoryPath) && File.Exists(video),
                "Draining completion overflow must not start downloads, write checkpoints/history or alter published media.");
            var stopped=preview.StopAsync();PumpUntil(()=>stopped.IsCompleted,"Overflow owner shutdown must join its shared worker.");
            stopped.GetAwaiter().GetResult();
        }
        finally{preview.Dispose();window.Close();}
    }

    private static void TestClearWhilePosterJoinsAndStartBegins(string root,string video,string ffmpeg)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Clear/poster/Start overlap WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static void Click(Window window,string name)=>((Button)window.FindName(name)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(fixture),"The actual native download fixture must be available.");
        string? oldGate=Environment.GetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE"),
            oldLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        string gate=Path.Combine(root,"clear-start-overlap-gate");
        Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"clear-start-profile"));
        NativeWindow? subject=null;bool overlapped=false,answered=false;Exception? callbackError=null;
        var responder=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
        uint thread=GetCurrentThreadId();
        responder.Tick+=(_,_)=>
        {
            if(DismissDialog(thread,"Clear entire queue",6) || DismissDialog(thread,"Очистить всю очередь",6))
            {answered=true;responder.Stop();}
        };
        try
        {
            subject=new NativeWindow(window,storage,fixture,automaticUpdates:false,localPosterFfmpegPath:ffmpeg);
            window.Show();subject.ApplyOptions(new DownloadOptions{Output=Path.Combine(root,"clear-start-output"),Threads="1",Container="MP4"});
            var completed=new DownloadItem{Url="https://local-poster.invalid/clear-overlap",Title="Прежний результат",Status="Completed",State="Done",
                AttemptFinished=true,Path=video,OutputRoot=root,OwnedPaths=[video]};
            var queued=new DownloadItem{Url="https://startup-download.invalid/poster-clear-timer-slow",Title="Новая загрузка"};
            SeedTitlePreviewCache(storage,new PreviewData(completed.Url,completed.Title,"","",.4,"fixture",90,5,"mpeg4","","",DateTime.UtcNow));
            subject.SessionState.TryAdd(completed);subject.SessionState.TryAdd(queued);subject.Reindex();
            subject.Preview.Changed+=(data,isFinal)=>
            {
                if(data.Url!=completed.Url || data.ThumbnailBase64.Length==0 || overlapped || callbackError is not null)return;
                try
                {
                    // The current local request cannot finish its join while
                    // this subscriber remains on its dispatcher callback stack.
                    // Clear therefore reaches its actual await deterministically.
                    using var watchdog=new CancellationTokenSource();
                    _=Task.Delay(TimeSpan.FromSeconds(8),watchdog.Token).ContinueWith(task=>
                    {if(!task.IsCanceled){DismissDialog(thread,"Clear entire queue",7);DismissDialog(thread,"Очистить всю очередь",7);}},TaskScheduler.Default);
                    responder.Start();try{Click(window,"ClearQueueButton");}finally{watchdog.Cancel();responder.Stop();}
                    Require(answered && subject.Preview.HasLocalPosterWork && subject.SessionState.Queue.Count==2,
                        "Confirmed Clear must yield while joining the current local-image callback.");
                    Click(window,"StartButton");overlapped=true;
                }
                catch(Exception error){callbackError=error;}
            };
            subject.Preview.ScheduleLocalPoster(completed);
            PumpUntil(()=>callbackError is not null || overlapped && queued.Status=="Running" &&
                subject.SessionState.Coordinator?.IsRunning==true && File.Exists(gate+".started"),
                "A real Start must survive Clear's later continuation and launch its owned slow fixture.");
            if(callbackError is not null)throw callbackError;
            Require(subject.SessionState.Queue.Count==2 && subject.SessionState.Queue.Contains(completed) &&
                subject.SessionState.Queue.Contains(queued) && completed.Status=="Completed" &&
                completed.OwnedPaths.SequenceEqual([video]) && File.Exists(video),
                "Clear must recheck the newly starting/running session after its await and preserve both captured rows and previous media.");
            Click(window,"StopButton");
            PumpUntil(()=>subject.SessionState.Coordinator is null && subject.SessionState.RunningTask?.IsCompleted==true,
                "The overlapping real download must cancel and join through the normal Stop path.");
            Require(subject.SessionState.Queue.Count==2 && queued.Status=="Interrupted" && !File.Exists(storage.HistoryPath),
                "The overlap must not forget queued work or record a cancelled fixture as success.");
            var stopped=subject.Preview.StopAsync();PumpUntil(()=>stopped.IsCompleted,"The overlap preview owner must join its worker.");
            stopped.GetAwaiter().GetResult();
        }
        finally
        {
            responder.Stop();
            if(subject?.SessionState.Coordinator is {} active)
            {active.Stop();PumpUntil(()=>subject.SessionState.Coordinator is null,"Overlap cleanup must stop its owned fixture.");}
            subject?.Dispose();window.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",oldGate);
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);
        }
    }
}

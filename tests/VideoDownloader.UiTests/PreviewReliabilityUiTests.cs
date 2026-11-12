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
    // Startup remains bounded. An explicit choice beyond that pass must still
    // reach the actual owned video through the one shared preview worker.
    private static void TestLocalPosterSelectionReliability(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Selected local-preview WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();Exception? failure=null;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>
            {
                try{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(8))frame.Continue=false;}
                catch(Exception error){failure=error;frame.Continue=false;}
            };
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(failure is not null)throw new InvalidOperationException(message,failure);
            Require(condition(),message);
        }
        static Window View(string name)
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui",name));
            using var reader=XmlReader.Create(input);return (Window)XamlReader.Load(reader);
        }
        static string CacheFile(AppPersistence storage,string url)=>Path.Combine(storage.PreviewDir,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        static void Seed(AppPersistence storage,DownloadItem row,string image="")
        {
            var data=new PreviewData(row.Url,row.Title,"Автор","",.4,"fixture",90,5,"mpeg4","",image,
                DateTime.UtcNow.AddMinutes(-5)){FormatsLoaded=true};
            AppPersistence.AtomicWrite(CacheFile(storage,row.Url),JsonSerializer.Serialize(new{
                CacheVersion=1,PreviewFetchVersion=3,Success=true,ThumbnailError="",data.Url,data.Title,data.Uploader,
                data.PlaylistTitle,data.Duration,data.Extractor,data.MaxHeight,data.MaxFps,data.Codecs,data.DynamicRange,
                data.ThumbnailBase64,data.ThumbnailsBase64,data.SavedAtUtc,data.Formats,data.FormatsLoaded
            },AppPersistence.Json));
        }
        static string RedCover()
        {
            const int width=16,height=12;
            var pixels=Enumerable.Range(0,width*height).SelectMany(_=>new byte[]{0,0,240,255}).ToArray();
            var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using var output=new MemoryStream();encoder.Save(output);return Convert.ToBase64String(output.ToArray());
        }
        static bool Blue(Image image)
        {
            if(image.Source is not BitmapSource bitmap)return false;
            var decoded=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);byte[] pixel=new byte[4];
            decoded.CopyPixels(new Int32Rect(bitmap.PixelWidth/2,bitmap.PixelHeight/2,1,1),pixel,4,0);
            return pixel[0]>200 && pixel[2]<40;
        }
        static void MakeBlueVideo(string ffmpeg,string path)
        {
            var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{"-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i",
                "color=c=blue:s=160x90:r=5:d=0.4","-an","-c:v","mpeg4","-threads","1","-pix_fmt","yuv420p",path})
                start.ArgumentList.Add(argument);
            using var child=ManagedChildProcess.Start(start);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var output=BoundedProcessOutput.ReadAsync(child,4096,128*1024,deadline.Token).GetAwaiter().GetResult();
            Require(child.Process.ExitCode==0 && File.Exists(path),"The actual blue video could not be created: "+output.StandardError);
        }

        string ffmpeg=DependencyCenter.Executable("ffmpeg.exe");
        Require(File.Exists(ffmpeg) && File.Exists(DependencyCenter.Executable("ffprobe.exe")),
            "Actual FFmpeg and ffprobe are required for the selected-preview regression.");
        string root=Path.Combine(testRoot,"local-poster-selection-reliability");Directory.CreateDirectory(root);
        string video=Path.Combine(root,"owned-blue.mp4");MakeBlueVideo(ffmpeg,video);
        byte[] mediaBefore=File.ReadAllBytes(video);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        var rows=Enumerable.Range(0,PreviewController.LocalPosterBackfillLimit+2).Select(index=>new DownloadItem{
            Url="https://local-poster.invalid/selection/"+index,Title="Сохранённое видео "+index,
            Status="Completed",State="Done",AttemptFinished=true,Path=video,OutputRoot=root,OwnedPaths=[video]}).ToList();
        var selected=rows[^2];var subscription=rows[^1];string remoteImage=RedCover();
        foreach(var row in rows)Seed(storage,row,ReferenceEquals(row,selected) || ReferenceEquals(row,subscription)?"":remoteImage);
        var window=View("MainWindow.xaml");var logs=new List<string>();int writes=0;
        using var preview=new PreviewController(window,storage,"must-not-launch",()=>rows,()=>writes++,logs.Add,
            localPosterFfmpegPath:ffmpeg);
        var received=new HashSet<string>(StringComparer.Ordinal);
        preview.Changed+=(data,_)=>{if(data.ThumbnailBase64.Length>0)received.Add(data.Url);};
        Window? subscriptionWindow=null;SubscriptionPreviewController? details=null;
        try
        {
            window.Show();window.UpdateLayout();var image=(Image)window.FindName("PreviewImage")!;
            preview.BackfillLocalPosters();
            PumpUntil(()=>!preview.HasLocalPosterWork,"The original bounded startup pass must finish.");
            Require(received.Count==PreviewController.LocalPosterBackfillLimit && !received.Contains(selected.Url) &&
                !received.Contains(subscription.Url) && preview.SelectedUrl.Length==0 && image.Source is null,
                "Startup must stop at its original 256-row boundary without choosing a row or silently covering the later results.");

            preview.Show(selected.Url,selected.Title);
            PumpUntil(()=>Blue(image) && preview.GetCached(selected.Url)?.ThumbnailBase64.Length>0 && !preview.HasLocalPosterWork,
                "Selecting the 257th completed row must decode its actual owned blue video even though its metadata has no remote image.");
            Require(preview.SelectedUrl==selected.Url && received.Contains(selected.Url),
                "The actual main gallery must receive the explicitly selected result beyond startup backfill.");

            // The subscription pane uses RequestUrl rather than Show. Exercise
            // that actual controller so its fallback cannot change main focus.
            subscriptionWindow=View("SubscriptionsWindow.xaml");subscriptionWindow.Owner=window;
            details=new SubscriptionPreviewController(subscriptionWindow,preview,_=>throw new InvalidOperationException("No shell action is expected."),logs.Add);
            subscriptionWindow.Show();
            var mainImage=image.Source;string mainTitle=((TextBlock)window.FindName("PreviewTitle")!).Text;
            details.Show(new SubscriptionItem{SubscriptionId="selection-fixture",Url=subscription.Url,Title=subscription.Title,
                State="Processed",DiscoveredAtUtc=DateTimeOffset.UtcNow},"Сохранённая подписка",1);
            var subscriptionImage=(Image)subscriptionWindow.FindName("SubscriptionPreviewImage")!;
            PumpUntil(()=>Blue(subscriptionImage) && received.Contains(subscription.Url) && !preview.HasLocalPosterWork,
                "Selecting the 258th result in the actual subscription pane must receive the local image through the same worker.");
            Require(preview.SelectedUrl==selected.Url && ReferenceEquals(image.Source,mainImage) &&
                ((TextBlock)window.FindName("PreviewTitle")!).Text==mainTitle && Blue(image),
                "A subscription's local image must leave the independent main preview and its selection unchanged.");

            // A previously successful local attempt must not remain a permanent
            // suppression marker after the bounded memory cache evicts it and a
            // compatible metadata-only cache replaces the missing image.
            var cold=rows.Take(PreviewController.LocalPosterBackfillLimit)
                .Where(row=>preview.GetCached(row.Url) is null).Take(129).ToArray();
            Require(cold.Length==129,"The 128-entry cache must leave enough untouched startup rows for real eviction pressure.");
            var coldKeys=cold.Select(row=>row.Url).ToHashSet(StringComparer.Ordinal);
            var warmed=new HashSet<string>(StringComparer.Ordinal);
            void Warmed(PreviewData data,bool final){if(final && coldKeys.Contains(data.Url))warmed.Add(data.Url);}
            preview.Changed+=Warmed;
            try
            {
                foreach(var row in cold)preview.RequestRow(row);
                PumpUntil(()=>warmed.Count>=129,"Independent cached rows must pass through the real bounded memory cache.");
            }
            finally{preview.Changed-=Warmed;}
            Require(preview.GetCached(selected.Url) is null,
                "The actual 128-entry memory limit must evict the earlier successful selected result.");
            preview.Show(cold[^1].Url,cold[^1].Title);
            Require(image.Source is BitmapSource && !Blue(image),"A different cached cover must replace the old rendered blue image before recovery.");
            Seed(storage,selected);
            preview.Show(selected.Url,selected.Title);
            Require(image.Source is null,"Returning to a missing image must clear the other video's cover while the local frame is prepared.");
            PumpUntil(()=>preview.GetCached(selected.Url)?.ThumbnailBase64.Length>0 && Blue(image) && !preview.HasLocalPosterWork,
                "Reselecting an evicted formerly successful result must recover its missing local image rather than retain a permanent seen marker.");

            string corruptFile=Path.Combine(root,"corrupt.mp4");File.WriteAllText(corruptFile,"Not a video");
            var corrupt=new DownloadItem{Url="https://local-poster.invalid/selection/corrupt",Title="Повреждённое видео",
                Status="Completed",State="Done",AttemptFinished=true,Path=corruptFile,OutputRoot=root,OwnedPaths=[corruptFile]};
            rows.Add(corrupt);Seed(storage,corrupt);preview.Show(corrupt.Url,corrupt.Title);
            PumpUntil(()=>!preview.HasLocalPosterWork && preview.GetCached(corrupt.Url) is not null,
                "An undecodable optional local file must finish without changing successful download state.");
            int failures=logs.Count(message=>message.Contains(corrupt.Url,StringComparison.Ordinal) &&
                message.Contains("Local video preview unavailable",StringComparison.Ordinal));
            Require(failures==1 && image.Source is null,"The corrupt local file must cause one quiet optional failure and no invented image.");
            preview.Show(selected.Url,selected.Title);preview.Show(corrupt.Url,corrupt.Title);
            preview.Show(selected.Url,selected.Title);preview.Show(corrupt.Url,corrupt.Title);
            Require(!preview.HasLocalPosterWork && logs.Count(message=>message.Contains(corrupt.Url,StringComparison.Ordinal) &&
                message.Contains("Local video preview unavailable",StringComparison.Ordinal))==failures,
                "Repeated selection must not turn a failed optional decoder into a hot retry loop.");
            Require(writes==0 && !File.Exists(storage.SessionPath) && !File.Exists(storage.HistoryPath) &&
                File.ReadAllBytes(video).SequenceEqual(mediaBefore) && rows.All(row=>row.Status=="Completed" && row.AttemptFinished &&
                    row.OwnedPaths.SequenceEqual([row.Path])) && !logs.Any(message=>message.Contains("stage=metadata",StringComparison.Ordinal)),
                "Selection fallback must preserve media, statuses, ownership and history and must reuse valid metadata without another metadata process.");
            Console.WriteLine("PASS: bounded startup, actual selected-video fallback beyond 256, independent subscription pane, cache eviction recovery and quiet failed attempts");
        }
        finally
        {
            details?.Dispose();subscriptionWindow?.Close();
            try
            {
                var stopped=preview.StopAsync();PumpUntil(()=>stopped.IsCompleted,"Selection fallback shutdown must join its shared worker and readers.");
                stopped.GetAwaiter().GetResult();
            }
            finally{window.Close();}
        }
    }
}

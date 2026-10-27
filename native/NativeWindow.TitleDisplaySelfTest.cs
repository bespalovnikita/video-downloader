using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Metadata is supplied directly or through a fresh local cache entry. This
    // exercises rendered WPF bindings without starting another preview worker.
    public void TestTitleDisplay(string testRoot)
    {
        string profile=Path.GetDirectoryName(_storage.SessionPath)!;
        if(!Path.GetFullPath(profile).StartsWith(Path.GetFullPath(testRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Title display tests require an isolated profile.");
        static void Require(bool condition,string error)
        {
            if(!condition) throw new InvalidOperationException("Title display WPF test failed: "+error);
        }
        // A closed controller may still have dispatcher callbacks queued by
        // its worker. They must not mutate rows/panels or checkpoint afterward.
        var closedRow=new DownloadItem{Url="https://example.invalid/disposed-preview",Title="Before close"};
        int closedCallbacks=0;
        string panelBeforeClose=T("PreviewTitle").Text;
        using(var closed=new PreviewController(_window,
            new AppPersistence(Path.Combine(testRoot,"disposed-preview-"+Guid.NewGuid().ToString("N"))),
            "must-not-start",()=>new[]{closedRow},()=>closedCallbacks++,_=>closedCallbacks++))
        {
            closed.SelectedUrl=closedRow.Url;
            var frame=new DispatcherFrame();
            _=Dispatcher.BeginInvoke(()=>closed.ReceiveReady(new PreviewData(closedRow.Url,
                "Late metadata","","",0,"",0,0,"","","",DateTime.UtcNow)));
            _=Dispatcher.BeginInvoke(()=>closed.ReceiveFailure(closedRow.Url,"Late error"));
            closed.Dispose();
            closed.RequestRow(closedRow);
            closed.Show(closedRow.Url);
            _=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Require(closedCallbacks==0 && closedRow.Title=="Before close" && T("PreviewTitle").Text==panelBeforeClose,
                "Callbacks and requests after preview disposal must leave the closed UI and checkpoint untouched");
        }
        static IEnumerable<TextBlock> Labels(DependencyObject parent)
        {
            if(parent is TextBlock text) yield return text;
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
                foreach(var child in Labels(VisualTreeHelper.GetChild(parent,index))) yield return child;
        }
        void RenderedLabel(DataGrid grid,DownloadItem row,string expected)
        {
            grid.ScrollIntoView(row);
            grid.UpdateLayout();
            bool Matches()
            {
                grid.UpdateLayout();
                var column=grid.Columns.OfType<DataGridTemplateColumn>().First();
                var content=column.GetCellContent(row);
                return content!=null && Labels(content).Any(label=>
                    label.Text==expected && string.Equals(label.ToolTip as string,row.Url,StringComparison.Ordinal));
            }
            if(Matches())return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(10);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(Matches() || DateTime.UtcNow>deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(Matches(),"Rendered label must show "+expected+" with its original URL tooltip");
        }
        var originalQueue=SessionState.Queue.ToArray();
        var originalDownloads=SessionState.Downloads.ToArray();
        var originalSession=SessionState.Coordinator;
        var originalSessionTask=SessionState.RunningTask;
        var originalSelection=Q.SelectedItem;
        string originalInput=B("UrlInput").Text;
        string originalPreview=_preview.SelectedUrl;
        bool? originalMode=K("ShowVideoTitlesCheck").IsChecked;
        string prefix="https://example.invalid/title-display/"+Guid.NewGuid().ToString("N")+"/";
        var known=new DownloadItem{Url=prefix+"Video"};
        var caseDistinct=new DownloadItem{Url=prefix+"video"};
        var unknown=new DownloadItem{Url=prefix+"unknown",Title=" \t "};
        var cached=new DownloadItem{Url=prefix+"cached"};
        PreviewData Metadata(string url,string title)=>new(url,title,"","",0,"",0,0,"","","",DateTime.UtcNow);
        void SeedCache(PreviewData data)
        {
            string file=Path.Combine(_storage.PreviewDir,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.Url))).ToLowerInvariant()+".json");
            AppPersistence.AtomicWrite(file,JsonSerializer.Serialize(new {
                CacheVersion=1,data.Url,data.Title,data.SavedAtUtc,data.Uploader,data.PlaylistTitle,data.Duration,
                data.Extractor,data.MaxHeight,data.MaxFps,data.Codecs,data.DynamicRange,data.ThumbnailBase64
            },AppPersistence.Json));
        }
        try
        {
            // Exercise the actual entry routes with fresh local cache records.
            // This verifies ingestion -> prefetch/cache -> rendered title without
            // clicking a row or Start. Cold metadata extraction is a separate test.
            string urlEntry=prefix+"add-url",textEntry=prefix+"file-txt",shortcutEntry=prefix+"file-url",subscriptionEntry=prefix+"subscription";
            var routeTitles=new Dictionary<string,string>(StringComparer.Ordinal) {
                [urlEntry]="Видео из URL",[textEntry]="Видео из TXT",
                [shortcutEntry]="Видео из InternetShortcut",[subscriptionEntry]="Видео из подписки"
            };
            foreach(var pair in routeTitles)SeedCache(Metadata(pair.Key,pair.Key==subscriptionEntry?"":pair.Value));
            string beforeRoutesPanel=T("PreviewTitle").Text;
            B("UrlInput").Text=urlEntry;
            F<Button>("AddUrlButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(B("UrlInput").Text.Length==0,"The actual Add button must consume the URL field");
            string routeFiles=Path.Combine(testRoot,"title-input-routes-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(routeFiles);
            string textFile=Path.Combine(routeFiles,"videos.txt"),shortcutFile=Path.Combine(routeFiles,"video.url");
            File.WriteAllText(textFile,textEntry+Environment.NewLine,Encoding.UTF8);
            File.WriteAllText(shortcutFile,"[InternetShortcut]\r\nURL="+shortcutEntry+"\r\n",Encoding.UTF8);
            ReceiveArguments([textFile,shortcutFile]);
            var subscriptionAccepted=QueueSubscriptionUrls([subscriptionEntry],Guid.NewGuid().ToString("N"),"Тестовая подписка",
                new Dictionary<string,string>(StringComparer.Ordinal){[subscriptionEntry]=routeTitles[subscriptionEntry]});
            Require(subscriptionAccepted.SequenceEqual(new[]{subscriptionEntry}),"The subscription callback must accept its exact URL");
            Require(SessionState.Queue.Count==originalQueue.Length+routeTitles.Count && ReferenceEquals(Q.SelectedItem,originalSelection) &&
                    _preview.SelectedUrl==originalPreview && T("PreviewTitle").Text==beforeRoutesPanel,
                "URL/file/subscription ingestion must preserve selection and the current preview without a row click");
            foreach(var pair in routeTitles)
            {
                var row=SessionState.Queue.Single(item=>item.Url==pair.Key);
                Require(row.Title==pair.Value && row.Status=="Queued" && row.Path.Length==0,
                    "Every added route must expose its known video title before download: "+pair.Key);
                K("ShowVideoTitlesCheck").IsChecked=false;
                RenderedLabel(Q,row,row.Url);
                K("ShowVideoTitlesCheck").IsChecked=true;
                RenderedLabel(Q,row,pair.Value);
            }
            SaveCheckpoint();
            var routeCheckpoint=_storage.Restore()!;
            Require(routeTitles.All(pair=>routeCheckpoint.Queue.Single(row=>row.Url==pair.Key).Title==pair.Value),
                "Titles from URL, TXT, InternetShortcut and subscription entry routes must survive checkpoint persistence");
            Require(ReferenceEquals(SessionState.Coordinator,originalSession) && ReferenceEquals(SessionState.RunningTask,originalSessionTask) &&
                    SessionState.Downloads.SequenceEqual(originalDownloads),"Checking imported names must not start any download");
            Console.WriteLine("PASS: native title entry routes URL, TXT, InternetShortcut and subscription (warm cache; no row click or Start)");
            foreach(var row in SessionState.Queue.Where(row=>routeTitles.ContainsKey(row.Url)).ToArray())SessionState.Queue.Remove(row);
            B("UrlInput").Text=originalInput;
            K("ShowVideoTitlesCheck").IsChecked=originalMode;
            Reindex();

            var tinyImage=PreviewImage.Decode(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAADklEQVR4nGP4DwYMEAoAU7oL9ZisIGcAAAAASUVORK5CYII="));
            Require(tinyImage.PixelWidth==2 && tinyImage.PixelHeight==2,"Thumbnail decoding must not enlarge small source images");
            var portrait=PreviewImage.Decode(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAPoCAYAAAAftpReAAAAIUlEQVR4nO3EMQEAAAzDoPg33anYBwetliRJkiRJkqTPDnx1yJqP8XqtAAAAAElFTkSuQmCC"));
            Require(portrait.PixelWidth<=720 && portrait.PixelHeight<=720,"Portrait thumbnails must fit both display dimensions");
            foreach(var row in new[]{known,caseDistinct,unknown,cached})SessionState.Queue.Add(row);
            SessionState.Downloads.Add(known);SessionState.Downloads.Add(unknown);
            _preview.SelectedUrl=prefix+"selected-preview";
            string panelBefore=T("PreviewTitle").Text;
            _preview.ReceiveReady(Metadata(known.Url,"Название из фоновых метаданных"));
            Require(known.Title=="Название из фоновых метаданных" && caseDistinct.Title.Length==0 && unknown.Title.Length==0,
                "Metadata must update its exact URL while unselected, without updating a case-distinct URL");
            Require(T("PreviewTitle").Text==panelBefore,"A background title must leave the selected preview panel alone");
            _preview.ReceiveReady(Metadata(known.Url,"   "));
            Require(known.Title=="Название из фоновых метаданных","An empty metadata title must preserve an already known title");
            _preview.SelectedUrl=known.Url;
            _preview.ReceiveReady(Metadata(known.Url,known.Url));
            Require(known.Title=="Название из фоновых метаданных" && T("PreviewTitle").Text==known.Title,
                "An empty or URL-only full preview must keep the discovered video name in the panel");
            _preview.ReceiveReady(Metadata(known.Url,""));
            Require(T("PreviewTitle").Text==known.Title,"A missing image and empty title must not erase a known subscription title");
            _preview.ReceiveFailure(known.Url,"Metadata fixture failed");
            Require(T("PreviewTitle").Text==known.Title && T("PreviewMeta").Text=="Metadata fixture failed",
                "A metadata failure must end loading while keeping the subscription title");
            _preview.ReceiveFailure(caseDistinct.Url,"Unselected failure");
            Require(T("PreviewMeta").Text=="Metadata fixture failed",
                "A stale failure for another exact URL must not alter the selected preview");
            _preview.SelectedUrl=unknown.Url;
            T("PreviewTitle").Text="Получаем метаданные…";
            _preview.ReceiveFailure(unknown.Url,"Timeout fixture");
            Require(T("PreviewTitle").Text=="Не удалось получить метаданные" && T("PreviewMeta").Text=="Timeout fixture",
                "A failed preview without a known title must replace its loading indicator with an error");

            var cache=Metadata(cached.Url,"Название из кеша");
            SeedCache(cache);
            RequestRowPreview(cached);
            Require(cached.Title==cache.Title,"A cache hit must fill a queued title without requiring another metadata callback");

            K("ShowVideoTitlesCheck").IsChecked=false;
            RenderedLabel(Q,known,known.Url);RenderedLabel(D,known,known.Url);
            K("ShowVideoTitlesCheck").IsChecked=true;
            RenderedLabel(Q,known,known.Title);RenderedLabel(D,known,known.Title);
            RenderedLabel(Q,unknown,unknown.Url);RenderedLabel(D,unknown,unknown.Url);
            SaveCheckpoint();
            var saved=_storage.Restore()!;
            Require(saved.Settings.ShowVideoTitles && saved.Queue.Single(row=>row.Url==known.Url).Title==known.Title,
                "The mode and discovered title must survive checkpoint persistence");
            K("ShowVideoTitlesCheck").IsChecked=false;
            RenderedLabel(Q,known,known.Url);RenderedLabel(D,known,known.Url);
            Require(SessionState.Queue.Count==originalQueue.Length+4 && SessionState.Downloads.Count==originalDownloads.Length+2 &&
                    ReferenceEquals(SessionState.Coordinator,originalSession) && known.Url==prefix+"Video" && caseDistinct.Url==prefix+"video",
                "Switching presentation must preserve queue membership, URL identity and download session");
        }
        finally
        {
            SessionState.Queue.Clear();foreach(var row in originalQueue)SessionState.Queue.Add(row);
            SessionState.Downloads.Clear();foreach(var row in originalDownloads)SessionState.Downloads.Add(row);
            B("UrlInput").Text=originalInput;
            _preview.SelectedUrl=originalPreview;
            K("ShowVideoTitlesCheck").IsChecked=originalMode;
            Reindex();
        }
    }
}

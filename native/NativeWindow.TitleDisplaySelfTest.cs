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
        var originalQueue=_queue.ToArray();
        var originalDownloads=_downloads.ToArray();
        var originalSession=_session;
        string originalPreview=_previewUrl;
        bool? originalMode=K("ShowVideoTitlesCheck").IsChecked;
        string prefix="https://example.invalid/title-display/"+Guid.NewGuid().ToString("N")+"/";
        var known=new DownloadItem{Url=prefix+"Video"};
        var caseDistinct=new DownloadItem{Url=prefix+"video"};
        var unknown=new DownloadItem{Url=prefix+"unknown",Title=" \t "};
        var cached=new DownloadItem{Url=prefix+"cached"};
        PreviewData Metadata(string url,string title)=>new(url,title,"","",0,"",0,0,"","","",DateTime.UtcNow);
        try
        {
            foreach(var row in new[]{known,caseDistinct,unknown,cached})_queue.Add(row);
            _downloads.Add(known);_downloads.Add(unknown);
            _previewUrl=prefix+"selected-preview";
            string panelBefore=T("PreviewTitle").Text;
            OnPreviewReady(Metadata(known.Url,"Название из фоновых метаданных"));
            Require(known.Title=="Название из фоновых метаданных" && caseDistinct.Title.Length==0 && unknown.Title.Length==0,
                "Metadata must update its exact URL while unselected, without updating a case-distinct URL");
            Require(T("PreviewTitle").Text==panelBefore,"A background title must leave the selected preview panel alone");
            OnPreviewReady(Metadata(known.Url,"   "));
            Require(known.Title=="Название из фоновых метаданных","An empty metadata title must preserve an already known title");

            var cache=Metadata(cached.Url,"Название из кеша");
            string file=Path.Combine(_storage.PreviewDir,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cached.Url))).ToLowerInvariant()+".json");
            AppPersistence.AtomicWrite(file,JsonSerializer.Serialize(new {
                CacheVersion=1,cache.Url,cache.Title,cache.SavedAtUtc,cache.Uploader,cache.PlaylistTitle,cache.Duration,
                cache.Extractor,cache.MaxHeight,cache.MaxFps,cache.Codecs,cache.DynamicRange,cache.ThumbnailBase64
            },AppPersistence.Json));
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
            Require(_queue.Count==originalQueue.Length+4 && _downloads.Count==originalDownloads.Length+2 &&
                    ReferenceEquals(_session,originalSession) && known.Url==prefix+"Video" && caseDistinct.Url==prefix+"video",
                "Switching presentation must preserve queue membership, URL identity and download session");
        }
        finally
        {
            _queue.Clear();foreach(var row in originalQueue)_queue.Add(row);
            _downloads.Clear();foreach(var row in originalDownloads)_downloads.Add(row);
            _previewUrl=originalPreview;
            K("ShowVideoTitlesCheck").IsChecked=originalMode;
            Reindex();
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    // Uses real modal dispatchers and the isolated UI test host profile.
    // These coordinators never start a child download process.
    internal void TestLiveAddDuringDuplicateDialog(string root)
    {
        var previousSession=SessionState.Coordinator;
        var previousUrls=SessionState.Urls.ToArray();
        try
        {
            foreach(string scenario in new[]{"same-url","finished","replaced"})
            {
                string url="https://example.com/modal-"+scenario;
                _storage.AppendSuccess(url);
                var session=new DownloadCoordinator("must-not-launch",_storage,
                    new DownloadOptions{Output=Path.Combine(root,"modal-output")},_=>{},_=>{});
                var replacement=new DownloadCoordinator("must-not-launch",_storage,
                    new DownloadOptions{Output=Path.Combine(root,"replacement-output")},_=>{},_=>{});
                SessionState.AttachCoordinator(session);
                Exception? failure=null;
                bool observed=false;
                Window? Dialog()=>Application.Current.Windows.Cast<Window>().LastOrDefault(window=>
                    window.Owner==_window && window.FindName("YesButton") is Button);
                void Answer(Window dialog,string name)=>((Button)dialog.FindName(name)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
                var watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(10)};
                watchdog.Tick+=(_,_)=>
                {
                    watchdog.Stop();failure=new TimeoutException("Live-add modal test timed out: "+scenario);
                    while(Dialog() is {} dialog) Answer(dialog,"CancelButton");
                };
                observer.Tick+=(_,_)=>
                {
                    var outer=Dialog();
                    if(outer==null)return;
                    observer.Stop();observed=true;
                    try
                    {
                        if(scenario=="same-url")
                        {
                            _=Dispatcher.BeginInvoke(()=>
                            {
                                if(Dialog() is {} nested && !ReferenceEquals(nested,outer)) Answer(nested,"YesButton");
                            });
                            ReceiveArguments([url]);
                        }
                        else
                        {
                            session.Stop();
                            SessionState.AttachCoordinator(scenario=="finished"?null:replacement);
                        }
                        Answer(outer,"YesButton");
                    }
                    catch(Exception error) {failure=error;Answer(outer,"CancelButton");}
                };
                observer.Start();watchdog.Start();
                try
                {
                    AddUrls([url]);
                    if(failure!=null)throw failure;
                    var rows=SessionState.Queue.Where(row=>row.Url==url).ToArray();
                    if(!observed || rows.Length!=1)
                        throw new InvalidOperationException("Modal live Add must retain exactly one row: "+scenario);
                    if(scenario=="same-url" && (session.Outstanding!=1 || rows[0].DeferredToNextStart))
                        throw new InvalidOperationException("The nested accepted row must enter the original session once.");
                    if(scenario!="same-url" && (!rows[0].DeferredToNextStart || rows[0].Status!="Queued" ||
                        session.Outstanding!=0 || replacement.Outstanding!=0))
                        throw new InvalidOperationException("A finished or replaced session must defer the accepted URL.");
                    SaveCheckpoint();
                    if(_storage.Restore()!.Queue.Count(row=>row.Url==url)!=1)
                        throw new InvalidOperationException("Modal live Add must produce a restorable checkpoint.");
                }
                finally
                {
                    observer.Stop();watchdog.Stop();
                    foreach(var row in SessionState.Queue.Where(row=>row.Url==url).ToArray()) ForgetRow(row);
                    SessionState.AttachCoordinator(null);
                    session.Stop();replacement.Stop();
                    session.RunAsync().GetAwaiter().GetResult();
                    replacement.RunAsync().GetAwaiter().GetResult();
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    replacement.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            SessionState.AttachCoordinator(previousSession);
            SessionState.ReplaceUrls(previousUrls);
            Reindex();
        }
    }
}

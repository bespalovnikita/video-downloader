using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestHistoryLibrary(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("History-library WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(6))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)??throw new InvalidOperationException("Missing history control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string root=Path.Combine(testRoot,"history-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        string existing=Path.Combine(root,"existing.mp4"),missing=Path.Combine(root,"missing.mp4"),denied=Path.Combine(root,"denied.mp4"),unsafePath=Path.Combine(root,"untrusted.exe");
        File.WriteAllText(existing,"media sentinel");
        File.WriteAllText(storage.SessionPath,"damaged queue must remain untouched");
        File.WriteAllText(storage.HistoryPath,JsonSerializer.Serialize(new{Version=1,Url="https://history-ui.invalid/legacy"})+"\n"+
            JsonSerializer.Serialize(new{Version=1,Url="https://history-ui.invalid/Video?token=AbC",CompletedAtUtc="2026-01-02T03:04:05Z",
                Title="Исторический Ёж",Source="Канал",OutputPaths=new[]{existing,missing,denied,unsafePath}})+"\n");
        byte[] history=File.ReadAllBytes(storage.HistoryPath),session=File.ReadAllBytes(storage.SessionPath);
        using var markup=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(markup);
        var owner=(Window)XamlReader.Load(reader);
        var probe=new HistoryUiProbe(existing,missing,denied);
        var opened=new ConcurrentQueue<HistoryOpenCommand>();
        DownloadHistoryWindow? window=null;
        try
        {
            owner.Show();owner.Width=1280;owner.Height=760;
            int dispatcherThread=Environment.CurrentManagedThreadId;
            window=new DownloadHistoryWindow(owner,storage,new HistoryFileAccess(probe),opened.Enqueue);window.Show();
            var grid=Control<DataGrid>(window,"HistoryGrid");var files=Control<DataGrid>(window,"HistoryFilesGrid");
            var search=Control<TextBox>(window,"HistorySearchBox");var status=Control<TextBlock>(window,"HistoryStatusText");
            PumpUntil(()=>grid.Items.Count==2,"The actual history grid did not load modern and legacy events.");
            Require(grid.IsReadOnly && files.IsReadOnly && probe.Calls==0,"Opening the library must not probe the filesystem or change records.");
            var modern=grid.Items.Cast<HistoryRecordRow>().Single(row=>row.Entry.Title.Length>0);
            grid.SelectedItem=modern;grid.UpdateLayout();
            Require(modern.Title=="Исторический Ёж" && modern.Source=="Канал" && modern.Url.EndsWith("token=AbC",StringComparison.Ordinal) &&
                modern.Date.Length>0 && files.Items.Count==4,"The actual grid must retain title, source, exact URL, date and captured file paths.");
            grid.ScrollIntoView(modern,grid.Columns[1]);grid.UpdateLayout();
            Require((grid.Columns[1].GetCellContent(modern) as TextBlock)?.Text=="Исторический Ёж",
                "The title must be rendered through the actual history DataGrid binding.");
            search.Text="ёж";PumpUntil(()=>grid.Items.Count==1 && status.Text.Contains("Найдено",StringComparison.Ordinal),"Unicode title search did not filter the real library.");
            search.Text="2026";PumpUntil(()=>grid.Items.Count==1 && status.Text.Contains("Найдено",StringComparison.Ordinal) && ((HistoryRecordRow)grid.Items[0]).Entry.CompletedAtUtc.HasValue,"Date search did not retain its dated success.");
            search.Text="legacy";PumpUntil(()=>grid.Items.Count==1 && ((HistoryRecordRow)grid.Items[0]).Entry.OutputPaths.Count==0,"Legacy search did not expose unknown path metadata.");
            Require(Control<TextBlock>(window,"HistorySelectionStatus").Text.Contains("не сохранены",StringComparison.Ordinal) &&
                !Control<Button>(window,"CheckHistoryFilesButton").IsEnabled,"Legacy records must explain unknown paths, without inventing a missing file.");
            search.Text="";PumpUntil(()=>grid.Items.Count==2,"Clearing search must restore both historical attempts.");
            modern=grid.Items.Cast<HistoryRecordRow>().Single(row=>row.Entry.Title.Length>0);grid.SelectedItem=modern;
            Click(window,"CheckHistoryFilesButton");
            PumpUntil(()=>modern.Availability.Contains("Доступно 1",StringComparison.Ordinal),"The selected event's file check did not finish.");
            var fileRows=files.Items.Cast<HistoryFileRow>().ToArray();
            Require(fileRows.Single(row=>row.Path==existing).Availability.State==HistoryFileState.Present &&
                fileRows.Single(row=>row.Path==missing).Availability.State==HistoryFileState.Missing &&
                fileRows.Single(row=>row.Path==denied).Availability.State==HistoryFileState.Unavailable &&
                fileRows.Single(row=>row.Path==unsafePath).Availability.State==HistoryFileState.Unsafe,
                "Availability must distinguish existing, missing, inaccessible and unsafe paths in actual rows.");
            Require(probe.Threads.All(thread=>thread!=dispatcherThread),"Filesystem work must execute outside the WPF dispatcher.");
            files.SelectedItem=fileRows.Single(row=>row.Path==existing);Click(window,"OpenHistoryFileButton");
            PumpUntil(()=>opened.Count==1 && Control<Button>(window,"OpenHistoryFileButton").IsEnabled,"Explicit opening of a checked media file did not reach the safe launcher.");
            Require(opened.Last().Kind==HistoryOpenKind.Media && opened.Last().MediaPath==existing,"The launch must retain the selected media path.");
            files.SelectedItem=fileRows.Single(row=>row.Path==missing);Click(window,"OpenHistoryFileButton");
            PumpUntil(()=>status.Text.Contains("Не удалось открыть",StringComparison.Ordinal),"A missing file must report its absence.");
            Require(opened.Count==1,"Missing files must not reach the shell launcher.");
            Click(window,"OpenHistoryFolderButton");PumpUntil(()=>opened.Count==2 && Control<Button>(window,"OpenHistoryFolderButton").IsEnabled,"A missing file's existing folder should still be accessible explicitly.");
            Require(opened.Last().Kind==HistoryOpenKind.Folder && opened.Last().TargetPath==root,"Folder actions must target the containing directory, not interpolate a shell command.");
            files.SelectedItem=fileRows.Single(row=>row.Path==unsafePath);
            int before=probe.Calls;Click(window,"OpenHistoryFileButton");Click(window,"OpenHistoryFolderButton");
            Require(!Control<Button>(window,"OpenHistoryFileButton").IsEnabled && opened.Count==2 && probe.Calls==before,
                "Directly raised clicks cannot turn an imported executable path into a shell action.");
            Require(history.SequenceEqual(File.ReadAllBytes(storage.HistoryPath)) && session.SequenceEqual(File.ReadAllBytes(storage.SessionPath)),
                "Reading, searching, missing-file checks and explicit opening cannot rewrite successes or a damaged queue checkpoint.");
            window.Close();window=null;

            using(var held=new HistoryUiHeldProbe())
            {
                window=new DownloadHistoryWindow(owner,storage,new HistoryFileAccess(held),opened.Enqueue);window.Show();
                grid=Control<DataGrid>(window,"HistoryGrid");
                PumpUntil(()=>grid.Items.Count==2,"The cancellable history view did not load.");
                grid.SelectedItem=grid.Items.Cast<HistoryRecordRow>().Single(row=>row.Entry.OutputPaths.Count>0);
                Click(window,"CheckHistoryFilesButton");PumpUntil(()=>held.Started.Task.IsCompleted,"The background file probe did not start.");
                window.Close();string closedStatus=Control<TextBlock>(window,"HistoryStatusText").Text;
                held.Release.Set();PumpUntil(()=>held.Active==0,"Closed-window file work did not release its bounded probe slots.");
                Require(Control<TextBlock>(window,"HistoryStatusText").Text==closedStatus,"Late file-check results must not mutate a closed history window.");
                window=null;
            }

            // Observe the actual main-window button/owner lifecycle without a
            // prepared history cache, custom product switches or live metadata.
            var isolated=new AppPersistence(Path.Combine(root,"main-button-profile"));
            using(var native=new NativeWindow(owner,isolated,"must-not-launch"))
            {
                Click(owner,"OpenHistoryButton");PumpUntil(()=>native.HistoryLibrary?.IsVisible==true,"The main history button did not open the actual library.");
                Require(native.SessionState.Coordinator==null && native.SessionState.RunningTask==null && !File.Exists(isolated.HistoryPath),
                    "Opening the main history library must neither create successful history nor start a download.");
                var ownedWindow=native.HistoryLibrary!;native.Dispose();Require(!ownedWindow.IsVisible,"Disposing the main owner must close its history window.");
            }
            Console.WriteLine("PASS: real WPF history search, legacy metadata, distinct availability, explicit safe open actions and cancelled close without queue/history writes");
        }
        finally{window?.Close();if(owner.IsVisible)owner.Close();}
    }
    private sealed class HistoryUiProbe(string present,string missing,string denied):IHistoryFileProbe
    {
        private int _calls;
        internal int Calls=>Volatile.Read(ref _calls);
        internal readonly ConcurrentQueue<int> Threads=new();
        public HistoryFileAvailability Check(string path,bool directory)
        {
            Interlocked.Increment(ref _calls);Threads.Enqueue(Environment.CurrentManagedThreadId);
            if(directory)return new(HistoryFileState.Present,"Папка доступна");
            if(path==present)return new(HistoryFileState.Present,"Файл доступен");
            if(path==missing)return new(HistoryFileState.Missing,"Файл не найден");
            return new(HistoryFileState.Unavailable,path==denied?"Нет доступа для проверки":"Неизвестный путь");
        }
    }
    private sealed class HistoryUiHeldProbe:IDisposable,IHistoryFileProbe
    {
        internal readonly ManualResetEventSlim Release=new();
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        internal int Active=>Volatile.Read(ref _active);
        public HistoryFileAvailability Check(string path,bool directory)
        {
            Interlocked.Increment(ref _active);Started.TrySetResult();
            try{Release.Wait();return new(HistoryFileState.Present,"late result");}
            finally{Interlocked.Decrement(ref _active);}
        }
        public void Dispose()=>Release.Dispose();
    }
}

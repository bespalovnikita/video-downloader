using System.Text.Json;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestJournalProfiles(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Journal WPF test failed: "+message);
        }
        static Window Load()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            return (Window)XamlReader.Load(reader);
        }
        static void Click(Window window,string name)=>((Button)window.FindName(name)!)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Complete(Task task)
        {
            if(!task.IsCompleted)
            {
                var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(12);
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
                timer.Tick+=(_,_)=>{if(task.IsCompleted || DateTime.UtcNow>=deadline)frame.Continue=false;};
                timer.Start();
                try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
                Require(task.IsCompleted,"Timed out waiting for asynchronous journal export");
            }
            task.GetAwaiter().GetResult();
        }

        string root=Path.Combine(testRoot,"journal");
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        var window=Load();
        NativeWindow? subject=null;
        DownloadCoordinator? coordinator=null;
        try
        {
            subject=new NativeWindow(window,storage,"must-not-launch");
            window.Show();
            var profiles=(ComboBox)window.FindName("LogProfileCombo")!;
            var log=(TextBox)window.FindName("LogBox")!;
            Require(profiles.Items.Cast<string>().SequenceEqual(JournalProfileCatalog.DisplayNames),
                "The actual journal selector must expose all four logging profiles");
            Require(subject.ReadOptions().LogProfile=="Standard","Old settings default to the standard journal");

            foreach(var profile in new[]{JournalProfile.Errors,JournalProfile.Standard,JournalProfile.Progress,JournalProfile.Detailed})
            {
                profiles.SelectedItem=JournalProfileCatalog.Display(profile);
                Click(window,"ClearJournalButton");
                subject.Journal.Write(new JournalRecord(JournalLevel.Error,"fixture","error-marker"));
                subject.Journal.Write(new JournalRecord(JournalLevel.Warning,"fixture","warning-marker"));
                subject.Journal.Write(new JournalRecord(JournalLevel.Information,"fixture","info-marker"));
                subject.Journal.Write(new JournalRecord(JournalLevel.Progress,"fixture","progress-marker"));
                subject.Journal.Write(new JournalRecord(JournalLevel.Verbose,"fixture","verbose-marker"));
                subject.FlushJournal();
                Require(log.Text.Contains("error-marker") && log.Text.Contains("warning-marker"),"Errors and warnings remain visible in every profile");
                Require(log.Text.Contains("info-marker")== (profile!=JournalProfile.Errors),"Information filtering follows the actual selector");
                Require(log.Text.Contains("progress-marker")== (profile is JournalProfile.Progress or JournalProfile.Detailed),"Progress filtering follows the actual selector");
                Require(log.Text.Contains("verbose-marker")== (profile==JournalProfile.Detailed),"Raw output is reserved for the detailed profile");
                Require(storage.Restore()!.Settings.LogProfile==JournalProfileCatalog.ToId(profile),"Changing a journal profile checkpoints it even with an empty queue");
            }

            var legacy=JsonSerializer.Deserialize<DownloadOptions>("{\"Threads\":\"4\"}",AppPersistence.Json)!;
            subject.ApplyOptions(legacy);
            Require(subject.ReadOptions().LogProfile=="Standard","Settings without the new journal field remain compatible");
            subject.ApplyOptions(new DownloadOptions{LogProfile="unknown-future-profile"});
            Require(subject.ReadOptions().LogProfile=="Standard","An unknown restored journal profile falls back safely");
            profiles.SelectedItem=JournalProfileCatalog.Display(JournalProfile.Detailed);
            ((ComboBox)window.FindName("ProfileCombo")!).SelectedItem=ProfileCatalog.Display("4K archive");
            Require(subject.ReadOptions().LogProfile=="Detailed","Changing the download profile must preserve journal verbosity");

            var previousCulture=CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
                Click(window,"ClearJournalButton");
                const string publicUrl="https://www.youtube.com/watch?v=AbC_de-12&list=PL123";
                subject.Journal.WriteMessage("Thumbnail timed out for "+publicUrl);
                subject.FlushJournal();
                string continuation=Environment.NewLine+"    Исходная диагностика: Thumbnail timed out for "+publicUrl;
                Require(log.Text.Contains("Истекло время получения обложки.") && log.Text.Contains(continuation) &&
                    !log.Text.Contains(@"\r") && !log.Text.Contains(@"\n"),
                    "The actual log control renders the original public video URL on a real continuation line");
                Click(window,"CopyJournalButton");
                Require(Clipboard.GetText().Contains(continuation),"Copy preserves readable diagnostic newlines and the public video URL");
                string diagnosticFile=Path.Combine(root,"multiline-diagnostic.log");
                Complete(subject.ExportJournalAsync(diagnosticFile));
                Require(File.ReadAllText(diagnosticFile).Contains(continuation),"Save preserves the same readable diagnostic newlines");
            }
            finally{CultureInfo.CurrentUICulture=previousCulture;}
            Click(window,"ClearJournalButton");
            for(int index=0;index<700;index++)subject.Journal.Write(new JournalRecord(JournalLevel.Warning,"fixture",
                $"multiline-{index:0000}\nfirst detail\nsecond detail\nthird detail"));
            for(int index=0;index<4;index++)subject.FlushJournal();
            Require(log.Text.Count(character=>character=='\n')<=2000 && !log.Text.Contains("multiline-0000") && log.Text.Contains("multiline-0699"),
                "The rendered tail counts physical continuation lines rather than only event records");

            // An active coordinator is attached without running its workers. The UI
            // must allow hot verbosity changes without admitting work or changing rows.
            var rows=Enumerable.Range(1,4).Select(index=>new DownloadItem
            {
                Url="https://journal.invalid/task-"+index+(index==4?"?v=PublicFour&token=private-video-token":""),
                Title=index<3?"Видео "+index:"",Order=index,Slot="T"+index,Progress=index*10
            }).ToArray();
            foreach(var row in rows)subject.SessionState.TryAdd(row);
            var options=new DownloadOptions{Output=Path.Combine(root,"output"),Threads="4"};
            coordinator=new DownloadCoordinator("must-not-launch",storage,options,_=>{},_=>{},trace:subject.Journal.Write);
            subject.SessionState.Begin(coordinator,options,rows);
            Click(window,"ClearJournalButton");
            profiles.SelectedItem=JournalProfileCatalog.Display(JournalProfile.Progress);
            subject.UpdateState();
            rows[0].Progress=1;
            subject.UpdateState();subject.FlushJournal();
            Require(log.Text.Contains("event=overall_progress") && log.Text.Contains("reason=percent_decreased") &&
                    log.Text.Contains("total=4") && log.Text.Contains("current_pct=22.75"),
                "Overall traces explain real aggregate decreases without replacing displayed percentages");
            Require(log.Text.Contains(coordinator.TraceSessionId) && rows.All(row=>log.Text.Contains(DownloadTraceIdentity.JobId(row.Url))),
                "The overall trace correlates all four rows with the engine's session and job identities");
            Require(rows.All(row=>log.Text.Contains("video_seq="+row.Order+" video=",StringComparison.Ordinal)) &&
                log.Text.Contains("video=\"Видео 1\"",StringComparison.Ordinal) &&
                log.Text.Contains("https://journal.invalid/task-3",StringComparison.Ordinal) &&
                log.Text.Contains("v=PublicFour",StringComparison.Ordinal) && !log.Text.Contains("private-video-token",StringComparison.Ordinal) &&
                log.Text.Contains("members_shown=4 members_truncated=False",StringComparison.Ordinal),
                "The actual overall log must show all queue numbers, known titles or sanitized URL fallbacks without losing its tail");
            foreach(var profile in new[]{JournalProfile.Errors,JournalProfile.Standard,JournalProfile.Progress,JournalProfile.Detailed})
                profiles.SelectedItem=JournalProfileCatalog.Display(profile);
            Require(profiles.IsEnabled && coordinator.Outstanding==4 && rows.All(row=>row.Status=="Queued") && !File.Exists(storage.HistoryPath),
                "Hot profile changes must preserve queued rows and never start or record downloads");
            subject.SessionState.AttachCoordinator(null);
            subject.UpdateState();

            Click(window,"ClearJournalButton");
            for(int index=0;index<2600;index++)subject.Journal.Write(new JournalRecord(JournalLevel.Verbose,"fixture",
                $"tail-{index:0000} "+new string('x',120)));
            for(int index=0;index<20;index++)subject.FlushJournal();
            Require(log.Text.Length<=512*1024 && log.Text.Count(character=>character=='\n')<=2000,
                "The rendered log tail remains bounded after a burst of raw output");
            Require(!log.Text.Contains("tail-0000") && log.Text.Contains("tail-2599"),"The UI retains the latest tail");
            Click(window,"CopyJournalButton");
            string copied=Clipboard.GetText();
            Require(copied.Contains("tail-0000") && copied.Contains("tail-2599"),
                "Copy uses the complete retained journal rather than only the rendered tail");
            string saved=Path.Combine(root,"export.log");
            Complete(subject.ExportJournalAsync(saved));
            string exported=File.ReadAllText(saved);
            Require(exported.Contains("tail-0000") && exported.Contains("tail-2599") &&
                    copied.Split('\n').Where(line=>line.Contains("tail-",StringComparison.Ordinal)).All(line=>exported.Contains(line,StringComparison.Ordinal)),
                "Export preserves every retained UTF-8 record from Copy, including records omitted from the UI tail");
            Click(window,"ClearJournalButton");
            Require(log.Text.Length==0 && !subject.Journal.Snapshot().Contains("tail-",StringComparison.Ordinal) && File.Exists(saved),
                "Clear affects the screen and copyable buffer while exported files stay intact");

            subject.SessionState.Queue.Clear();subject.SaveCheckpoint();
            Require(storage.Restore()!.Settings.LogProfile=="Detailed","The final live-selected journal profile persists for the next launch");
            Console.WriteLine("PASS: journal WPF profile filtering, live changes, aggregate trace, bounded tail, clipboard/export and empty-queue persistence");
        }
        finally
        {
            if(coordinator!=null)
            {
                subject?.SessionState.AttachCoordinator(null);
                coordinator.Stop();
                coordinator.RunAsync().GetAwaiter().GetResult();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            subject?.Dispose();window.Close();
        }

        var restoredWindow=Load();
        NativeWindow? restoredSubject=null;
        try
        {
            restoredSubject=new NativeWindow(restoredWindow,storage,"must-not-launch");
            Require(restoredSubject.ReadOptions().LogProfile=="Detailed", "The actual next application window restores the selected profile");
        }
        finally { restoredSubject?.Dispose();restoredWindow.Close(); }
    }
}

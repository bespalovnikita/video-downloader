using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using VideoDownloader.Native;

internal static class JournalTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-journal-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach(var profile in Enum.GetValues<JournalProfile>())
            {
                await using var journal=new DiagnosticJournal(Path.Combine(root,profile.ToString()),profile);
                foreach(var level in Enum.GetValues<JournalLevel>())journal.Write(new JournalRecord(level,"test","marker-"+level));
                string snapshot=journal.Snapshot();
                Require(snapshot.Contains("marker-Error") && snapshot.Contains("marker-Warning"),"Every profile retains errors and warnings");
                Require(snapshot.Contains("marker-Information")== (profile>=JournalProfile.Standard),"Ordinary events follow the selected verbosity");
                Require(snapshot.Contains("marker-Progress")== (profile>=JournalProfile.Progress),"Progress has its own explicit profile");
                Require(snapshot.Contains("marker-Verbose")== (profile==JournalProfile.Detailed),"Raw extractor output appears only in Detailed");
                journal.Profile=JournalProfile.Detailed;
                journal.Write(new JournalRecord(JournalLevel.Verbose,"test","after-live-change"));
                Require(journal.Snapshot().Contains("after-live-change"),"The profile changes immediately without restarting a session");
                Require(JournalProfileCatalog.Parse(JournalProfileCatalog.ToId(profile))==profile &&
                    JournalProfileCatalog.Parse(JournalProfileCatalog.Display(profile))==profile,"Profile IDs and Russian labels round-trip");
            }
            Require(JournalProfileCatalog.Parse("future-profile")==JournalProfile.Standard,"Unknown saved profile IDs fall back to Standard");

            var limits=new JournalLimits(MemoryEntries:3,MemoryBytes:2048,FileBytes:4096,FileQueueEntries:100);
            await using(var bounded=new DiagnosticJournal(Path.Combine(root,"bounded"),JournalProfile.Standard,limits))
            {
                for(int i=0;i<10;i++)bounded.Write(new JournalRecord(JournalLevel.Information,"bounds","entry-"+i));
                string snapshot=bounded.Snapshot();
                Require(snapshot.Contains("entry-9") && !snapshot.Contains("entry-0") && snapshot.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length==3,
                    "The memory journal evicts old records instead of growing indefinitely");
                Require(Encoding.UTF8.GetByteCount(snapshot)<=limits.MemoryBytes,"The UTF-8 memory bound includes line endings");
                var batch=bounded.DrainPending(2);
                Require(batch.Count==2 && batch[0].Contains("удалены из памяти"),"The UI receives a visible bounded-gap notice when it falls behind");
                bounded.Clear();
                Require(bounded.Snapshot().Length==0 && bounded.DrainPending().Count==0,"Clear resets the retained display and its pending cursor");
                bounded.WriteMessage("after-clear");
                Require(bounded.DrainPending().Single().Contains("after-clear"),"New entries still arrive after Clear");
            }
            Require(File.ReadAllText(Path.Combine(root,"bounded","journal.log")).Contains("entry-0"),"Clear does not delete the diagnostic file");

            var previousCulture=CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
                await using var russian=new DiagnosticJournal(Path.Combine(root,"russian"));
                russian.WriteMessage("ERROR: HTTP Error 403: Forbidden https://name:password@example.org/watch?token=secret#fragment");
                string text=russian.Snapshot();
                Require(text.Contains("Исходная диагностика") && text.Contains("403"),"Russian explanations retain the sanitized original diagnostic");
                Require(!text.Contains("secret") && !text.Contains("password") && !text.Contains("#fragment"),"Localization cannot restore URL credentials or tokens");
                const string publicUrl="https://www.youtube.com/watch?v=AbC_de-12&list=PL123";
                russian.WriteMessage("Thumbnail timed out for "+publicUrl);
                string warning=russian.DrainPending().Single(entry=>entry.Contains("Thumbnail timed out",StringComparison.Ordinal));
                Require(warning.Contains("Истекло время получения обложки.") &&
                    warning.Contains(Environment.NewLine+"    Исходная диагностика: Thumbnail timed out for "+publicUrl) &&
                    !warning.Contains(@"\r") && !warning.Contains(@"\n"),
                    "One localized event has real readable continuation lines and preserves the exact public video URL");
                russian.Write(new JournalRecord(JournalLevel.Warning,"category\nforged","before\n[Warning/app] forged",Session:"session\r\nforged"));
                string multiline=russian.Snapshot();
                Require(multiline.Contains("[Warning/category\\nforged] session=session\\r\\nforged before"+Environment.NewLine+"    [Warning/app] forged"),
                    "Correlation fields stay single-line and untrusted message continuations cannot forge top-level records");
                await russian.DisposeAsync();
                Require(File.ReadAllText(russian.FilePath)==multiline,
                    "The actual UTF-8 journal file contains exactly the readable snapshot, including sanitized multiline messages");
            }
            finally{CultureInfo.CurrentUICulture=previousCulture;}

            var concurrentLimits=new JournalLimits(MemoryEntries:5000,MemoryBytes:2*1024*1024,FileBytes:1024*1024,FileQueueEntries:5000);
            var concurrent=new DiagnosticJournal(Path.Combine(root,"concurrent"),JournalProfile.Progress,concurrentLimits);
            await Task.WhenAll(Enumerable.Range(1,4).Select(worker=>Task.Run(()=>
            {
                for(int n=0;n<250;n++)concurrent.Write(new JournalRecord(JournalLevel.Progress,"download",
                    $"event=sample n={n}","session","job-"+worker,"T"+worker));
            })));
            await concurrent.DisposeAsync();
            var samples=concurrent.Snapshot().Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(line=>line.Contains("event=sample")).ToArray();
            Require(samples.Length==1000,"Four concurrent producers retain every captured sample within capacity");
            foreach(int worker in Enumerable.Range(1,4))
                Require(samples.Count(line=>line.Contains("job=job-"+worker+" worker=T"+worker))==250,"Job and worker identities cannot mix across producers");
            Require(File.ReadAllText(concurrent.FilePath).Split('\n').Count(line=>line.Contains("event=sample"))==1000,
                "DisposeAsync flushes actual queued records to disk");
            Require(concurrent.DroppedFileEntries==0 && concurrent.LastFileError.Length==0,"The writer reports no loss when queue capacity is sufficient");
            using(var reopened=new FileStream(concurrent.FilePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Require(reopened.Length>0,"DisposeAsync must release the actual journal handle for an exclusive reopen");

            var rotation=new DiagnosticJournal(Path.Combine(root,"rotation"),JournalProfile.Detailed,
                new JournalLimits(MemoryEntries:1000,MemoryBytes:1024*1024,FileBytes:2048,FileQueueEntries:1000));
            for(int i=0;i<80;i++)rotation.Write(new JournalRecord(JournalLevel.Verbose,"raw","rotate-"+i+" "+new string('x',300)));
            await rotation.DisposeAsync();
            string[] files=Directory.GetFiles(Path.GetDirectoryName(rotation.FilePath)!,"journal*.log");
            Require(files.Length==3 && files.All(file=>new FileInfo(file).Length<=2048),"Real file rotation keeps at most three files within the byte cap");
            Require(File.ReadAllText(rotation.FilePath).Contains("rotate-79"),"Rotation preserves the newest records in journal.log");

            string blocked=Path.Combine(root,"blocked");File.WriteAllText(blocked,"unrelated sentinel");
            var failure=new DiagnosticJournal(blocked);
            failure.WriteMessage("visible despite inaccessible file");
            await failure.DisposeAsync();
            Require(failure.LastFileError.Length>0 && failure.Snapshot().Contains("visible despite inaccessible file") &&
                failure.Snapshot().Contains("Не удалось записать файл журнала"),"An unavailable file sink is non-fatal and visible while memory capture continues");
            Require(File.ReadAllText(blocked)=="unrelated sentinel","A logging failure never changes an unrelated existing file");

            string outside=Path.Combine(root,"outside");Directory.CreateDirectory(outside);
            string sentinel=Path.Combine(outside,"journal.log");File.WriteAllText(sentinel,"outside sentinel");
            string linked=Path.Combine(root,"linked");
            if(OperatingSystem.IsWindows())
            {
                var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(string argument in new[]{"/d","/c","mklink","/J",linked,outside})start.ArgumentList.Add(argument);
                using var process=Process.Start(start)!;
                await process.WaitForExitAsync();
                Require(process.ExitCode==0,"The native journal test must create a real NTFS junction");
            }
            else Directory.CreateSymbolicLink(linked,outside);
            var redirected=new DiagnosticJournal(Path.Combine(linked,"child"));
            redirected.WriteMessage("must stay in memory");await redirected.DisposeAsync();
            Require(redirected.LastFileError.Length>0 && !Directory.Exists(Path.Combine(outside,"child")) && File.ReadAllText(sentinel)=="outside sentinel",
                "An intermediate link cannot redirect journal creation or rotation into another directory");
            Directory.Delete(linked);
            if(!OperatingSystem.IsWindows())
            {
                string danglingDirectory=Path.Combine(root,"dangling");Directory.CreateDirectory(danglingDirectory);
                string absent=Path.Combine(outside,"must-not-create.log");
                string link=Path.Combine(danglingDirectory,"journal.log");File.CreateSymbolicLink(link,absent);
                var dangling=new DiagnosticJournal(danglingDirectory);
                dangling.WriteMessage("must not follow a dangling file link");await dangling.DisposeAsync();
                Require(dangling.LastFileError.Length>0 && !File.Exists(absent),"A dangling journal.log link must be rejected before opening its target");
                File.Delete(link);
            }
            Console.WriteLine("PASS: journal profiles, live changes, bounded memory, redaction, concurrent jobs, file rotation and non-fatal file errors");
        }
        finally{Directory.Delete(root,true);}
    }
    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("Journal test failed: "+message);
    }
}

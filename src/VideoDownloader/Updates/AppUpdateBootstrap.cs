using System.Diagnostics;
using System.Text.Json;

namespace VideoDownloader.Native;

internal sealed record AppUpdateHandoff(string Directory,string TargetDirectory,int ParentId,long ParentStartTicks,string MutexName,string HelperName,bool Recovery);

// Private handoff between the installed single-file application and its own
// temporary copy. This runs before WPF/singleton startup and is not a user CLI.
internal static class AppUpdateBootstrap
{
    private const string Argument="--vd-internal-update";
    internal static Process StartHelper(PreparedAppUpdate prepared,string mutexName,Process? owner=null,
        string? sourceExecutable=null,string? profileDirectory=null,bool recovery=false)
    {
        string executable=sourceExecutable??Environment.ProcessPath??throw new InvalidOperationException("Cannot locate the application executable.");
        if(!Path.GetFullPath(executable).Equals(Path.Combine(Path.GetFullPath(prepared.TargetDirectory),"VideoDownloader.exe"),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install the complete Windows ZIP before using application updates.");
        AppUpdatePackage.AssertNoReparse(prepared.Directory);
        string name="VideoDownloader.Update."+Guid.NewGuid().ToString("N")+".exe";
        string helper=Path.Combine(prepared.Directory,name);
        File.Copy(executable,helper,false);
        using var current=owner is null?Process.GetCurrentProcess():null;
        owner??=current!;
        string handoff=helper+".json";
        File.WriteAllText(handoff,JsonSerializer.Serialize(new AppUpdateHandoff(prepared.Directory,prepared.TargetDirectory,
            owner.Id,owner.StartTime.ToUniversalTime().Ticks,mutexName,name,recovery),AppUpdatePackage.Json));
        var start=new ProcessStartInfo(helper){UseShellExecute=false,WorkingDirectory=prepared.Directory};
        if(profileDirectory is not null)start.Environment["VD_DATA_DIRECTORY"]=profileDirectory;
        start.ArgumentList.Add(Argument);start.ArgumentList.Add(handoff);
        return Process.Start(start)??throw new IOException("Cannot start the application updater.");
    }
    internal static bool TryStartRecovery(string targetDirectory,string mutexName)
    {
        var prepared=AppUpdateInstaller.FindPending(targetDirectory);
        if(prepared is null)return false;
        using var helper=StartHelper(prepared,mutexName,recovery:true);
        return true;
    }
    internal static void ScheduleCompletedCleanup()
    {
        _=Task.Run(async()=>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            try
            {
                AppUpdateInstaller.CleanupCompleted(Path.Combine(Path.GetTempPath(),"VideoDownloaderUpdates"),directory=>
                {
                    foreach(string file in Directory.EnumerateFiles(directory,"VideoDownloader.Update.*.exe").Take(8))
                    {
                        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(file)))
                        {
                            using(process)
                            {
                                try
                                {
                                    if(process.HasExited)continue;
                                    long started=process.StartTime.ToUniversalTime().Ticks;
                                    if(string.Equals(process.MainModule?.FileName,file,StringComparison.OrdinalIgnoreCase) &&
                                        !process.HasExited && process.StartTime.ToUniversalTime().Ticks==started)return true;
                                }
                                catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception)
                                {return true;} // An uncertain process identity cannot authorize deletion.
                            }
                        }
                    }
                    return false;
                });
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {Debug.WriteLine("Application update cleanup deferred: "+error.Message);}
        });
    }
    internal static bool TryRun(string[] args)
    {
        if(args.Length==0 || args[0]!=Argument)return false;
        try { RunAsync(args).GetAwaiter().GetResult(); }
        catch(Exception error)
        {
            System.Windows.Forms.MessageBox.Show(ErrorMessages.Text(
                "Не удалось обновить приложение. Прежние файлы сохранены или восстановлены.\n",
                "The application update failed. Previous files were retained or restored.\n")+ErrorMessages.ForException(error)+ErrorMessages.Text("\n\nИсходная диагностика:\n","\n\nOriginal diagnostic:\n")+error,
                "Video Downloader",System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Warning);
        }
        return true;
    }
    private static async Task RunAsync(string[] args)
    {
        if(args.Length!=2)throw new InvalidDataException("Invalid updater handoff.");
        string file=Path.GetFullPath(args[1]);AppUpdatePackage.AssertNoReparse(file);
        if(new FileInfo(file).Length>16*1024)throw new InvalidDataException("Invalid updater handoff size.");
        var handoff=JsonSerializer.Deserialize<AppUpdateHandoff>(File.ReadAllBytes(file),AppUpdatePackage.Json)
            ??throw new InvalidDataException("Empty updater handoff.");
        string directory=Path.GetFullPath(handoff.Directory);
        string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"VideoDownloaderUpdates"))+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(root,StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(handoff.HelperName,@"^VideoDownloader\.Update\.[0-9a-f]{32}\.exe$") ||
            !file.Equals(Path.Combine(directory,handoff.HelperName)+".json",StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Environment.ProcessPath,Path.Combine(directory,handoff.HelperName),StringComparison.OrdinalIgnoreCase) ||
            !handoff.MutexName.StartsWith(@"Local\VideoDownloader.Native.",StringComparison.Ordinal) ||
            handoff.ParentId<=0 || handoff.ParentStartTicks<=0)
            throw new InvalidDataException("The updater handoff does not belong to this process.");
        using var prepared=handoff.Recovery?AppUpdateInstaller.FindPending(handoff.TargetDirectory)??
            throw new InvalidDataException("The interrupted update was not found."):AppUpdateInstaller.OpenPrepared(directory);
        if(!prepared.Directory.Equals(directory,StringComparison.OrdinalIgnoreCase) ||
            !prepared.TargetDirectory.Equals(handoff.TargetDirectory,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update recovery belongs to another installation.");
        await WaitForOwnerAsync(handoff.ParentId,handoff.ParentStartTicks,TimeSpan.FromMinutes(2),CancellationToken.None).ConfigureAwait(false);
        Exception? updateFailure=null;
        using(var singleton=new Mutex(false,handoff.MutexName))
        {
            bool acquired;
            try {acquired=singleton.WaitOne(TimeSpan.FromSeconds(10));}catch(AbandonedMutexException){acquired=true;}
            if(!acquired)throw new IOException("Another application instance is still using this installation.");
            try {if(handoff.Recovery)AppUpdateInstaller.Recover(prepared);else AppUpdateInstaller.Apply(prepared);}
            catch(Exception error)
            {
                updateFailure=error;
            }
            finally {singleton.ReleaseMutex();}
        }
        // Apply reports an incomplete rollback as AggregateException and keeps
        // recovery files. Do not launch an installation in that uncertain state.
        if(updateFailure is AggregateException)throw updateFailure;
        using var restarted=Restart(prepared.TargetDirectory);
        File.WriteAllText(Path.Combine(prepared.Directory,"result.json"),JsonSerializer.Serialize(new{
            ProcessId=restarted.Id,StartedUtcTicks=restarted.StartTime.ToUniversalTime().Ticks,
            Recovered=handoff.Recovery,Success=updateFailure is null},AppUpdatePackage.Json));
        if(updateFailure is not null)throw new IOException("The update failed; the previous application was restored and restarted.",updateFailure);
    }
    internal static async Task WaitForOwnerAsync(int id,long startTicks,TimeSpan timeout,CancellationToken cancel)
    {
        Process? owner=null;
        try
        {
            try {owner=Process.GetProcessById(id);}catch(ArgumentException){return;}
            if(owner.StartTime.ToUniversalTime().Ticks!=startTicks)return;
            await owner.WaitForExitAsync(cancel).WaitAsync(timeout,cancel).ConfigureAwait(false);
        }
        finally {owner?.Dispose();}
    }
    internal static Process Restart(string directory)
    {
        return Process.Start(new ProcessStartInfo(Path.Combine(directory,"VideoDownloader.exe"))
            {UseShellExecute=false,WorkingDirectory=directory})??throw new IOException("Cannot restart the application.");
    }
}

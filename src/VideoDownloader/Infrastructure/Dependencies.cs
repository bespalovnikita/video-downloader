using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace VideoDownloader.Native;

public enum DependencyStage { Downloading, Verifying, Installing, Updating }

// The percentage belongs to the current transfer only. Verification and
// publication have no measurable overall percentage and stay indeterminate.
public sealed record DependencyProgress(DependencyStage Stage,long DownloadedBytes=0,long? TotalBytes=null);

internal sealed record DownloadToolReadiness(bool HasYtDlp,bool HasFfmpeg,bool HasFfprobe)
{
    internal bool Ready=>HasYtDlp && HasFfmpeg && HasFfprobe;
    internal bool NeedsFfmpeg=>!HasFfmpeg || !HasFfprobe;
    internal string Description=>
        $"yt-dlp: {(HasYtDlp?ErrorMessages.Text("установлен","installed"):ErrorMessages.Text("не найден","missing"))}\n"+
        $"FFmpeg: {(HasFfmpeg?ErrorMessages.Text("установлен","installed"):ErrorMessages.Text("не найден","missing"))}\n"+
        $"ffprobe: {(HasFfprobe?ErrorMessages.Text("установлен","installed"):ErrorMessages.Text("не найден","missing"))}";
}

public static class DependencyCenter
{
    public static string Executable(string name)
    {
        string local=Path.Combine(AppContext.BaseDirectory,name);
        if(File.Exists(local)) return local;
        foreach(var dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
        {
            try
            {
                var candidate=Path.Combine(dir,name);
                if(File.Exists(candidate))return candidate;
            }
            catch(ArgumentException){}
        }
        return local;
    }
    public static string Inspect()=>InspectTools().Description;
    internal static DownloadToolReadiness InspectTools(string? ytDlp=null)=>new(
        File.Exists(ytDlp??Executable("yt-dlp.exe")),File.Exists(Executable("ffmpeg.exe")),File.Exists(Executable("ffprobe.exe")));
    public static void ValidateDownloadTools(string ytDlp) =>
        ValidateDownloadTools(ytDlp,Executable("ffmpeg.exe"),Executable("ffprobe.exe"));
    public static void ValidateDownloadTools(string ytDlp,string ffmpeg,string ffprobe)
    {
        if(!File.Exists(ytDlp)) throw new FileNotFoundException("Не найден yt-dlp.exe.",ytDlp);
        var missing=new[]{ffmpeg,ffprobe}.Where(path=>!File.Exists(path)).Select(Path.GetFileName).ToArray();
        if(missing.Length>0)
            throw new FileNotFoundException("Для скачивания и обработки медиа нужны "+string.Join(", ",missing)+
                ". Нажмите Install FFmpeg в разделе Dependencies и повторите запуск. Загрузка ещё не началась.");
    }
    public static Task<string> UpdateYtDlpAsync(CancellationToken token=default)=>UpdateYtDlpAsync(null,token);

    public static async Task<string> UpdateYtDlpAsync(IProgress<DependencyProgress>? progress,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        Report(progress,new(DependencyStage.Updating));
        var psi=new ProcessStartInfo(Executable("yt-dlp.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        psi.ArgumentList.Add("-U");
        // yt-dlp's Windows updater schedules deletion of its .old binary in
        // a child shell after it exits. That cleanup must outlive -U itself.
        using var process=Process.Start(psi)??throw new IOException("Cannot start yt-dlp");
        using var cancel=token.Register(()=>{try {if(!process.HasExited)process.Kill(true);}catch{}});
        // Read both pipes through EOF after cancellation too. Returning while
        // the updater is still replacing bytes would let a new download start
        // against a changing executable.
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdout,stderr).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)throw new IOException(stderr.Result);
            return stdout.Result;
        }
        finally
        {
            if(!process.HasExited)try{process.Kill(true);}catch(InvalidOperationException){}
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout,stderr).ConfigureAwait(false);
        }
    }
    public static Task InstallFfmpegAsync(CancellationToken token=default) =>
        InstallFfmpegAsync(null,token);

    public static Task InstallFfmpegAsync(IProgress<DependencyProgress>? progress,CancellationToken token=default) =>
        InstallFfmpegAsync(new Uri("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"),
            AppContext.BaseDirectory,TimeSpan.FromMinutes(6),token,progress:progress);

    internal static async Task InstallFfmpegAsync(Uri source,string installDirectory,TimeSpan downloadTimeout,CancellationToken token,
        Action<int>? afterReplacement=null,IProgress<DependencyProgress>? progress=null)
    {
        if (downloadTimeout<=TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        string scratch=Path.Combine(Path.GetTempPath(),"vd-ffmpeg-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            using var http=new HttpClient{Timeout=Timeout.InfiniteTimeSpan};
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(downloadTimeout);
            string zip=Path.Combine(scratch,"ffmpeg.zip");
            try
            {
                Report(progress,new(DependencyStage.Downloading));
                using var response=await http.GetAsync(source,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long? total=response.Content.Headers.ContentLength is >0?response.Content.Headers.ContentLength:null;
                Report(progress,new(DependencyStage.Downloading,0,total));
                await using var input=await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var target=File.Create(zip);
                byte[] buffer=new byte[81920];
                long received=0,lastReport=Stopwatch.GetTimestamp();
                int count;
                while((count=await input.ReadAsync(buffer,deadline.Token).ConfigureAwait(false))!=0)
                {
                    await target.WriteAsync(buffer.AsMemory(0,count),deadline.Token).ConfigureAwait(false);
                    received+=count;
                    if(Stopwatch.GetElapsedTime(lastReport)>=TimeSpan.FromMilliseconds(250))
                    {
                        Report(progress,new(DependencyStage.Downloading,received,total));
                        lastReport=Stopwatch.GetTimestamp();
                    }
                }
                deadline.Token.ThrowIfCancellationRequested();
                Report(progress,new(DependencyStage.Downloading,received,total));
            }
            catch(OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new TimeoutException("Не удалось скачать FFmpeg за отведённое время. Повторите установку.",error); }
            token.ThrowIfCancellationRequested();
            Report(progress,new(DependencyStage.Verifying));
            FfmpegInstallTransaction.Install(zip,installDirectory,token,afterReplacement,
                ()=>Report(progress,new(DependencyStage.Installing)));
        }
        finally { try { Directory.Delete(scratch,true); } catch(IOException){} }
    }
    private static void Report(IProgress<DependencyProgress>? progress,DependencyProgress value)
    {
        // Optional feedback never turns an otherwise valid install into a
        // failed component transaction.
        try{progress?.Report(value);}catch{}
    }
    public static void RegisterProtocol()
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Classes\videodownloader")??throw new IOException("Registry unavailable");
        key.SetValue("","URL:VideoDownloader Protocol");
        key.SetValue("URL Protocol","");
        using(var icon=key.CreateSubKey("DefaultIcon"))
            icon?.SetValue("","\""+Environment.ProcessPath+"\",0");
        using(var command=key.CreateSubKey(@"shell\open\command"))
            command?.SetValue("","\""+Environment.ProcessPath+"\" \"%1\"");
    }
}

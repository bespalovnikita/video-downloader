using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace VideoDownloader.Native;

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
    public static string Inspect()
    {
        string yt=Executable("yt-dlp.exe"), ffmpeg=Executable("ffmpeg.exe"),probe=Executable("ffprobe.exe");
        return $"yt-dlp: {(File.Exists(yt)?"installed":"missing")}\n"+
               $"FFmpeg: {(File.Exists(ffmpeg)?"installed":"missing")}\n"+
               $"ffprobe: {(File.Exists(probe)?"installed":"missing")}";
    }
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
    public static async Task<string> UpdateYtDlpAsync(CancellationToken token=default)
    {
        var psi=new ProcessStartInfo(Executable("yt-dlp.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        psi.ArgumentList.Add("-U");
        // yt-dlp's Windows updater schedules deletion of its .old binary in
        // a child shell after it exits. That cleanup must outlive -U itself.
        using var process=Process.Start(psi)??throw new IOException("Cannot start yt-dlp");
        using var cancel=token.Register(()=>{try {if(!process.HasExited)process.Kill(true);}catch{}});
        var stdout=process.StandardOutput.ReadToEndAsync(token);
        var stderr=process.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(token)).ConfigureAwait(false);
        if(process.ExitCode!=0)throw new IOException(stderr.Result);
        return stdout.Result;
    }
    public static Task InstallFfmpegAsync(CancellationToken token=default) =>
        InstallFfmpegAsync(new Uri("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"),
            AppContext.BaseDirectory,TimeSpan.FromMinutes(6),token);

    internal static async Task InstallFfmpegAsync(Uri source,string installDirectory,TimeSpan downloadTimeout,CancellationToken token)
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
                using var response=await http.GetAsync(source,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var target=File.Create(zip);
                await response.Content.CopyToAsync(target,deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch(OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new TimeoutException("Не удалось скачать FFmpeg за отведённое время. Повторите установку.",error); }
            string unpack=Path.Combine(scratch,"unpacked");
            ZipFile.ExtractToDirectory(zip,unpack);
            string[] f=Directory.GetFiles(unpack,"ffmpeg.exe",SearchOption.AllDirectories);
            string[] p=Directory.GetFiles(unpack,"ffprobe.exe",SearchOption.AllDirectories);
            if(f.Length==0||p.Length==0)throw new IOException("No ffmpeg or ffprobe binary in package.");
            token.ThrowIfCancellationRequested();
            File.Copy(f[0],Path.Combine(installDirectory,"ffmpeg.exe"),true);
            File.Copy(p[0],Path.Combine(installDirectory,"ffprobe.exe"),true);
        }
        finally { try { Directory.Delete(scratch,true); } catch(IOException){} }
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

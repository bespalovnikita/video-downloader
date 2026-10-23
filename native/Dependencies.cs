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
        using var child=ManagedChildProcess.Start(psi);
        var process=child.Process;
        using var cancel=token.Register(()=>{try {if(!process.HasExited)process.Kill(true);}catch{}});
        var stdout=child.StandardOutput.ReadToEndAsync(token);
        var stderr=child.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(token)).ConfigureAwait(false);
        if(process.ExitCode!=0)throw new IOException(stderr.Result);
        return stdout.Result;
    }
    public static async Task InstallFfmpegAsync(CancellationToken token=default)
    {
        string scratch=Path.Combine(Path.GetTempPath(),"vd-ffmpeg-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(6)};
            string zip=Path.Combine(scratch,"ffmpeg.zip");
            using (var response=await http.GetAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
                       HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var target=File.Create(zip);
                await response.Content.CopyToAsync(target,token).ConfigureAwait(false);
            }
            string unpack=Path.Combine(scratch,"unpacked");
            ZipFile.ExtractToDirectory(zip,unpack);
            string[] f=Directory.GetFiles(unpack,"ffmpeg.exe",SearchOption.AllDirectories);
            string[] p=Directory.GetFiles(unpack,"ffprobe.exe",SearchOption.AllDirectories);
            if(f.Length==0||p.Length==0)throw new IOException("No ffmpeg or ffprobe binary in package.");
            File.Copy(f[0],Path.Combine(AppContext.BaseDirectory,"ffmpeg.exe"),true);
            File.Copy(p[0],Path.Combine(AppContext.BaseDirectory,"ffprobe.exe"),true);
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

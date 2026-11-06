using System.Diagnostics;

namespace VideoDownloader.Native;

public enum HistoryFileState { Unknown, Present, Missing, Unavailable, Unsafe }
public sealed record HistoryFileAvailability(HistoryFileState State,string Explanation)
{
    internal static readonly HistoryFileAvailability Unknown=new(HistoryFileState.Unknown,"Не проверено");
}
internal interface IHistoryFileProbe
{
    HistoryFileAvailability Check(string path,bool directory);
}
internal sealed class LocalHistoryFileProbe:IHistoryFileProbe
{
    public HistoryFileAvailability Check(string path,bool directory)
    {
        try
        {
            string? current=path;
            while(!string.IsNullOrEmpty(current))
            {
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    return new(HistoryFileState.Unsafe,"Открытие ссылок и reparse points из истории недоступно");
                current=Path.GetDirectoryName(current);
            }
            bool isDirectory=(File.GetAttributes(path)&FileAttributes.Directory)!=0;
            if(!directory && !isDirectory)
            {
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                Span<byte> header=stackalloc byte[20];int count=input.ReadAtLeast(header,header.Length,throwOnEndOfStream:false);
                if(count>=2 && header[0]=='M' && header[1]=='Z' || count>=20 &&
                    header.SequenceEqual(new byte[]{0x4c,0,0,0,1,0x14,2,0,0,0,0,0,0xc0,0,0,0,0,0,0,0x46}))
                    return new(HistoryFileState.Unsafe,"Исполняемый файл или ярлык нельзя открыть как медиа из истории");
            }
            return isDirectory==directory?new(HistoryFileState.Present,directory?"Папка доступна":"Файл доступен"):
                new(HistoryFileState.Unavailable,directory?"По пути находится не папка":"По пути находится не файл");
        }
        catch(FileNotFoundException){return new(HistoryFileState.Missing,"Файл не найден");}
        catch(DirectoryNotFoundException){return new(HistoryFileState.Missing,"Папка или файл не найдены");}
        catch(UnauthorizedAccessException){return new(HistoryFileState.Unavailable,"Нет доступа для проверки");}
        catch(Exception error) when(error is IOException or ArgumentException or NotSupportedException)
        {return new(HistoryFileState.Unavailable,"Проверка недоступна: "+error.GetType().Name);}
    }
}

/// <summary>Checks historical paths without granting deletion ownership or blocking the UI.</summary>
internal sealed class HistoryFileAccess
{
    // An unavailable network drive can retain a synchronous OS call after a UI
    // deadline. A process-wide gate bounds such calls even after closing/reopening.
    private static readonly SemaphoreSlim ProbeSlots=new(4,4);
    private readonly IHistoryFileProbe _probe;
    private readonly TimeSpan _timeout;
    internal HistoryFileAccess(IHistoryFileProbe? probe=null,TimeSpan? timeout=null)
    {_probe=probe??new LocalHistoryFileProbe();_timeout=timeout??TimeSpan.FromSeconds(8);}
    private static readonly HashSet<string> MediaExtensions=new(StringComparer.OrdinalIgnoreCase) {
        ".mp4",".mkv",".webm",".mov",".m4v",".avi",".mp3",".m4a",".aac",".opus",".flac",".wav",".ogg",
        ".wma",".wmv",".mpeg",".mpg",".ts",".m2ts",".3gp",".3g2",".mka",".flv",".aiff",".ape",
    };
    internal static bool IsSupportedMediaPath(string path)=>TryNormalize(path,out _);
    internal static bool TryNormalize(string? path,out string normalized)
    {
        normalized="";
        try
        {
            if(string.IsNullOrWhiteSpace(path) || path.Length>32768 || path.Any(char.IsControl) ||
                path.StartsWith(@"\\?\",StringComparison.Ordinal) || path.StartsWith(@"\\.\",StringComparison.Ordinal) ||
                !Path.IsPathFullyQualified(path) || !MediaExtensions.Contains(Path.GetExtension(path)))return false;
            string[] parts=path.Replace('\\','/').Split('/');
            if(parts.Any(part=>part is "." or ".."))return false;
            if(OperatingSystem.IsWindows() && path.AsSpan(2).Contains(':'))return false;
            normalized=Path.GetFullPath(path);return true;
        }
        catch(Exception error) when(error is ArgumentException or NotSupportedException or IOException){return false;}
    }
    internal async Task<HistoryFileAvailability> CheckAsync(string path,bool directory,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!TryNormalize(path,out string normalized))return new(HistoryFileState.Unsafe,"Неподдерживаемый или небезопасный путь медиа");
        string target=directory?Path.GetDirectoryName(normalized)??"":normalized;
        if(target.Length==0)return new(HistoryFileState.Unsafe,"Папка неизвестна");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        bool entered=false;
        try
        {
            await ProbeSlots.WaitAsync(deadline.Token).ConfigureAwait(false);entered=true;
            // A disconnected SMB drive must not occupy the normal thread pool
            // used by metadata/HTTP continuations. At most four dedicated OS
            // probes can exist because their leases survive waiter timeouts.
            var task=Task.Factory.StartNew(()=>
            {
                try{return _probe.Check(target,directory);}
                catch(Exception error) when(error is not OutOfMemoryException)
                {return new HistoryFileAvailability(HistoryFileState.Unavailable,"Проверка недоступна: "+error.GetType().Name);}
                finally{ProbeSlots.Release();}
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            entered=false; // The OS call, not its waiter, owns this slot now.
            return await task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {return new(HistoryFileState.Unavailable,"Проверка не завершилась вовремя");}
        finally{if(entered)ProbeSlots.Release();}
    }
}

internal enum HistoryOpenKind { Media, Folder }
internal sealed record HistoryOpenCommand(HistoryOpenKind Kind,string MediaPath)
{
    internal string TargetPath=>Kind==HistoryOpenKind.Folder?Path.GetDirectoryName(MediaPath)!:MediaPath;
}
internal static class HistoryFileLauncher
{
    internal static void Open(HistoryOpenCommand command,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!HistoryFileAccess.TryNormalize(command.MediaPath,out string path))throw new InvalidDataException("Небезопасный путь медиа.");
        bool folder=command.Kind==HistoryOpenKind.Folder;
        string target=folder?Path.GetDirectoryName(path)!:path;
        // The window has just awaited the bounded availability probe. Do not
        // repeat a potentially blocking network-drive check outside that gate.
        cancellationToken.ThrowIfCancellationRequested();
        if(folder)
        {
            string explorer=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
            Process.Start(new ProcessStartInfo(explorer){UseShellExecute=true,ArgumentList={target}});
        }
        else Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
}

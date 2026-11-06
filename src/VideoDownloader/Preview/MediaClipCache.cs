using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

/// <summary>Owned clip leases survive failed Windows deletes and are recoverable after a crash.</summary>
internal sealed class MediaClipCache(string root)
{
    private static readonly object Gate=new();
    private static readonly Dictionary<string,Lease> Live=new(StringComparer.OrdinalIgnoreCase);
    private static readonly int ProcessId=Environment.ProcessId;
    private static readonly long ProcessStarted=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private const string Schema="VDCLIP1";
    internal Lease Reserve()
    {
        lock(Gate)
        {
            OwnedFiles.NoReparseAncestors(Path.Combine(root,"check"));Directory.CreateDirectory(root);
            foreach(var pending in Live.Values.Where(value=>value.Released).ToArray())pending.TryDelete();
            var entries=Directory.EnumerateFileSystemEntries(root).Take(1025).ToArray();
            if(entries.Length>1024)throw new MediaFrameUnavailableException("Кеш предпросмотра переполнен.");
            foreach(string marker in entries.Where(path=>Regex.IsMatch(Path.GetFileName(path),@"\A[0-9a-f]{32}\.owner\z",RegexOptions.CultureInvariant)))
            {
                string clip=Path.ChangeExtension(marker,".mp4");
                if(Live.ContainsKey(clip) || !Recognized(marker,out int pid,out long started) || IsRunning(pid,started))continue;
                try{OwnedFiles.DeleteOwned(root,new[]{clip,marker});}catch(IOException){ }catch(UnauthorizedAccessException){ }
            }
            var files=Directory.EnumerateFileSystemEntries(root).Take(1025).ToArray();long size=0;int slots=0;
            if(files.Length>1024)throw new MediaFrameUnavailableException("Кеш предпросмотра переполнен.");
            foreach(string file in files)
            {
                OwnedFiles.RegularOrAbsent(file);
                if(File.Exists(file))size=checked(size+new FileInfo(file).Length);
                if(Regex.IsMatch(Path.GetFileName(file),@"\A[0-9a-f]{32}\.owner\z",RegexOptions.CultureInvariant))slots++;
            }
            if(slots>=MediaClipService.MaxCachedClips || size>MediaClipService.MaxCachedClips*MediaClipService.MaxClipBytes-MediaClipService.MaxClipBytes+4096)
                throw new MediaFrameUnavailableException("Кеш коротких фрагментов занят. Закройте предыдущие предпросмотры.");
            string path=Path.Combine(root,Guid.NewGuid().ToString("N")+".mp4"),owner=Path.ChangeExtension(path,".owner");
            OwnedFiles.RegularOrAbsent(path);OwnedFiles.RegularOrAbsent(owner);
            bool created=false,marked=false;
            try
            {
                using(var reservation=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {created=true;}
                using(var marker=new FileStream(owner,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                {marked=true;using var text=new StreamWriter(marker);text.Write(Schema+"\n"+ProcessId.ToString(CultureInfo.InvariantCulture)+"\n"+ProcessStarted.ToString(CultureInfo.InvariantCulture)+"\n");}
                var lease=new Lease(root,path,owner);Live.Add(path,lease);return lease;
            }
            catch
            {
                try{OwnedFiles.DeleteOwned(root,(created?new[]{path}:Array.Empty<string>()).Concat(marked?new[]{owner}:Array.Empty<string>()));}catch(IOException){ }catch(UnauthorizedAccessException){ }
                throw;
            }
        }
    }
    private static bool Recognized(string marker,out int pid,out long started)
    {
        pid=0;started=0;
        try
        {
            OwnedFiles.RegularOrAbsent(marker);if(new FileInfo(marker).Length>256)return false;
            string[] values=File.ReadAllLines(marker);
            return values.Length==3 && values[0]==Schema && int.TryParse(values[1],NumberStyles.None,CultureInfo.InvariantCulture,out pid) && pid>0 &&
                long.TryParse(values[2],NumberStyles.None,CultureInfo.InvariantCulture,out started) && started>0;
        }
        catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
    }
    private static bool IsRunning(int pid,long started)
    {
        try{using var process=Process.GetProcessById(pid);return !process.HasExited && process.StartTime.ToUniversalTime().Ticks==started;}
        catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
        // A process we cannot inspect is never evidence of abandonment.
        catch(System.ComponentModel.Win32Exception){return true;}
    }
    internal sealed class Lease(string root,string path,string marker):IDisposable
    {
        internal string Path {get;}=path;
        internal bool Released {get;private set;}
        private bool _retrying;
        internal bool TryDelete()
        {
            try
            {
                OwnedFiles.DeleteOwned(root,new[]{Path});
                OwnedFiles.DeleteOwned(root,new[]{marker});Live.Remove(Path);return true;
            }
            catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        public void Dispose()
        {
            lock(Gate)
            {
                if(Released)return;Released=true;
                if(!TryDelete() && !_retrying){_retrying=true;_=RetryAsync();}
            }
        }
        private async Task RetryAsync()
        {
            for(int attempt=0;attempt<4;attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                lock(Gate)if(TryDelete())return;
            }
            // Keep this released lease and its marker charged to the cache cap.
            // A later request retries; a subsequent process reclaims dead owners.
        }
    }
}

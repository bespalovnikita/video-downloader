using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoDownloader.Native;

public enum DiskSpaceAvailability { Available, Unknown, Unavailable }

public sealed record DiskSpaceSnapshot(string Destination,string Volume,long? AvailableBytes,
    DiskSpaceAvailability Availability,string Detail)
{
    public static DiskSpaceSnapshot Unknown(string destination,string detail)=>new(destination,"",null,DiskSpaceAvailability.Unknown,detail);
    public static DiskSpaceSnapshot Unavailable(string destination,string detail)=>new(destination,"",null,DiskSpaceAvailability.Unavailable,detail);
}

public interface IDiskSpaceProvider
{
    Task<DiskSpaceSnapshot> ReadAsync(string destination,CancellationToken cancellationToken);
}

/// <summary>Local disk queries run outside WPF and never create or delete a destination.</summary>
public sealed class LocalDiskSpaceProvider:IDiskSpaceProvider
{
    private readonly SemaphoreSlim _queries;
    private readonly Func<string,DiskSpaceSnapshot> _probe;
    private readonly TimeSpan _deadline;
    public LocalDiskSpaceProvider():this(Probe,TimeSpan.FromSeconds(3),2) { }
    internal LocalDiskSpaceProvider(Func<string,DiskSpaceSnapshot> probe,TimeSpan deadline,int concurrency=2)
    {
        if(deadline<=TimeSpan.Zero || concurrency<1)throw new ArgumentOutOfRangeException(nameof(deadline));
        _probe=probe;_deadline=deadline;_queries=new(concurrency,concurrency);
    }
    public async Task<DiskSpaceSnapshot> ReadAsync(string destination,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        // A filesystem call cannot always be interrupted. A timed-out call
        // continues to hold this bounded slot until the OS has actually returned.
        var token=deadline.Token;
        var query=Task.Run(async()=>
        {
            await _queries.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return _probe(destination);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
            {
                return DiskSpaceSnapshot.Unavailable(destination,ErrorMessages.Text("Не удалось прочитать сведения о диске.","Disk information could not be read."));
            }
            finally { _queries.Release(); }
        },CancellationToken.None);
        try {return await query.WaitAsync(deadline.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
        {
            ObserveDelayedFailure(query);throw;
        }
        catch(OperationCanceledException)
        {
            // Observe a delayed probe even when this caller has already received
            // a bounded unknown result. Do not free its concurrency slot early.
            ObserveDelayedFailure(query);
            return DiskSpaceSnapshot.Unknown(destination,ErrorMessages.Text("Проверка диска не завершилась вовремя.","The disk check timed out."));
        }
    }
    private static void ObserveDelayedFailure(Task query)=>
        _=query.ContinueWith(task=>_ = task.Exception,CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    internal static DiskSpaceSnapshot Probe(string destination)
    {
        if(string.IsNullOrWhiteSpace(destination))return DiskSpaceSnapshot.Unknown(destination,ErrorMessages.Text("Выберите папку загрузок.","Choose an output directory."));
        if(destination.StartsWith(@"\\",StringComparison.Ordinal))
            return DiskSpaceSnapshot.Unknown(destination,ErrorMessages.Text("Для сетевой папки свободное место не проверяется автоматически.","Free space is not checked automatically for a network share."));
        string full=Path.GetFullPath(destination);
        string root=Path.GetPathRoot(full)??"";
        if(root.Length==0)return DiskSpaceSnapshot.Unknown(full,ErrorMessages.Text("Не удалось определить диск назначения.","The destination volume could not be resolved."));
        if(OperatingSystem.IsWindows() && new DriveInfo(root).DriveType==DriveType.Network)
            return DiskSpaceSnapshot.Unknown(full,ErrorMessages.Text("Для сетевого диска свободное место не проверяется автоматически.","Free space is not checked automatically for a network drive."));
        string? existing=full;
        while(existing is not null)
        {
            try
            {
                var attributes=File.GetAttributes(existing);
                if((attributes&FileAttributes.Directory)==0)
                    return DiskSpaceSnapshot.Unavailable(full,ErrorMessages.Text("Путь назначения не является папкой.","The destination is not a directory."));
                break;
            }
            catch(FileNotFoundException){existing=Path.GetDirectoryName(existing);}
            catch(DirectoryNotFoundException){existing=Path.GetDirectoryName(existing);}
            catch(UnauthorizedAccessException){return DiskSpaceSnapshot.Unavailable(full,ErrorMessages.Text("Нет доступа к папке назначения.","Access to the destination directory is denied."));}
            catch(IOException){return DiskSpaceSnapshot.Unavailable(full,ErrorMessages.Text("Папка или диск назначения недоступны.","The destination directory or disk is unavailable."));}
        }
        if(existing is null)return DiskSpaceSnapshot.Unavailable(full,ErrorMessages.Text("Диск назначения не найден.","The destination disk was not found."));
        // The download pipeline disallows reparse ancestors too. Do not report
        // free bytes for a different volume through an untrusted junction.
        try {OwnedFiles.NoReparseAncestors(Path.Combine(existing,"__disk_space_check__"));}
        catch(IOException){return DiskSpaceSnapshot.Unavailable(full,ErrorMessages.Text("Не удалось безопасно определить диск папки.","The directory's volume could not be resolved safely."));}
        if(OperatingSystem.IsWindows())
        {
            if(!GetDiskFreeSpaceEx(existing,out ulong available,out _,out _))
                return DiskSpaceSnapshot.Unavailable(full,ErrorMessages.Text("Диск назначения недоступен.","The destination disk is unavailable."));
            var volume=new StringBuilder(1024);
            string volumeName=GetVolumePathName(existing,volume,volume.Capacity)?volume.ToString():root;
            return new(full,volumeName,available>long.MaxValue?long.MaxValue:(long)available,
                DiskSpaceAvailability.Available,"");
        }
        var drive=new DriveInfo(root);
        return drive.IsReady?new(full,drive.Name,drive.AvailableFreeSpace,DiskSpaceAvailability.Available,""):
            DiskSpaceSnapshot.Unavailable(full,ErrorMessages.Text("Диск назначения не готов.","The destination disk is not ready."));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory,out ulong available,out ulong total,out ulong free);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string filename,StringBuilder volume,int bufferLength);
}

public sealed record DiskSpaceCandidate(string Url,string Destination,DownloadSelection? Selection,
    IReadOnlyList<DownloadSection>? Ranges=null,int ReservedCopies=1,bool ProbeOnly=false);

public sealed record DiskSpaceEstimate(long KnownPayloadBytes,long RequiredBytes,int UnknownItems,bool Approximate)
{
    public bool HasKnownSize=>KnownPayloadBytes>0;
}

public sealed record DiskSpaceVolume(DiskSpaceSnapshot Space,DiskSpaceEstimate Estimate,int ItemCount);

public sealed record DiskSpaceAssessment(IReadOnlyList<DiskSpaceVolume> Volumes)
{
    public const long LowSpaceBytes=1024L*1024*1024;
    public bool ShouldWarn=>Volumes.Any(volume=>volume.Space.AvailableBytes is {} free &&
        (volume.Estimate.RequiredBytes>free || volume.Estimate.UnknownItems>0 && free<LowSpaceBytes));
}

public static class DiskSpaceEstimator
{
    public static DiskSpaceEstimate Estimate(IEnumerable<DiskSpaceCandidate> candidates)
    {
        decimal total=0,reserved=0;int unknown=0;bool approximate=false;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var candidate in candidates)
        {
            if(candidate.ProbeOnly)continue;
            var selection=candidate.Selection;
            bool first=seen.Add(candidate.Url);
            if(selection?.Format is not {} primary){if(first)unknown++;continue;}
            long? main=primary.EstimatedSizeBytes is >0?primary.EstimatedSizeBytes:null;
            long? audio=selection.AudioFormat is {} secondary?secondary.EstimatedSizeBytes is >0?secondary.EstimatedSizeBytes:null:0;
            if(first && (main is null || audio is null))unknown++;
            decimal bytes=(decimal)(main??0)+(audio??0);
            approximate|=primary.SizeIsApproximate || selection.AudioFormat?.SizeIsApproximate==true;
            var ranges=candidate.Ranges??selection.EffectiveSections;
            if(ranges.Count>0)
            {
                approximate=true;
                if(selection.DurationSeconds is {} duration && double.IsFinite(duration) && duration>0)
                {
                    decimal fraction=0;
                    foreach(var range in ranges)
                    {
                        if(!double.IsFinite(range.StartSeconds) || !double.IsFinite(range.EndSeconds) ||
                            range.StartSeconds<0 || range.EndSeconds<=range.StartSeconds || range.EndSeconds>duration)
                            fraction+=1;
                        else fraction+=(decimal)((range.EndSeconds-range.StartSeconds)/duration);
                    }
                    bytes*=fraction;
                }
                // Every range publishes its own file. Without source duration,
                // reserve one full stream per range rather than invent a ratio.
                else bytes*=ranges.Count;
            }
            bytes=Math.Min(long.MaxValue,decimal.Ceiling(bytes));
            if(first)total=Math.Min(long.MaxValue,total+bytes);
            reserved=Math.Min(long.MaxValue,reserved+bytes*Math.Clamp(candidate.ReservedCopies,1,4));
        }
        long known=(long)total;
        // Callers reserve input+merge copies on the actual temporary volume and
        // a published copy on the output volume. These are planning allowances,
        // never guarantees about the eventual codec size.
        return new(known,(long)reserved,unknown,approximate);
    }
}

public sealed class DiskSpaceService
{
    private readonly IDiskSpaceProvider _provider;
    public DiskSpaceService(IDiskSpaceProvider provider)=>_provider=provider;
    public Task<DiskSpaceAssessment> AssessAsync(IReadOnlyList<DiskSpaceCandidate> candidates,CancellationToken cancellationToken)=>
        Task.Run(()=>AssessCoreAsync(candidates,cancellationToken),CancellationToken.None);
    private async Task<DiskSpaceAssessment> AssessCoreAsync(IReadOnlyList<DiskSpaceCandidate> candidates,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        // Subscription folders and dated archive directories cannot cross a
        // volume through a safe folder leaf. Probe each drive/share once.
        var groups=candidates.GroupBy(candidate=>RootKey(candidate.Destination),comparer).ToArray();
        var result=new List<DiskSpaceVolume>();
        foreach(var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items=group.ToArray();
            DiskSpaceSnapshot space;
            try {space=await _provider.ReadAsync(items[0].Destination,cancellationToken).ConfigureAwait(false);}
            catch(OperationCanceledException){throw;}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {space=DiskSpaceSnapshot.Unavailable(items[0].Destination,ErrorMessages.Text("Не удалось прочитать сведения о диске.","Disk information could not be read."));}
            if(space.Availability!=DiskSpaceAvailability.Available || space.AvailableBytes is null or <0)
                space=space with {AvailableBytes=null,Availability=space.Availability==DiskSpaceAvailability.Available?DiskSpaceAvailability.Unknown:space.Availability};
            result.Add(new(space,DiskSpaceEstimator.Estimate(items),items.Where(item=>!item.ProbeOnly).Select(item=>item.Url).Distinct(StringComparer.Ordinal).Count()));
        }
        return new(result);
    }
    internal static string RootKey(string destination)
    {
        try
        {
            if(destination.StartsWith(@"\\",StringComparison.Ordinal))
            {
                string[] parts=destination.TrimStart('\\').Split('\\',StringSplitOptions.RemoveEmptyEntries);
                return parts.Length>=2?@"\\"+parts[0]+"\\"+parts[1]:destination;
            }
            return Path.GetPathRoot(Path.GetFullPath(destination))??destination;
        }
        catch(Exception error) when(error is ArgumentException or NotSupportedException){return destination;}
    }
}

/// <summary>One warning on entry to low space; enough recovery rearms it.</summary>
internal sealed class DiskSpacePressure
{
    private readonly HashSet<string> _warned=new(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
    internal void Reset()=>_warned.Clear();
    internal bool Observe(DiskSpaceSnapshot space)
    {
        if(space.Availability!=DiskSpaceAvailability.Available || space.AvailableBytes is not {} available)return false;
        string key=space.Volume.Length>0?space.Volume:DiskSpaceService.RootKey(space.Destination);
        if(available>=DiskSpaceAssessment.LowSpaceBytes+DiskSpaceAssessment.LowSpaceBytes/4){_warned.Remove(key);return false;}
        return available<DiskSpaceAssessment.LowSpaceBytes && _warned.Add(key);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoDownloader.Native;

/// <summary>Owns suspension state only for registered download process trees.</summary>
internal sealed class WindowsDownloadPause
{
    private readonly ConcurrentDictionary<string,Process> _processes=new(StringComparer.Ordinal);
    private readonly Dictionary<int,DateTime> _suspended=new();
    private readonly object _guard=new();
    private volatile bool _paused;
    internal bool IsPaused=>_paused;

    internal void SetPaused(bool pause)
    {
        lock(_guard)
        {
            if (_paused==pause)return;
            _paused=pause;
            if(!pause){ResumeSuspended();return;}
            foreach(var process in _processes.Values)
            {
                try
                {
                    if(process.HasExited)continue;
                    foreach(int pid in ProcessTree(process.Id))
                        if(!_suspended.ContainsKey(pid)) SuspendProcess(pid);
                }
                catch(InvalidOperationException){}
            }
        }
    }

    internal async Task WaitForResumeAsync(CancellationToken token)
    {
        while(_paused) await Task.Delay(100,token).ConfigureAwait(false);
    }

    internal void Register(string url,Process process)
    {
        _processes[url]=process;
        lock(_guard)
        {
            if(_paused) SuspendProcess(process.Id);
        }
    }

    internal void Unregister(string url)=>_processes.TryRemove(url,out _);

    // The caller retains cancellation/termination ownership. Resuming a paused
    // root here preserves the existing kill-tree cancellation path.
    internal void ResumeForCancellation(Process process)
    {
        if (_paused) NtResumeProcess(process.Handle);
    }

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr handle);
    private const uint SnapshotProcess=0x00000002;
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size,Usage,ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId,Threads,ParentId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string ExeFile;
    }

    private static IReadOnlyList<int> ProcessTree(int root)
    {
        var parents=new Dictionary<int,int>();
        var snapshot=CreateToolhelp32Snapshot(SnapshotProcess,0);
        if (snapshot!=new IntPtr(-1))
        {
            try
            {
                var item=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),ExeFile=""};
                if(Process32FirstW(snapshot,ref item))
                    do { parents[(int)item.ProcessId]=(int)item.ParentId; } while(Process32NextW(snapshot,ref item));
            }
            finally {CloseHandle(snapshot);}
        }
        var discovered=new List<int>{root};
        var seen=new HashSet<int>{root};
        for(int i=0;i<discovered.Count;i++)
        {
            foreach(var (pid,parent) in parents)
                if(parent==discovered[i]&&seen.Add(pid))discovered.Add(pid);
        }
        return discovered;
    }

    private void SuspendProcess(int pid)
    {
        try
        {
            using var process=Process.GetProcessById(pid);
            if(!process.HasExited && NtSuspendProcess(process.Handle)==0)
                _suspended[pid]=process.StartTime.ToUniversalTime();
        }
        catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
    }

    private void ResumeSuspended()
    {
        foreach(var (pid,started) in _suspended)
        {
            try
            {
                using var process=Process.GetProcessById(pid);
                if(!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime()-started).TotalSeconds)<2)
                    NtResumeProcess(process.Handle);
            }
            catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
        _suspended.Clear();
    }
}

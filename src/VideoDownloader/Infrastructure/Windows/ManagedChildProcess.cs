using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VideoDownloader.Native;

/// <summary>A redirected child with a Windows job assigned atomically at creation.</summary>
public sealed class ManagedChildProcess : IDisposable
{
    public Process Process { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    private readonly SafeFileHandle? _job;
    private ManagedChildProcess(Process process,StreamReader stdout,StreamReader stderr,SafeFileHandle? job)
        => (Process,StandardOutput,StandardError,_job)=(process,stdout,stderr,job);

    public static ManagedChildProcess Start(ProcessStartInfo start)
    {
        if(start.UseShellExecute || !start.RedirectStandardOutput || !start.RedirectStandardError)
            throw new ArgumentException("Managed children require redirected output and UseShellExecute=false.");
        if(!OperatingSystem.IsWindows())
        {
            var process=Process.Start(start)??throw new IOException("Cannot start child process.");
            return new(process,process.StandardOutput,process.StandardError,null);
        }
        return StartWindows(start);
    }

    private static ManagedChildProcess StartWindows(ProcessStartInfo start)
    {
        var job=CreateJobObjectW(IntPtr.Zero,null);
        SafeFileHandle? outputRead=null,errorRead=null;
        StreamReader? stdout=null,stderr=null;
        Process? process=null;
        IntPtr attributes=IntPtr.Zero,jobValue=IntPtr.Zero,handles=IntPtr.Zero,environment=IntPtr.Zero;
        bool attributesReady=false;
        try
        {
            if(job.IsInvalid) throw Error("Cannot create child-process job");
            var limits=new ExtendedLimits();
            limits.Basic.LimitFlags=0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>()))
                throw Error("Cannot configure child-process job");
            using var outputWrite=Pipe(out outputRead);
            using var errorWrite=Pipe(out errorRead);
            using var inputWrite=Pipe(out var inputRead);
            using var input=inputRead;
            nuint size=0;
            InitializeProcThreadAttributeList(IntPtr.Zero,2,0,ref size);
            attributes=Marshal.AllocHGlobal(checked((int)size));
            if(!InitializeProcThreadAttributeList(attributes,2,0,ref size)) throw Error("Cannot initialize child attributes");
            attributesReady=true;
            jobValue=Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobValue,job.DangerousGetHandle());
            // JOB_LIST closes the start/assign race: the child is born in its
            // own job, not in a job containing the GUI or external applications.
            if(!UpdateProcThreadAttribute(attributes,0,0x2000D,jobValue,(nuint)IntPtr.Size,IntPtr.Zero,IntPtr.Zero))
                throw Error("Cannot set child job at process creation");
            handles=Marshal.AllocHGlobal(3*IntPtr.Size);
            if(!SetHandleInformation(input,1,1)) throw Error("Cannot prepare child input");
            var childHandles=new[]{input.DangerousGetHandle(),outputWrite.DangerousGetHandle(),errorWrite.DangerousGetHandle()};
            for(int i=0;i<childHandles.Length;i++) Marshal.WriteIntPtr(handles,i*IntPtr.Size,childHandles[i]);
            // Only these handles may be inherited. In particular, neither this
            // job handle nor another worker's handles can keep a job alive.
            if(!UpdateProcThreadAttribute(attributes,0,0x20002,handles,(nuint)(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero))
                throw Error("Cannot set child pipe handles");
            var startup=new StartupInfoEx { Attributes=attributes };
            startup.Startup.Size=(uint)Marshal.SizeOf<StartupInfoEx>();
            startup.Startup.Flags=0x100; // STARTF_USESTDHANDLES
            startup.Startup.Input=childHandles[0];startup.Startup.Output=childHandles[1];startup.Startup.Error=childHandles[2];
            string command=Quote(start.FileName)+(start.ArgumentList.Count>0
                ? " "+string.Join(" ",start.ArgumentList.Select(Quote))
                : string.IsNullOrEmpty(start.Arguments) ? "" : " "+start.Arguments);
            string block=string.Join('\0',start.Environment.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase)
                .Select(p=>p.Key+"="+(p.Value??"")))+"\0\0";
            environment=Marshal.StringToHGlobalUni(block);
            // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT
            if(!CreateProcessW(start.FileName,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,true,
                0x08080400,environment,string.IsNullOrEmpty(start.WorkingDirectory)?null:start.WorkingDirectory,
                ref startup,out var info)) throw Error("Cannot start protected child process");
            using(var initialProcess=new SafeFileHandle(info.Process,true))
            using(var initialThread=new SafeFileHandle(info.Thread,true))
            {
                process=Process.GetProcessById((int)info.ProcessId);
                _=process.SafeHandle;
            }
            stdout=new StreamReader(new FileStream(outputRead,FileAccess.Read),start.StandardOutputEncoding??Encoding.UTF8);
            outputRead=null;
            stderr=new StreamReader(new FileStream(errorRead,FileAccess.Read),start.StandardErrorEncoding??Encoding.UTF8);
            errorRead=null;
            return new(process,stdout,stderr,job);
        }
        catch
        {
            job.Dispose();
            stdout?.Dispose();stderr?.Dispose();process?.Dispose();
            throw;
        }
        finally
        {
            outputRead?.Dispose();errorRead?.Dispose();
            if(attributesReady) DeleteProcThreadAttributeList(attributes);
            if(attributes!=IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if(jobValue!=IntPtr.Zero) Marshal.FreeHGlobal(jobValue);
            if(handles!=IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if(environment!=IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    private static SafeFileHandle Pipe(out SafeFileHandle read)
    {
        var security=new SecurityAttributes { Size=Marshal.SizeOf<SecurityAttributes>(),Inherit=true };
        if(!CreatePipe(out read,out var write,ref security,0)) throw Error("Cannot create child pipe");
        if(!SetHandleInformation(read,1,0)) {var error=Error("Cannot protect parent pipe");read.Dispose();write.Dispose();throw error;}
        return write;
    }
    private static Win32Exception Error(string message)=>new(Marshal.GetLastWin32Error(),message);
    private static string Quote(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\') {slashes++;continue;}
            result.Append('\\',c=='"'?slashes*2+1:slashes).Append(c);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    public void Dispose()
    {
        _job?.Dispose();
        StandardOutput.Dispose();StandardError.Dispose();Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime,JobTime;public uint LimitFlags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public uint Size;public IntPtr Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;public ushort Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup;public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process,Thread;public uint ProcessId,ThreadId; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job,int infoClass,ref ExtendedLimits limits,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref SecurityAttributes security,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref nuint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,nuint attribute,IntPtr value,nuint size,IntPtr previous,IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfoEx startup,out ProcessInfo info);
}

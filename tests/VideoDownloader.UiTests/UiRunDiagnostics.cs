using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace VideoDownloader.UiTests;

// Observes this isolated WPF host only. The UI stack distinguishes a nested
// modal loop from a dispatcher that has stopped delivering heartbeats.
internal sealed class UiRunDiagnostics : IDisposable
{
    private readonly Stopwatch _elapsed=Stopwatch.StartNew();
    private readonly string _mode;
    private readonly string? _path;
    private readonly string? _statePath;
    private readonly Dispatcher _dispatcher=Dispatcher.CurrentDispatcher;
    private readonly uint _thread=GetCurrentThreadId();
    private readonly DispatcherTimer? _timer;
    private readonly System.Threading.Timer? _stateTimer;
    private long _lastUiCapture=Stopwatch.GetTimestamp();
    private int _stateSampleActive;
    private volatile bool _disposed;

    internal UiRunDiagnostics(string mode)
    {
        _mode=mode;
        string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
        if(string.IsNullOrWhiteSpace(result))return;
        _path=result+".diagnostics.txt";
        _statePath=result+".dispatcher-state.txt";
        Capture("start");
        _timer=new DispatcherTimer(DispatcherPriority.Send,Dispatcher.CurrentDispatcher)
        {Interval=TimeSpan.FromSeconds(2)};
        _timer.Tick+=Heartbeat;
        _timer.Start();
        CaptureDispatcherState(true);
        _stateTimer=new System.Threading.Timer(_=>CaptureDispatcherState(false),null,
            TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(2));
    }

    private void Heartbeat(object? sender,EventArgs args)=>Capture("heartbeat");

    private void Capture(string reason)
    {
        if(_disposed || _path is null)return;
        Interlocked.Exchange(ref _lastUiCapture,Stopwatch.GetTimestamp());
        try
        {
            var text=new StringBuilder();
            text.AppendLine($"UTC={DateTimeOffset.UtcNow:O}; elapsed={_elapsed.Elapsed.TotalSeconds:F3}s; mode={_mode}; reason={reason}; pid={Environment.ProcessId}; UI thread={_thread}");
            text.AppendLine("SynchronizationContext="+(SynchronizationContext.Current?.GetType().FullName??"none"));
            text.AppendLine("Managed UI stack:");
            string stack=Environment.StackTrace;
            text.AppendLine(stack.Length<=48*1024?stack:stack[..(48*1024)]+"\n[stack truncated]");
            text.AppendLine("Native UI thread windows (up to 32):");
            int count=0;
            EnumThreadWindows(_thread,(handle,_)=>
            {
                // Capture runs on this same owning UI thread. Standard native
                // and WPF top-level captions need no cross-thread SendMessage.
                var caption=new StringBuilder(256);
                var type=new StringBuilder(128);
                GetWindowTextW(handle,caption,caption.Capacity);
                GetClassNameW(handle,type,type.Capacity);
                text.AppendLine($"HWND=0x{handle.ToInt64():X}; owner=0x{GetWindow(handle,4).ToInt64():X}; class={type}; visible={IsWindowVisible(handle)}; enabled={IsWindowEnabled(handle)}; title={caption}");
                return ++count<32;
            },IntPtr.Zero);
            // Preserve the latest heartbeat on Dispose. Its timestamp remains
            // useful when the UI later blocks before the process deadline.
            File.WriteAllText(_path,text.ToString());
        }
        catch(Exception)
        {
            // Optional diagnostics must never change a scenario's result.
        }
    }

    private static object? Field(object? target,string name)=>target?.GetType()
        .GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(target);
    private static object? Property(object? target,string name)=>target?.GetType()
        .GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(target);

    private void CaptureDispatcherState(bool starting)
    {
        if(_disposed || _statePath is null)return;
        double age=Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUiCapture)).TotalSeconds;
        if(!starting && age<6 || Interlocked.CompareExchange(ref _stateSampleActive,1,0)!=0)return;
        try
        {
            var text=new StringBuilder();
            text.AppendLine($"UTC={DateTimeOffset.UtcNow:O}; elapsed={_elapsed.Elapsed.TotalSeconds:F3}s; mode={_mode}; reason={(starting?"start":"stale UI heartbeat")}; UI heartbeat age={age:F3}s");
            text.AppendLine($"pid={Environment.ProcessId}; UI thread={_thread}; TickCount={Environment.TickCount}; TickCount64={Environment.TickCount64}");
            IntPtr handle=IntPtr.Zero;
            object? sync=Field(_dispatcher,"_instanceLock");
            bool entered=false;
            try
            {
                if(sync is not null)Monitor.TryEnter(sync,0,ref entered);
                if(!entered)text.AppendLine("Dispatcher instance lock unavailable; private snapshot skipped.");
                else
                {
                    // Read only. No Dispatcher operations, wake messages or
                    // state changes may be issued from this observer.
                    foreach(string name in new[]{"_hasRequestProcessingFailed","_postedProcessingType","_dueTimeFound",
                        "_dueTimeInTicks","_isWin32TimerSet","_isTSFMessagePumpEnabled","_disableProcessingCount",
                        "_hasShutdownStarted","_hasShutdownFinished","_startingShutdown","_frameDepth","_exitAllFrames"})
                        text.AppendLine(name+"="+(Field(_dispatcher,name)??"<missing>"));
                    text.AppendLine("_timers.Count="+((Field(_dispatcher,"_timers") as ICollection)?.Count.ToString()??"<missing>"));
                    text.AppendLine("_queue.MaxPriority="+(Property(Field(_dispatcher,"_queue"),"MaxPriority")??"<missing>"));
                    // In WPF 8 these two getters only return stored values;
                    // HwndWrapper.Handle explicitly supports other threads.
                    handle=Property(Property(Field(_dispatcher,"_window"),"Value"),"Handle") is IntPtr hwnd?hwnd:IntPtr.Zero;
                    if(Field(_dispatcher,"_hasRequestProcessingFailed") is true &&
                        Field(_dispatcher,"_reservedPtsCache") is Tuple<object,List<string>> failure)
                        foreach(string entry in failure.Item2.TakeLast(10))
                            text.AppendLine("request processing failure: "+entry[..Math.Min(entry.Length,512)]);
                }
            }
            finally{if(entered)Monitor.Exit(sync!);}
            // Native reads and file IO occur after releasing the Dispatcher
            // lock. A busy UI never makes this observer wait for that lock.
            text.AppendLine($"Dispatcher HWND=0x{handle.ToInt64():X}; IsWindow={handle!=IntPtr.Zero && IsWindow(handle)}");
            using(var process=Process.GetCurrentProcess())
                text.AppendLine($"GUI resources: USER={GetGuiResources(process.Handle,1)}; GDI={GetGuiResources(process.Handle,0)}");
            byte[] bytes=Encoding.UTF8.GetBytes(text.ToString());
            if(bytes.Length>32*1024)
            {
                int length=32*1024;
                while(length>0 && (bytes[length]&0xC0)==0x80)length--;
                Array.Resize(ref bytes,length);
            }
            if(!_disposed)File.WriteAllBytes(_statePath,bytes);
        }
        catch(Exception)
        {
            // Private framework fields may change. Diagnostic failure must
            // never alter the test or hold up cancellation/disposal.
        }
        finally{Interlocked.Exchange(ref _stateSampleActive,0);}
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _stateTimer?.Dispose(); // nonblocking; never join the UI from Dispose
        if(_timer is null)return;
        _timer.Stop();
        _timer.Tick-=Heartbeat;
    }

    private delegate bool WindowCallback(IntPtr window,IntPtr argument);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread,WindowCallback callback,IntPtr argument);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process,uint flags);
}

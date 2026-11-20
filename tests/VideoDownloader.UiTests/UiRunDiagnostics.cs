using System.Diagnostics;
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
    private readonly uint _thread=GetCurrentThreadId();
    private readonly DispatcherTimer? _timer;
    private bool _disposed;

    internal UiRunDiagnostics(string mode)
    {
        _mode=mode;
        string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
        if(string.IsNullOrWhiteSpace(result))return;
        _path=result+".diagnostics.txt";
        Capture("start");
        _timer=new DispatcherTimer(DispatcherPriority.Send,Dispatcher.CurrentDispatcher)
        {Interval=TimeSpan.FromSeconds(2)};
        _timer.Tick+=Heartbeat;
        _timer.Start();
    }

    private void Heartbeat(object? sender,EventArgs args)=>Capture("heartbeat");

    private void Capture(string reason)
    {
        if(_disposed || _path is null)return;
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

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
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
}

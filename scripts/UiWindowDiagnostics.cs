using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// Observation only: never sends input or dismisses a test window.
public static class VdUiWindowDiagnostics
{
    private delegate bool EnumerateWindow(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback,IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
        IntPtr window,uint message,UIntPtr length,StringBuilder text,uint flags,uint timeout,out UIntPtr result);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);

    public static string Capture(int processId)
    {
        var lines=new List<string>();
        EnumWindows((window,state)=>
        {
            uint ownerProcess;
            uint thread=GetWindowThreadProcessId(window,out ownerProcess);
            if(ownerProcess!=(uint)processId)return true;
            var name=new StringBuilder(128);GetClassName(window,name,name.Capacity);
            var title=new StringBuilder(512);UIntPtr copied;
            bool answered=SendMessageTimeout(window,0x000d,(UIntPtr)title.Capacity,title,0x0002,50,out copied)!=IntPtr.Zero;
            lines.Add("HWND=0x"+window.ToInt64().ToString("x")+" thread="+thread+" class="+name+
                " visible="+IsWindowVisible(window)+" enabled="+IsWindowEnabled(window)+
                " owner=0x"+GetWindow(window,4).ToInt64().ToString("x")+
                " title="+(answered?title.ToString():"<WM_GETTEXT timed out>"));
            return lines.Count<16;
        },IntPtr.Zero);
        return lines.Count==0?"No top-level windows for WPF process "+processId:string.Join(Environment.NewLine,lines);
    }
}

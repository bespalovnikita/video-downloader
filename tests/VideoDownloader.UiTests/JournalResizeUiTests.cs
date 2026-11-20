using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Point=System.Windows.Point;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestJournalResize(Window window)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Journal resizing WPF test failed: "+message);}
        void Until(Func<bool> condition,string message,Func<string>? evidence=null)
        {
            window.UpdateLayout();
            if(condition())return;
            var clock=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{window.UpdateLayout();if(condition() || clock.Elapsed>TimeSpan.FromSeconds(3))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message+(evidence is null?"":" "+evidence()));
        }
        T Control<T>(string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing journal resizing control: "+name));
        void Click(string name)=>Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SetLayoutTestBounds(window,1500,900,compact:false);
        var expander=Control<Expander>("JournalExpander");
        var body=Control<Grid>("JournalBody");
        var handle=Control<Thumb>("JournalResizeHandle");
        var downloads=Control<DataGrid>("DownloadGrid");
        var layout=Control<Grid>("DownloadsContentGrid");
        var log=Control<TextBox>("LogBox");
        var controls=Control<ScrollViewer>("JournalControlsScrollViewer");
        expander.IsExpanded=true;window.UpdateLayout();window.Activate();
        Require(handle.IsVisible && handle.ActualHeight>=12,"The expanded journal must expose a usable resize handle.");
        var peer=UIElementAutomationPeer.CreatePeerForElement(handle);
        Require(peer is not null && peer.GetName()=="Высота журнала" && peer.GetHelpText().Contains("Home"),
            "The actual resize handle's automation peer must describe its name and keyboard controls.");

        var dragDeltas=new List<string>();
        double minimum=double.NaN,maximum=double.NaN;
        string Geometry()=>$"window={window.ActualWidth:0.##}x{window.ActualHeight:0.##}; layout={layout.ActualWidth:0.##}x{layout.ActualHeight:0.##}; "+
            $"body height={body.Height:0.##}, actual={body.ActualHeight:0.##}, min={minimum:0.##}, max={maximum:0.##}; rows={string.Join(",",layout.RowDefinitions.Select(row=>row.ActualHeight.ToString("0.##")))}; "+
            $"expander={expander.ActualHeight:0.##}; handleOrigin={handle.TranslatePoint(new Point(),layout)}; mouseHandle={Mouse.GetPosition(handle)}; "+
            $"mouseLayout={Mouse.GetPosition(layout)}; cursor={System.Windows.Forms.Cursor.Position}; button={Mouse.LeftButton}; "+
            $"dragging={handle.IsDragging}; captured={handle.IsMouseCaptured}; deltas=[{string.Join(" | ",dragDeltas)}]";
        // Establish room for the measured gesture on this actual viewport,
        // independently of the journal's default or a temporary height clamp.
        PressKey(0x23,Key.End);
        maximum=body.ActualHeight;
        PressKey(0x24,Key.Home);
        minimum=body.ActualHeight;
        Require(maximum-minimum>=80,$"The shown viewport must have at least 80 DIP of drag room (minimum={minimum:0.##}, maximum={maximum:0.##}). "+Geometry());

        nint hwnd=new WindowInteropHelper(window).Handle;
        nint Move(Point screen,nuint buttons=0)
        {
            System.Windows.Forms.Cursor.Position=new System.Drawing.Point((int)Math.Round(screen.X),(int)Math.Round(screen.Y));
            var point=new WaveformMouseInput.DevicePoint{X=(int)Math.Round(screen.X),Y=(int)Math.Round(screen.Y)};
            Require(WaveformMouseInput.ScreenToClient(hwnd,ref point),"Native mouse input needs client coordinates of the shown window.");
            nint coordinates=(point.Y<<16)|(point.X&0xffff);
            _=WaveformMouseInput.SendMessage(hwnd,0x0200,buttons,coordinates);
            return coordinates;
        }
        Point local=new(handle.ActualWidth/2,handle.ActualHeight/2);
        Point start=handle.PointToScreen(local);
        Move(start);
        Until(()=>handle.IsMouseOver && Math.Abs(Mouse.GetPosition(handle).X-local.X)<1.5 && Math.Abs(Mouse.GetPosition(handle).Y-local.Y)<1.5,
            "WPF must acknowledge the native resize target before the mouse button goes down.",Geometry);
        double before=body.ActualHeight;
        DragDeltaEventHandler observeDelta=(_,e)=>
        {
            if(dragDeltas.Count<32)dragDeltas.Add($"{e.HorizontalChange:0.##},{e.VerticalChange:0.##} -> height={body.Height:0.##}/actual={body.ActualHeight:0.##}");
        };
        handle.DragDelta+=observeDelta;
        try
        {
            // Thumb.OnMouseMove checks the real MouseDevice.LeftButton state.
            // Sending WM_LBUTTONDOWN directly starts capture without pressing
            // the system button; the next move then cancels that fake drag.
            // Queue real button input and release it unconditionally below.
            JournalMouseEvent(0x0002,0,0,0,UIntPtr.Zero);
            Until(()=>handle.IsDragging && handle.IsMouseCaptured && Mouse.LeftButton==MouseButtonState.Pressed,
                "Native button-down must hold the real mouse button and start the Thumb drag.",Geometry);
            double scale=VisualTreeHelper.GetDpi(handle).DpiScaleY;
            Move(new Point(start.X,start.Y-48*scale),1);
            Until(()=>Math.Abs(body.ActualHeight-before-48)<2,"Dragging the upper divider upward must give the journal more height.",Geometry);
            Move(new Point(start.X,start.Y-80*scale),1);
            Until(()=>Math.Abs(body.ActualHeight-before-80)<2 && handle.IsMouseCaptured,
                "A continuous native drag must retain capture and accumulate height changes.",Geometry);
        }
        finally
        {
            JournalMouseEvent(0x0004,0,0,0,UIntPtr.Zero);
            handle.DragDelta-=observeDelta;
        }
        Until(()=>!handle.IsMouseCaptured && Mouse.LeftButton==MouseButtonState.Released,
            "Completing the native drag must release capture and the system mouse button.",Geometry);

        void PressKey(byte virtualKey,Key expected)
        {
            window.Activate();Keyboard.Focus(handle);
            Require(handle.IsKeyboardFocused,"The visible height handle must accept keyboard focus.");
            bool received=false;
            System.Windows.Input.KeyEventHandler observed=(_,e)=>{if(e.Key==expected)received=true;};
            handle.AddHandler(Keyboard.PreviewKeyDownEvent,observed,true);
            try
            {
                QueueRemovalKeyboardEvent(virtualKey,0,0x0001,UIntPtr.Zero);
                QueueRemovalKeyboardEvent(virtualKey,0,0x0003,UIntPtr.Zero);
                Until(()=>received,"The height handle must receive the actual native "+expected+" key.",Geometry);
            }
            finally{handle.RemoveHandler(Keyboard.PreviewKeyDownEvent,observed);}
        }
        double dragged=body.ActualHeight;
        PressKey(0x28,Key.Down);
        Require(Math.Abs(body.ActualHeight-dragged+16)<1,"Down must reduce the journal height by a keyboard step.");
        PressKey(0x26,Key.Up);
        Require(Math.Abs(body.ActualHeight-dragged)<1,"Up must restore the previous journal height.");
        var focusBorder=(Border)handle.Template.FindName("JournalResizeChrome",handle);
        Require(Equals(focusBorder.BorderBrush,window.FindResource("FocusBrush")),"Keyboard focus must have a visible theme-aware border.");
        PressKey(0x24,Key.Home);
        Require(Math.Abs(body.ActualHeight-140)<1 && log.ActualHeight>=30,"Home must retain a readable minimum journal.");
        PressKey(0x23,Key.End);
        Require(body.ActualHeight>dragged && downloads.ActualHeight>=90,
            "End must grow the journal while keeping a usable download list.");
        PressKey(0x28,Key.Down);
        double chosen=body.ActualHeight;
        expander.IsExpanded=false;window.UpdateLayout();
        expander.IsExpanded=true;window.UpdateLayout();
        Require(Math.Abs(body.ActualHeight-chosen)<1,"Collapse and reopen must preserve the chosen height.");

        SetLayoutTestBounds(window,800,550,compact:true);Click("ShowDownloadsPanelButton");
        Until(()=>body.ActualHeight<chosen && log.ActualHeight>=30,"A short window must temporarily clamp the journal and keep its text visible.");
        double minimumList=Math.Min(96,layout.ActualHeight/4);
        Require(downloads.ActualHeight>=minimumList-2 && controls.ViewportHeight>15,
            "A short viewport must retain its download list and independently scrollable journal actions.");
        var fileAction=Control<Button>("OpenUrlButton");
        double fileBottom=fileAction.TranslatePoint(new Point(0,fileAction.ActualHeight),layout).Y;
        Require(fileBottom<=layout.ActualHeight && body.TranslatePoint(new Point(0,body.ActualHeight),layout).Y<=layout.ActualHeight+1,
            "The journal and file actions must stay inside the short Downloads panel.");
        Click("ShowQueuePanelButton");Click("ShowDownloadsPanelButton");
        SetLayoutTestBounds(window,1500,900,compact:false);
        Until(()=>Math.Abs(body.ActualHeight-chosen)<1,
            "Growing the window after compact navigation must restore the user's height, not save the temporary clamp.");
        Console.WriteLine("PASS: journal native drag, keyboard bounds, focus, collapse/reopen and short viewport preserve a usable Downloads panel");
    }

    [DllImport("user32.dll",EntryPoint="mouse_event")]
    private static extern void JournalMouseEvent(uint flags,uint dx,uint dy,uint data,UIntPtr extraInfo);
}

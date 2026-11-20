using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Binding=System.Windows.Data.Binding;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using Point=System.Windows.Point;
using ToolTip=System.Windows.Controls.ToolTip;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestToolTipTimers()
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Tooltip timer WPF test: "+message);}

        foreach(string mode in new[]{"default","explicit shorter","bound maximum"})
        {
            var tooltip=new ToolTip{Content="Длинную подсказку можно читать, пока указатель остаётся над кнопкой."};
            var button=new Button{Content="Подсказка",ToolTip=tooltip,Width=200,Height=50,
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            var window=new Window{Title="Tooltip timer regression: "+mode,Width=380,Height=220,
                WindowStartupLocation=WindowStartupLocation.CenterScreen,ShowInTaskbar=false,Content=button};
            // This is the same shared resource entry point used by MainWindow.xaml.
            window.Resources.MergedDictionaries.Add(new WindowThemeResources());
            ToolTipService.SetInitialShowDelay(button,0);
            ToolTipService.SetBetweenShowDelay(button,0);
            if(mode=="explicit shorter")ToolTipService.SetShowDuration(button,45_000);
            BindingExpressionBase? binding=null;
            if(mode=="bound maximum")binding=BindingOperations.SetBinding(button,ToolTipService.ShowDurationProperty,
                new Binding{Source=int.MaxValue});
            var shortTimer=new DispatcherTimer(DispatcherPriority.Send){Interval=TimeSpan.FromMilliseconds(1)};
            var frame=new DispatcherFrame();
            bool opened=false,opening=false,nativeMoveSeen=false,firedWhileOpen=false;
            int timeout=0,durationAtOpening=0;double overdueMilliseconds=0;
            tooltip.Opened+=(_,_)=>opened=true;
            shortTimer.Tick+=(_,_)=>
            {
                shortTimer.Stop();firedWhileOpen=tooltip.IsOpen;frame.Continue=false;
            };
            button.ToolTipOpening+=(_,_)=>
            {
                opening=true;durationAtOpening=ToolTipService.GetShowDuration(button);
                var clock=Stopwatch.StartNew();shortTimer.Start();
                // No message pump here: make the short timer overdue BEFORE
                // PopupControlService creates the tooltip's long duration timer.
                Thread.Sleep(40);
                overdueMilliseconds=clock.Elapsed.TotalMilliseconds;
            };
            button.MouseMove+=(_,e)=>
            {
                Point position=e.GetPosition(button);
                nativeMoveSeen=button.IsMouseOver && Math.Abs(position.X-button.ActualWidth/2)<2 &&
                    Math.Abs(position.Y-button.ActualHeight/2)<2;
            };
            System.Threading.Timer? watchdog=null;
            try
            {
                window.Show();window.Activate();window.UpdateLayout();
                nint hwnd=new WindowInteropHelper(window).Handle;
                void Move(Point screen)
                {
                    System.Windows.Forms.Cursor.Position=new System.Drawing.Point((int)Math.Round(screen.X),(int)Math.Round(screen.Y));
                    var point=new WaveformMouseInput.DevicePoint{X=(int)Math.Round(screen.X),Y=(int)Math.Round(screen.Y)};
                    Require(WaveformMouseInput.ScreenToClient(hwnd,ref point),"Native hover needs client coordinates.");
                    _=WaveformMouseInput.SendMessage(hwnd,0x0200,0,(point.Y<<16)|(point.X&0xffff));
                }
                // A separate timeout closes only this fixture tooltip through a
                // normal public Dispatcher operation. It cannot make a late
                // short-timer tick count as success, nor extend the host deadline.
                watchdog=new System.Threading.Timer(_=>
                {
                    Interlocked.Exchange(ref timeout,1);
                    _=window.Dispatcher.BeginInvoke(DispatcherPriority.Send,new Action(()=>
                    {tooltip.IsOpen=false;frame.Continue=false;}));
                },null,TimeSpan.FromSeconds(2),Timeout.InfiniteTimeSpan);
                Move(window.PointToScreen(new Point(5,5)));
                Move(button.PointToScreen(new Point(button.ActualWidth/2,button.ActualHeight/2)));
                Require(nativeMoveSeen && button.IsMouseOver,"WPF MouseDevice must acknowledge the actual native hover.");
                Dispatcher.PushFrame(frame);
                watchdog.Dispose();watchdog=null;
                Require(Volatile.Read(ref timeout)==0 && opening && opened && overdueMilliseconds>=30 && firedWhileOpen && tooltip.IsOpen,
                    $"The overdue short timer must fire while the actual {mode} tooltip remains open; opening={opening}; opened={opened}; overdue={overdueMilliseconds:F1}ms; duration={durationAtOpening}; firedWhileOpen={firedWhileOpen}; timeout={timeout}.");
                int expected=mode=="explicit shorter"?45_000:WindowThemeResources.ToolTipShowDurationMilliseconds;
                Require(durationAtOpening==expected && ToolTipService.GetShowDuration(button)==expected,
                    "Only the unsafe maximum default may be bounded; shorter explicit reading time must remain unchanged.");
                if(binding is not null)Require(ReferenceEquals(binding,BindingOperations.GetBindingExpressionBase(button,ToolTipService.ShowDurationProperty)),
                    "Bounding the effective duration must preserve its binding expression.");
                Move(window.PointToScreen(new Point(5,5)));
                var closedFrame=new DispatcherFrame();
                var closeDeadline=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
                closeDeadline.Tick+=(_,_)=>closedFrame.Continue=false;
                tooltip.Closed+=(_,_)=>closedFrame.Continue=false;
                if(tooltip.IsOpen)
                {
                    closeDeadline.Start();try{Dispatcher.PushFrame(closedFrame);}finally{closeDeadline.Stop();}
                }
                Require(!tooltip.IsOpen,"Moving the real pointer away must still dismiss the tooltip.");
            }
            finally
            {
                watchdog?.Dispose();shortTimer.Stop();tooltip.IsOpen=false;window.Close();
            }
        }
        Console.WriteLine("PASS: native tooltip hover preserves overdue DispatcherTimer ticks, long reading time, explicit duration, binding and pointer dismissal");
    }
}

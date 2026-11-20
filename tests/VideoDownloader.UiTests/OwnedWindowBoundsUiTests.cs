using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using Point=System.Windows.Point;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestOwnedWindowBounds(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Owned dialog work-area WPF test: "+message);}
        static void Until(Func<bool> condition,string message,Func<string>? evidence=null)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            var elapsed=Stopwatch.StartNew();timer.Tick+=(_,_)=>{if(condition() || elapsed.Elapsed>TimeSpan.FromSeconds(5))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message+(evidence is null?"":" "+evidence()));
        }
        static void Flush()
        {
            var frame=new DispatcherFrame();
            _=Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
        }
        static string FocusName(IInputElement? element)=>element switch
        {
            FrameworkElement view=>$"{view.GetType().Name}[{view.Name}]",
            FrameworkContentElement content=>$"{content.GetType().Name}[{content.Name}]",
            _=>element?.GetType().Name??"null"
        };
        static bool NextFocus(IInputElement? element)=>element switch
        {
            UIElement view=>view.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),
            ContentElement content=>content.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),
            _=>false
        };
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing actual bounded-dialog control: "+name));
        static IEnumerable<DependencyObject> Children(DependencyObject root)
        {
            yield return root;
            for(int n=0;n<VisualTreeHelper.GetChildrenCount(root);n++)
                foreach(var child in Children(VisualTreeHelper.GetChild(root,n)))yield return child;
        }
        static bool Reachable(Window window,FrameworkElement control)
        {
            if(!control.IsVisible || control.ActualWidth<=0 || control.ActualHeight<=0 || window.Content is not FrameworkElement content)return false;
            var bounds=control.TransformToAncestor(content).TransformBounds(new Rect(new Point(),control.RenderSize));
            return bounds.Left>=-1 && bounds.Top>=-1 && bounds.Right<=content.ActualWidth+1 && bounds.Bottom<=content.ActualHeight+1;
        }
        static bool Fits(Window window,Rect area)=>window.IsVisible && window.ActualWidth>0 && window.ActualHeight>0 &&
            window.Left>=area.Left-1 && window.Top>=area.Top-1 && window.Left+window.ActualWidth<=area.Right+1 && window.Top+window.ActualHeight<=area.Bottom+1;

        // Physical-monitor conversion is checked separately from the actual
        // windows below; this does not claim to change CI's real OS DPI.
        foreach(double scale in new[]{1.25,1.5,2d})
        {
            var pixels=new Rect(-1920,40,1920,1040);
            var dips=OwnedWindowLayout.DeviceBoundsToDips(pixels,new Matrix(1/scale,0,0,1/scale,0,0));
            Require(Math.Abs(dips.Width-1920/scale)<.01 && Math.Abs(dips.Height-1040/scale)<.01 &&
                Math.Abs(dips.Left+1920/scale)<.01 && Math.Abs(dips.Top-40/scale)<.01,
                "125/150/200 percent conversion must preserve the negative-coordinate active monitor's DIP work area.");
        }
        string root=Path.Combine(testRoot,"owned-layout-"+Guid.NewGuid().ToString("N"));
        using var input=XmlReader.Create(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        var owner=(Window)XamlReader.Load(input);owner.Show();
        var storage=new AppPersistence(root);using var journal=new DiagnosticJournal(Path.Combine(root,"logs"),JournalProfile.Errors);
        var target=JobDiagnosticTarget.Capture(new DownloadItem{Url="https://layout.invalid/job",Title="Проверка геометрии",Status="Failed"});
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IAsyncDisposable> HoldMaintenance(CancellationToken token)
        {entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return new BoundsMaintenanceLease();}
        var backup=new ProfileBackupWindow(owner,new WindowsProfileBackup(storage),HoldMaintenance,
            ()=>new DownloadOptions(),()=>[],_=>Task.CompletedTask,_=>{},new(_=>null,_=>Path.Combine(root,"not-created.vdbackup")));
        var history=new DownloadHistoryWindow(owner,storage);
        var diagnostics=new JobDiagnosticsWindow(owner,target,new JobDiagnosticsService(journal,"must-not-launch"),()=>journal.Sequence);
        var help=NativeWindow.CreateFilenameTemplateHelp(owner);
        using var duplicateReader=XmlReader.Create(Path.Combine(AppContext.BaseDirectory,"ui","DuplicateDownloadDialog.xaml"));
        var duplicate=(Window)XamlReader.Load(duplicateReader);duplicate.Owner=owner;duplicate.Resources.MergedDictionaries.Add(owner.Resources);
        ((TextBox)duplicate.FindName("DuplicateUrl")!).Text="https://layout.invalid/video?"+new string('x',2048);
        OwnedWindowLayout.Attach(duplicate,owner);
        var dialogs=new[]{backup,history,diagnostics,help,duplicate};
        try
        {
            foreach(var window in dialogs)
            {
                try
                {
                    window.Show();window.UpdateLayout();Flush();window.Activate();
                    Until(()=>window.IsLoaded && window.IsActive,
                        $"{window.Title} must finish loading and activate before its keyboard path is exercised.",
                        ()=>$"loaded={window.IsLoaded}, active={window.IsActive}, focused={FocusName(Keyboard.FocusedElement)}.");
                    var close=window==help?Children((DependencyObject)window.Content).OfType<Button>().Single(button=>Equals(button.Content,"Закрыть")):
                        Control<Button>(window,window==backup?"CloseProfileBackupButton":window==history?"CloseHistoryButton":window==diagnostics?"CloseJobDiagnosticsButton":"CancelButton");
                    var area=new Rect(10,10,640,420);var layout=OwnedWindowLayout.Attach(window,owner);layout.Refresh(area);
                    Until(()=>Fits(window,area) && Reachable(window,close),
                        $"{window.Title} must fit a 640x420 DIP work area and keep its real Close/Cancel outside scrolling content; actual {window.ActualWidth}x{window.ActualHeight}.");
                    var focusPath=new List<string>();
                    string FocusEvidence()=>$"window={window.Title}, active={window.IsActive}, focusWithin={window.IsKeyboardFocusWithin}, "+
                        $"focused={FocusName(Keyboard.FocusedElement)}, close={close.Name}, visible={close.IsVisible}, "+
                        $"enabled={close.IsEnabled}, focusable={close.Focusable}, tabStop={close.IsTabStop}, "+
                        $"reachable={Reachable(window,close)}, path={string.Join(" -> ",focusPath)}.";
                    close.Focus();
                    Until(()=>close.IsKeyboardFocusWithin,
                        "The bounded dialog's visible Close/Cancel must accept real keyboard focus.",FocusEvidence);
                    Require(window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                        "The bounded dialog must support navigation to its first keyboard target. "+FocusEvidence());
                    Flush();
                    Until(()=>window.IsKeyboardFocusWithin,
                        "The first keyboard navigation target must belong to the active bounded dialog.",FocusEvidence);
                    for(int step=0;step<40 && !close.IsKeyboardFocusWithin;step++)
                    {
                        focusPath.Add(FocusName(Keyboard.FocusedElement));
                        // Tab navigation starts at the current focus, rather than repeatedly at the Window.
                        if(!NextFocus(Keyboard.FocusedElement))break;
                        Flush();
                    }
                    Require(close.IsKeyboardFocusWithin && Reachable(window,close),
                        "The bounded dialog's Close/Cancel must remain reachable through the actual keyboard focus path. "+FocusEvidence());
                    if(window==backup)
                    {
                        Control<Button>(backup,"CreateProfileBackupButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Until(()=>backup.IsBusy && entered.Task.IsCompleted,"The controlled backup preparation must enter its cancellable owner lease.");
                        var cancel=Control<Button>(backup,"CancelProfileBackupOperationButton");
                        Require(cancel.IsEnabled && Reachable(backup,cancel) && Reachable(backup,close),
                            "An active backup must keep Cancel and Close in the visible footer at the constrained work area.");
                        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Until(()=>!backup.IsBusy && backup.ActiveOperation.IsCompleted,"Cancelling the constrained backup must join its operation.");
                        Require(!File.Exists(Path.Combine(root,"not-created.vdbackup")),"Layout and cancelled preparation must not write a profile archive.");
                    }
                    var larger=new Rect(10,10,960,540);layout.Refresh(larger);
                    Until(()=>Fits(window,larger) && Reachable(window,close),"Changing to a larger DIP work area must restore usable constraints and keep Close visible.");
                }
                finally{window.Close();}
            }
            Console.WriteLine("PASS: owned Windows dialogs fit monitor DIP bounds, preserve reachable close/cancel and release cancelled backup preparation");
        }
        finally
        {
            owner.Close();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    private sealed class BoundsMaintenanceLease:IAsyncDisposable
    {public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
}

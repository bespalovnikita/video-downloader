using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;
using TabControl=System.Windows.Controls.TabControl;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestCompactPanelLayout(Window window,NativeWindow subject)
    {
        static T Control<T>(Window owner,string name) where T:class=>(T)(owner.FindName(name)
            ??throw new InvalidOperationException("Missing compact layout test control: "+name));
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Compact layout WPF test failed: "+message);}
        static void Click(Window owner,string name)
        {Control<Button>(owner,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));owner.UpdateLayout();}
        static bool Inside(Window owner,FrameworkElement element)
        {
            if(!element.IsVisible || element.ActualWidth<=0 || element.ActualHeight<=0)return false;
            var content=(FrameworkElement)owner.Content;
            var bounds=element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            return bounds.Left>=-1 && bounds.Top>=-1 && bounds.Right<=content.ActualWidth+1 && bounds.Bottom<=content.ActualHeight+1;
        }
        var before=subject.ReadOptions();
        double wideQueue=before.QueuePanelWidth,widePreview=before.PreviewPanelWidth;
        window.Width=800;window.Height=550;window.UpdateLayout();
        Require(Control<FrameworkElement>(window,"CompactNavigation").IsVisible,
            "An 800 x 550 window must offer navigation between all three areas.");
        Require(Control<FrameworkElement>(window,"QueuePanel").IsVisible &&
            !Control<FrameworkElement>(window,"DownloadsPanel").IsVisible && !Control<FrameworkElement>(window,"PreviewPanel").IsVisible,
            "Compact mode starts with the queue, without off-screen competing panels.");
        foreach(string name in new[]{"UrlInput","PasteClipboardButton","AddUrlButton","StartButton","PauseButton","StopButton"})
            Require(Inside(window,Control<FrameworkElement>(window,name)),name+" must remain inside the compact window.");
        Require(Control<DataGrid>(window,"QueueGrid").ActualHeight>=30,
            "The default compact queue retains a usable list area.");
        var compactOptions=subject.ReadOptions();
        Require(Math.Abs(compactOptions.QueuePanelWidth-wideQueue)<1 && Math.Abs(compactOptions.PreviewPanelWidth-widePreview)<1,
            "Temporary compact columns must not overwrite the saved wide layout.");

        Click(window,"ShowDownloadsPanelButton");
        Require(Control<FrameworkElement>(window,"DownloadsPanel").IsVisible && !Control<FrameworkElement>(window,"QueuePanel").IsVisible,
            "Downloads navigation must reveal the full list in the same viewport.");
        Require(!Control<Expander>(window,"JournalExpander").IsExpanded,"The journal is optional by default.");
        RaiseCompactSearchShortcut(window);
        Require(Control<FrameworkElement>(window,"QueuePanel").IsVisible &&
            !Control<FrameworkElement>(window,"DownloadsPanel").IsVisible &&
            Control<TextBox>(window,"QueueSearchBox").IsKeyboardFocusWithin,
            "Ctrl+F from compact Downloads must reveal the queue and focus its actual search field.");
        Click(window,"OpenQueueOptionsButton");
        Require(Control<TabItem>(window,"QueueOptionsTab").IsSelected && Control<FrameworkElement>(window,"PreviewPanel").IsVisible,
            "Folder and quality must open the queue-wide settings directly, including compact mode.");
        Control<FrameworkElement>(window,"OutputBox").BringIntoView();window.UpdateLayout();
        Require(Inside(window,Control<FrameworkElement>(window,"OutputBox")),"The output folder must be reachable in queue options.");
        var tabs=Control<TabControl>(window,"DetailsTabs");
        tabs.SelectedItem=Control<TabItem>(window,"MaintenanceTab");window.UpdateLayout();
        Require(Control<TabItem>(window,"MaintenanceTab").IsSelected,"Maintenance has its own explicit section.");
        tabs.SelectedItem=Control<TabItem>(window,"SelectedVideoTab");window.UpdateLayout();
        Require(Control<FrameworkElement>(window,"PreviewImageHost").IsVisible,"The selected video stays independently reachable.");
        RaiseCompactSearchShortcut(window);
        Require(Control<FrameworkElement>(window,"QueuePanel").IsVisible &&
            Control<TextBox>(window,"QueueSearchBox").IsKeyboardFocusWithin,
            "Ctrl+F from compact video/settings must return to the visible search rather than a collapsed control.");
        Click(window,"ShowDetailsPanelButton");
        Require(AutomationProperties.GetName(Control<TextBox>(window,"OutputBox")).Length>0 &&
            AutomationProperties.GetName(Control<Button>(window,"PreviewNextButton")).Length>0,
            "Text inputs and icon actions expose meaningful accessible names.");

        window.Width=1500;window.Height=900;window.UpdateLayout();
        Require(!Control<FrameworkElement>(window,"CompactNavigation").IsVisible && Control<FrameworkElement>(window,"PreviewPanel").IsVisible,
            "A wide window returns to simultaneous panels.");
        Click(window,"ToggleDetailsPanelButton");
        Require(!Control<FrameworkElement>(window,"PreviewPanel").IsVisible &&
            Control<ColumnDefinition>(window,"PreviewPanelColumn").ActualWidth<1,
            "Hiding details returns its space to the working lists.");
        Require(Math.Abs(subject.ReadOptions().PreviewPanelWidth-widePreview)<1,
            "Hiding details must retain the preferred preview width.");
        Click(window,"ToggleDetailsPanelButton");
        Require(Control<FrameworkElement>(window,"PreviewPanel").IsVisible,"Details must reopen without losing their contents.");
        Click(window,"ShowQueuePanelButton");
    }

    // Keyboard.Modifiers reads this UI thread's real Win32 key state. Set and
    // restore it around the routed key event, without sending input to another
    // application or leaving Ctrl held for later tests.
    private static void RaiseCompactSearchShortcut(Window window)
    {
        var previous=new byte[256];
        if(!ReadCompactKeyboardState(previous))throw new InvalidOperationException("Cannot read the UI thread keyboard state.");
        var pressed=(byte[])previous.Clone();pressed[0x11]|=0x80;pressed[0xA2]|=0x80;
        try
        {
            if(!WriteCompactKeyboardState(pressed))throw new InvalidOperationException("Cannot set the UI thread Ctrl state.");
            var source=PresentationSource.FromVisual(window)??throw new InvalidOperationException("Compact window must have a real presentation source.");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.F)
                {RoutedEvent=Keyboard.PreviewKeyDownEvent});
            window.UpdateLayout();
        }
        finally{WriteCompactKeyboardState(previous);}
    }

    [DllImport("user32.dll",EntryPoint="GetKeyboardState")]
    private static extern bool ReadCompactKeyboardState([Out] byte[] state);
    [DllImport("user32.dll",EntryPoint="SetKeyboardState")]
    private static extern bool WriteCompactKeyboardState(byte[] state);
}

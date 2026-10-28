using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestPanelLayout(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Panel layout WPF test failed: "+message);
        }
        static Window LoadWindow()
        {
            using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(stream);
            return (Window)XamlReader.Load(reader);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing panel layout test control: "+name));
        static void Drag(Window window,string name,double change)
        {
            var splitter=Control<GridSplitter>(window,name);
            splitter.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            splitter.RaiseEvent(new DragDeltaEventArgs(change,0){RoutedEvent=Thumb.DragDeltaEvent});
            window.UpdateLayout();
            splitter.RaiseEvent(new DragCompletedEventArgs(change,0,false){RoutedEvent=Thumb.DragCompletedEvent});
            window.UpdateLayout();
        }

        var storage=new AppPersistence(Path.Combine(testRoot,"panel-layout"));
        double savedQueue=0,savedPreview=0;
        var window=LoadWindow();
        NativeWindow? subject=null;
        try
        {
            subject=new NativeWindow(window,storage,"must-not-launch");
            window.Show();
            window.UpdateLayout();
            var queue=Control<ColumnDefinition>(window,"QueuePanelColumn");
            var downloads=Control<ColumnDefinition>(window,"DownloadsPanelColumn");
            var preview=Control<ColumnDefinition>(window,"PreviewPanelColumn");
            string Widths()=>$"Queue={queue.ActualWidth:0.###}, Downloads={downloads.ActualWidth:0.###}, Preview={preview.ActualWidth:0.###}, grid={Control<Grid>(window,"MainPanelsGrid").ActualWidth:0.###}";
            Require(Math.Abs(queue.ActualWidth-380d*4/3)<1,
                "The default Queue panel must be one third wider than its original 380 pixels.");
            double originalQueue=queue.ActualWidth,originalDownloads=downloads.ActualWidth,originalPreview=preview.ActualWidth;
            // A Windows runner may constrain the window to its work area.
            // Release space first so subsequent positive drags have room even
            // when Downloads initially has only its minimum width.
            Drag(window,"QueueDownloadsSplitter",-72);
            Require(Math.Abs(queue.ActualWidth-originalQueue+72)<2 &&
                    Math.Abs(downloads.ActualWidth-originalDownloads-72)<2 &&
                    Math.Abs(preview.ActualWidth-originalPreview)<1,
                $"Dragging the first real GridSplitter must resize Queue and Downloads without moving Preview. Before: Queue={originalQueue}, Downloads={originalDownloads}, Preview={originalPreview}. After: {Widths()}");
            Drag(window,"QueueDownloadsSplitter",24);
            Require(Math.Abs(queue.ActualWidth-originalQueue+48)<2 &&
                    Math.Abs(downloads.ActualWidth-originalDownloads-48)<2 &&
                    Math.Abs(preview.ActualWidth-originalPreview)<1,
                $"The first splitter must also grow Queue when Downloads has room. {Widths()}");
            double beforeRightDownloads=downloads.ActualWidth;
            Drag(window,"DownloadsPreviewSplitter",-36);
            Require(Math.Abs(preview.ActualWidth-originalPreview-36)<2 &&
                    Math.Abs(downloads.ActualWidth-beforeRightDownloads+36)<2,
                $"Dragging the second real GridSplitter must resize Downloads and Preview. {Widths()}");
            var options=subject.ReadOptions();
            savedQueue=options.QueuePanelWidth;savedPreview=options.PreviewPanelWidth;
            Require(Math.Abs(savedQueue-queue.ActualWidth)<1 && Math.Abs(savedPreview-preview.ActualWidth)<1,
                "Options must capture the adjusted panel widths.");
            var persisted=storage.Restore()!;
            Require(Math.Abs(persisted.Settings.QueuePanelWidth-savedQueue)<1 &&
                    Math.Abs(persisted.Settings.PreviewPanelWidth-savedPreview)<1,
                "Completing a drag must persist layout even when no download is running.");
            ProfileCatalog.Apply(options,"Music MP3");
            subject.ApplyOptions(options);
            window.UpdateLayout();
            Require(Math.Abs(queue.ActualWidth-savedQueue)<1 && Math.Abs(preview.ActualWidth-savedPreview)<1,
                "Changing a download profile must preserve the user's panel widths.");
        }
        finally {subject?.Dispose();window.Close();}

        window=LoadWindow();subject=null;
        try
        {
            subject=new NativeWindow(window,storage,"must-not-launch");
            window.Show();window.UpdateLayout();
            var grid=Control<Grid>(window,"MainPanelsGrid");
            var queue=Control<ColumnDefinition>(window,"QueuePanelColumn");
            var downloads=Control<ColumnDefinition>(window,"DownloadsPanelColumn");
            var preview=Control<ColumnDefinition>(window,"PreviewPanelColumn");
            Require(Math.Abs(queue.ActualWidth-savedQueue)<1 && Math.Abs(preview.ActualWidth-savedPreview)<1,
                "A fresh real application window must restore saved widths from an empty-queue session.");

            subject.ApplyOptions(new DownloadOptions{QueuePanelWidth=double.NaN,PreviewPanelWidth=-10});
            window.UpdateLayout();
            Require(Math.Abs(queue.Width.Value-380d*4/3)<1 && Math.Abs(preview.Width.Value-390)<1,
                "Invalid saved widths must fall back to usable defaults.");
            using(var json=JsonDocument.Parse(JsonSerializer.Serialize(subject.ReadOptions(),AppPersistence.Json)))
                Require(double.IsFinite(json.RootElement.GetProperty("QueuePanelWidth").GetDouble()),
                    "Invalid input must not leak non-finite values into checkpoint JSON.");

            window.Width=window.MinWidth;
            window.UpdateLayout();
            subject.ApplyOptions(new DownloadOptions{QueuePanelWidth=6000,PreviewPanelWidth=6000});
            window.UpdateLayout();
            double total=grid.ColumnDefinitions.Sum(column=>column.ActualWidth);
            Require(queue.ActualWidth>=queue.MinWidth-1 && downloads.ActualWidth>=downloads.MinWidth-1 &&
                    preview.ActualWidth>=preview.MinWidth-1 && total<=grid.ActualWidth+1 && downloads.Width.IsStar,
                "Restoring wide-screen dimensions on a smaller window must keep all panels visible and Downloads flexible.");
            double previewBeforeLarge=preview.ActualWidth;
            Drag(window,"QueueDownloadsSplitter",10000);
            total=grid.ColumnDefinitions.Sum(column=>column.ActualWidth);
            Require(downloads.ActualWidth>=downloads.MinWidth-1 &&
                    Math.Abs(preview.ActualWidth-previewBeforeLarge)<1 && total<=grid.ActualWidth+1,
                $"A constrained Queue drag must keep Downloads visible without shrinking Preview. Queue={queue.ActualWidth}, Downloads={downloads.ActualWidth}, Preview before={previewBeforeLarge}, after={preview.ActualWidth}, grid={grid.ActualWidth}, total={total}.");
            double queueBeforeLarge=queue.ActualWidth;
            Drag(window,"DownloadsPreviewSplitter",-10000);
            total=grid.ColumnDefinitions.Sum(column=>column.ActualWidth);
            Require(downloads.ActualWidth>=downloads.MinWidth-1 &&
                    Math.Abs(queue.ActualWidth-queueBeforeLarge)<1 && total<=grid.ActualWidth+1,
                $"A constrained Preview drag must keep Downloads visible without shrinking Queue. Queue before={queueBeforeLarge}, after={queue.ActualWidth}, Downloads={downloads.ActualWidth}, Preview={preview.ActualWidth}, grid={grid.ActualWidth}, total={total}.");
            Require(double.IsPositiveInfinity(queue.MaxWidth) && double.IsPositiveInfinity(preview.MaxWidth),
                "Temporary drag bounds must be restored after each completed drag.");

            var oldSettings=JsonSerializer.Deserialize<DownloadOptions>("{\"Profile\":\"Universal\"}",AppPersistence.Json)!;
            Require(Math.Abs(oldSettings.QueuePanelWidth-380d*4/3)<1 && oldSettings.PreviewPanelWidth==390,
                "Older settings without panel widths must receive the new defaults without a format migration.");
            Console.WriteLine("PASS: real WPF panel splitters, wider Queue, profile/settings persistence and small-window restoration");
        }
        finally {subject?.Dispose();window.Close();}
    }
}

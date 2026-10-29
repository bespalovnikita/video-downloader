using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    // Real grid cells bind to independent rows. There is no controller/worker:
    // only the production XAML and the production change notifications run.
    private static void TestDownloadProgressStatus()
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Progress status WPF test failed: "+message);
        }
        static void Pump()
        {
            var frame=new DispatcherFrame();
            _=System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        var originalCulture=CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        Window? window=null;
        try
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            var rows=new[] {
                new DownloadItem {Url="https://status.invalid/video",Status="Running",State="Downloading",ProgressDetail="Видео 1/2 · 100%"},
                new DownloadItem {Url="https://status.invalid/audio",Status="Running",State="Downloading",ProgressDetail="Аудио 2/2 · 18%",RawError="HTTP Error 403: Forbidden"},
                new DownloadItem {Url="https://status.invalid/processing",Status="Running",State="Processing"},
                new DownloadItem {Url="https://status.invalid/retry",Status="Running",State="Retrying",ProgressDetail="попытка 2 · 720p",RawError="Fixture retry diagnostic"}
            };
            var queue=(DataGrid)window.FindName("QueueGrid")!;
            var downloads=(DataGrid)window.FindName("DownloadGrid")!;
            queue.ItemsSource=rows;
            downloads.ItemsSource=rows;
            window.Show();

            void CheckRows()
            {
                Pump();
                foreach(var grid in new[]{queue,downloads})
                {
                    var column=grid.Columns.OfType<DataGridTextColumn>().Single(candidate=>
                        (candidate.Binding as Binding)?.Path?.Path=="StateLabel");
                    Require(column.Width.Value==(ReferenceEquals(grid,queue)?90:110),
                        "Long status labels must retain the configured column widths");
                    foreach(var row in rows)
                    {
                        grid.ScrollIntoView(row);
                        grid.UpdateLayout();
                        var cell=column.GetCellContent(row) as TextBlock;
                        Require(cell!=null && cell.Text==row.StateLabel && cell.ToolTip as string==row.StateToolTip,
                            "Each rendered cell and its complete tooltip must follow its own row: "+row.Url);
                        Require(cell!.TextWrapping==TextWrapping.Wrap && cell.TextTrimming==TextTrimming.None,
                            "Component and retry labels must wrap without ellipsis");
                        var expected=row.Status=="Completed"?Color.FromRgb(0x4A,0xDE,0x80):Color.FromRgb(0x67,0xD7,0xFD);
                        Require((cell.Foreground as SolidColorBrush)?.Color==expected,
                            "Phase text must preserve the color selected by stable Status");
                    }
                }
            }

            CheckRows();
            Require(rows[0].StateToolTip=="Видео 1/2 · 100%" &&
                    rows[1].StateToolTip=="Аудио 2/2 · 18%"+Environment.NewLine+rows[1].ErrorMessage &&
                    rows[3].StateToolTip.Contains("Повторная попытка",StringComparison.Ordinal),
                "Tooltips must include the complete current phase and only that row's error");
            rows[0].ProgressDetail="Аудио 2/2 · 0%";
            rows[1].ProgressDetail="Аудио 2/2 · 100%";
            rows[1].RawError="";
            CheckRows();
            Require(rows[0].StateLabel=="Аудио 2/2 · 0%" && rows[1].StateToolTip=="Аудио 2/2 · 100%",
                "Video-to-audio changes and cleared diagnostics must notify both grid bindings immediately");
            rows[0].State="Processing";
            rows[1].State="Processing";
            rows[2].State="Publishing";
            rows[3].State="Downloading";
            rows[3].ProgressDetail="Видео 1/2 · 35%";
            rows[3].RawError="";
            CheckRows();
            Require(rows[0].StateLabel=="Обработка медиа" && rows[0].Status=="Running" &&
                    rows[2].StateToolTip=="Сохранение файла",
                "Reaching a component's 100% must show processing/publishing while stable Status remains Running");
            rows[0].Status="Completed";
            rows[0].State="Done";
            CheckRows();
            Console.WriteLine("PASS: real Queue/Downloads phase bindings, video/audio/processing transitions, wrapped text, per-row tooltip and stable status colors");
        }
        finally
        {
            window?.Close();
            CultureInfo.CurrentUICulture=originalCulture;
        }
    }
}

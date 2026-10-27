using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    // Construct the actual controls under each UI language. Numeric/date
    // formatting deliberately uses the opposite culture: it must not select
    // the language of errors. No URL is requested and no worker is launched.
    private static void TestErrorLocalization(string testRoot)
    {
        static void Require(bool condition,string error)
        {
            if(!condition)throw new InvalidOperationException("Error localization WPF test failed: "+error);
        }
        static Window LoadWindow()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            return (Window)XamlReader.Load(reader);
        }
        static bool RussianText(string text)=>text.Any(character=>character is >= '\u0400' and <= '\u04ff');

        var originalUiCulture=CultureInfo.CurrentUICulture;
        var originalCulture=CultureInfo.CurrentCulture;
        const string raw="ERROR: [youtube] fixture: HTTP Error 403: Forbidden";
        const string url="https://example.invalid/localized-error";
        try
        {
            foreach(string language in new[]{"ru-RU","ru-KZ","en-US"})
            {
                bool russian=language.StartsWith("ru",StringComparison.Ordinal);
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(russian?"en-US":"ru-RU");
                var storage=new AppPersistence(Path.Combine(testRoot,"error-localization-"+language));
                var window=LoadWindow();
                NativeWindow? subject=null;
                try
                {
                    subject=new NativeWindow(window,storage,"must-not-launch");
                    window.Show();
                    var metadata=(TextBlock)window.FindName("PreviewMeta")!;
                    var title=(TextBlock)window.FindName("PreviewTitle")!;
                    var preview=subject.Preview;
                    preview.SelectedUrl=url;
                    preview.ReceiveFailure(url,raw);
                    if(russian)
                    {
                        Require(metadata.Text!=raw && RussianText(metadata.Text) &&
                                metadata.Text.Contains("доступ",StringComparison.OrdinalIgnoreCase),
                            "A Russian UI must show an explanation of denied access, not the English diagnostic: "+language);
                    }
                    else Require(metadata.Text==raw,"A non-Russian UI must preserve the original diagnostic.");
                    Require(metadata.ToolTip as string==raw,"The preview tooltip must keep the exact original diagnostic.");
                    string failureSummary=metadata.Text;
                    preview.ReceiveFailure(url+"/other","ERROR: HTTP Error 404: Not Found");
                    Require(metadata.Text==failureSummary && metadata.ToolTip as string==raw,
                        "A stale failure for another URL must not change the selected error or its original details.");

                    var row=new DownloadItem{Url=url,Status="Failed",State="Failed",RawError=raw};
                    Require(row.RawError==raw && row.Status=="Failed" && row.State=="Failed",
                        "Presentation must not translate raw diagnostics or machine-readable states.");
                    Require(row.ErrorMessage==failureSummary,
                        "Download rows and preview errors must use the same display-only explanation.");
                    string json=JsonSerializer.Serialize(row,AppPersistence.Json);
                    using(var document=JsonDocument.Parse(json))
                    {
                        Require(document.RootElement.GetProperty("RawError").GetString()==raw &&
                                !document.RootElement.TryGetProperty("ErrorMessage",out _) &&
                                !document.RootElement.TryGetProperty("StateLabel",out _),
                            "Session JSON must store the exact diagnostic without a localized display field.");
                    }

                    var download=(DataGrid)window.FindName("DownloadGrid")!;
                    subject.SessionState.Queue.Add(row);
                    subject.SessionState.Downloads.Add(row);
                    foreach(var grid in new[]{(DataGrid)window.FindName("QueueGrid")!,download})
                    {
                        grid.ScrollIntoView(row);
                        grid.UpdateLayout();
                        var stateColumn=grid.Columns.OfType<DataGridTextColumn>().Single(column=>
                            (column.Binding as Binding)?.Path?.Path=="StateLabel");
                        var cell=stateColumn.GetCellContent(row) as TextBlock;
                        Require(cell!=null && cell.Text==(russian?"Ошибка":"Failed") &&
                                cell.ToolTip as string==failureSummary,
                            "Both actual grids must render the localized state and error explanation.");
                        Require((cell!.Foreground as SolidColorBrush)?.Color==Color.FromRgb(0xFB,0x71,0x85),
                            "Failed-row colors must still follow the stable Status identifier.");
                    }
                    string[] headers=download.ContextMenu.Items.OfType<MenuItem>()
                        .Select(item=>item.Header?.ToString()??"").ToArray();
                    Require(headers.Contains(russian?"Показать ошибку":"Show error") &&
                            headers.Contains(russian?"Исходная диагностика":"Show raw error"),
                        "Download actions must distinguish the error explanation from original diagnostic details.");

                    var definition=new SubscriptionDefinition{Id="source",Url=url,Initialized=true,LastError=raw};
                    var source=new SubscriptionController.SubscriptionSourceView(definition);
                    Require(source.Message==failureSummary && source.RawMessage==raw && definition.LastError==raw,
                        "Subscription rows must translate their display without rewriting stored LastError.");
                    definition.LastError="";
                    definition.LastWarning=raw;
                    Require(source.Message==failureSummary && source.RawMessage==raw && definition.LastWarning==raw,
                        "Persisted subscription warnings must receive the same display-only translation.");

                    preview.ReceiveReady(new PreviewData(url,"Название после повтора","","",0,"",0,0,"","","",DateTime.UtcNow));
                    Require(title.Text=="Название после повтора" && metadata.ToolTip==null,
                        "Successful metadata must remove the previous error tooltip.");
                }
                finally
                {
                    subject?.Dispose();
                    window.Close();
                }
            }
            Console.WriteLine("PASS: native Russian error display follows UI culture; preview, download actions, subscription diagnostics and JSON stay consistent");
        }
        finally
        {
            CultureInfo.CurrentUICulture=originalUiCulture;
            CultureInfo.CurrentCulture=originalCulture;
        }
    }
}

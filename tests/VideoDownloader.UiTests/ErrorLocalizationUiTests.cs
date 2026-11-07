using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
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
        static IEnumerable<string> RenderedText(DependencyObject parent)
        {
            if(parent is TextBlock label)yield return label.Text;
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
                foreach(string text in RenderedText(VisualTreeHelper.GetChild(parent,index)))yield return text;
        }
        static void AwaitPresentation(Func<bool> ready,Func<string> failure)
        {
            if(ready())return;
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            var deadline=DateTime.UtcNow.AddSeconds(5);
            timer.Tick+=(_,_)=>{if(ready() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{Dispatcher.PushFrame(frame);}
            finally{timer.Stop();}
            Require(ready(),failure());
        }

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
                    Require(((Button)window.FindName("OpenListButton")!).Content.ToString()=="Открыть список",
                        "The actual main window must translate Open list.");
                    Require(((CheckBox)window.FindName("EmbedThumbnailCheck")!).Content.ToString()=="Встроить обложку" &&
                            ((Button)window.FindName("InstallFfmpegButton")!).Content.ToString()=="Установить FFmpeg",
                        "The actual settings and component buttons must use Russian captions, keeping the FFmpeg brand.");
                    foreach(var (name,id,caption,readId) in new (string,string,string,Func<string>)[]
                    {
                        ("QualityCombo","Best",russian?"Лучшее":"Best",()=>subject.ReadOptions().Quality),
                        ("CodecCombo","Auto",russian?"Авто":"Auto",()=>subject.ReadOptions().Codec)
                    })
                    {
                        var option=(ComboBox)window.FindName(name)!;
                        // Read the selected value the user sees. A detached
                        // DataTemplate.LoadContent() has no host or binding/layout
                        // turn and cannot establish the displayed ComboBox result.
                        option.BringIntoView();option.ApplyTemplate();window.UpdateLayout();
                        var selection=option.Template.FindName("ContentSite",option) as ContentPresenter;
                        Require(selection is not null,"The actual ComboBox must expose its selected-value presenter: "+name);
                        string OptionDetails(string location,IEnumerable<string> captions)=>
                            $"{name}/{location}; UI={language}; selected={option.SelectedItem}; option={readId()}; captions=[{string.Join(" | ",captions)}]";
                        AwaitPresentation(()=>RenderedText(selection!).Contains(caption),()=>
                            "The actual selected option must translate its visible caption. "+OptionDetails("selected",RenderedText(selection!)));
                        Require(option.SelectedItem as string==id && selection!.Content as string==id && readId()==id,
                            "The actual selected caption must preserve its canonical item and checkpoint ID. "+OptionDetails("selected",RenderedText(selection!)));
                        try
                        {
                            option.IsDropDownOpen=true;window.UpdateLayout();
                            AwaitPresentation(()=>option.Template.FindName("PART_Popup",option) is Popup {IsOpen:true} &&
                                    option.ItemContainerGenerator.ContainerFromItem(id) is ComboBoxItem item && RenderedText(item).Contains(caption),()=>
                                "The actual open dropdown must translate the option caption. "+OptionDetails("dropdown",
                                    option.ItemContainerGenerator.ContainerFromItem(id) is ComboBoxItem item?RenderedText(item):[]));
                            Require(option.SelectedItem as string==id && readId()==id,
                                "Opening the localized dropdown must preserve the selected canonical identifier.");
                            Console.WriteLine("PASS: actual option presentation: "+OptionDetails("selected",RenderedText(selection!)));
                        }
                        finally{option.IsDropDownOpen=false;}
                    }
                    var rate=(ComboBox)window.FindName("RateLimitCombo")!;
                    Require(rate.Text==(russian?"Без ограничений":"Unlimited") && subject.ReadOptions().RateLimit=="Unlimited",
                        "The editable unlimited rate must persist its canonical identifier.");
                    rate.Text="5M";
                    Require(subject.ReadOptions().RateLimit=="5M","A manually typed rate must remain unchanged.");
                    subject.ApplyOptions(new DownloadOptions{RateLimit="Unlimited",Quality="720",Codec="Auto",Container="MKV"});
                    subject.SaveCheckpoint();
                    var saved=storage.Restore()!.Settings;
                    Require(saved.RateLimit=="Unlimited" && saved.Quality=="720" && saved.Codec=="Auto" && saved.Container=="MKV",
                        "Localized controls must save the same canonical options after restoring older identifiers.");
                    Require(((TextBlock)window.FindName("HeaderStatus")!).Text==(russian?"● Готово":"● Ready") &&
                            ((Button)window.FindName("PauseButton")!).Content.ToString()==(russian?"Пауза":"Pause"),
                        "Timer-driven main-window statuses must follow UI culture, independently of numeric culture.");
                    var tray=subject.Lifecycle.TrayIcon;
                    string[] trayActions=tray.ContextMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>()
                        .Select(item=>item.Text??"").ToArray();
                    Require(trayActions.SequenceEqual(russian?new[]{"Открыть","Открыть папку загрузок","Выход"}:
                            new[]{"Open","Open downloads","Exit"}),"The actual tray context menu must translate all actions.");
                    string trayProgress=DownloadFormat.Tray(12,34,5,1024,true,true);
                    Require(trayProgress.Length<=63 && trayProgress.Contains(russian?"Готово 12 Осталось 34 Ошибок 5":"OK 12 Left 34 Err 5",StringComparison.Ordinal) &&
                            trayProgress.EndsWith(russian?"Пауза":"Paused",StringComparison.Ordinal),
                        "Tray metrics must retain their meaning, order and Windows tooltip length limit.");
                    using(var listDialog=NativeWindow.CreateOpenListDialog())
                    {
                        Require(listDialog.Title==(russian?"Открыть список ссылок":"Open URL list") &&
                                listDialog.Filter.Contains(russian?"Все файлы|*.*":"All files|*.*",StringComparison.Ordinal) &&
                                listDialog.Filter.Contains("*.vdqueue;*.txt;*.url",StringComparison.Ordinal),
                            "The production list picker must translate its caption and filter descriptions without changing extensions.");
                    }
                    if(language=="ru-RU")
                    {
                        // Open the real WinForms picker through the WPF button.
                        // A native thread-window observer closes it without choosing a file.
                        uint thread=GetCurrentThreadId();bool observed=false;
                        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
                        timer.Tick+=(_,_)=>
                        {
                            if(DismissDialog(thread,"Открыть список ссылок",1)){observed=true;timer.Stop();}
                        };
                        using var watchdog=new CancellationTokenSource();
                        _=Task.Delay(TimeSpan.FromSeconds(10),watchdog.Token).ContinueWith(task=>
                        {if(!task.IsCanceled)DismissDialog(thread,"Открыть список ссылок",1);},TaskScheduler.Default);
                        timer.Start();
                        try {((Button)window.FindName("OpenListButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
                        finally {watchdog.Cancel();timer.Stop();}
                        Require(observed,"Clicking the actual Open list button must show the translated native file picker.");
                        using var duplicateInput=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","DuplicateDownloadDialog.xaml"));
                        using var duplicateReader=XmlReader.Create(duplicateInput);
                        var duplicate=(Window)XamlReader.Load(duplicateReader);
                        try
                        {
                            duplicate.Show();
                            Require(duplicate.Title=="Уже скачано" && ((Button)duplicate.FindName("YesAllButton")!).Content.ToString()=="Да для всех" &&
                                    ((Button)duplicate.FindName("CancelButton")!).Content.ToString()=="Отмена",
                                "The actual duplicate dialog and its batch actions must use Russian captions.");
                        }
                        finally {duplicate.Close();}
                    }
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

                    var row=new DownloadItem{Url=url,Status="Failed",State="Failed",RawError=raw,Speed="1.5MiB/s"};
                    Require(row.RawError==raw && row.Status=="Failed" && row.State=="Failed",
                        "Presentation must not translate raw diagnostics or machine-readable states.");
                    Require(row.ErrorMessage==failureSummary,
                        "Download rows and preview errors must use the same display-only explanation.");
                    string json=JsonSerializer.Serialize(row,AppPersistence.Json);
                    using(var document=JsonDocument.Parse(json))
                    {
                        Require(document.RootElement.GetProperty("RawError").GetString()==raw &&
                                !document.RootElement.TryGetProperty("ErrorMessage",out _) &&
                                !document.RootElement.TryGetProperty("StateLabel",out _) &&
                                !document.RootElement.TryGetProperty("StateToolTip",out _),
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
                                cell.ToolTip as string==row.StateLabel+Environment.NewLine+failureSummary,
                            "Both actual grids must render the localized state and error explanation.");
                        Require((cell!.Foreground as SolidColorBrush)?.Color==Color.FromRgb(0xFB,0x71,0x85),
                            "Failed-row colors must still follow the stable Status identifier.");
                    }
                    Require(RenderedText(download).Contains(russian?"1.5МиБ/с":"1.5MiB/s") && row.Speed=="1.5MiB/s",
                        "Actual transfer details must translate speed units without changing the stored/parser input.");
                    string[] headers=download.ContextMenu.Items.OfType<MenuItem>()
                        .Select(item=>item.Header?.ToString()??"").ToArray();
                    Require(headers.Contains(russian?"Показать ошибку":"Show error") &&
                            headers.Contains(russian?"Исходная диагностика":"Show raw error"),
                        "Download actions must distinguish the error explanation from original diagnostic details.");
                    Require(headers.Contains(russian?"Показать в папке":"Show in folder") &&
                            headers.Contains(russian?"Скачать повторно":"Retry URL"),
                        "The actual dynamic download menu must translate file and retry actions.");
                    Require(DependencyCenter.Inspect().Contains(russian?"не найден":"missing",StringComparison.Ordinal) ||
                            DependencyCenter.Inspect().Contains(russian?"установлен":"installed",StringComparison.Ordinal),
                        "Component inspection must translate its display statuses.");

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
            Console.WriteLine("PASS: actual Russian main-window controls, options, list picker, duplicate dialog, dynamic menus, tray and errors; checkpoint IDs and raw diagnostics stay consistent");
        }
        finally
        {
            CultureInfo.CurrentUICulture=originalUiCulture;
            CultureInfo.CurrentCulture=originalCulture;
        }
    }
}

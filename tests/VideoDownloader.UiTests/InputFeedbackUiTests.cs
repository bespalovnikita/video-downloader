using System.Globalization;
using System.Windows;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestInputFeedback(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Input-feedback WPF test failed: "+message);}
        static T Control<T>(Window owner,string name) where T:class=>(T)(owner.FindName(name)
            ??throw new InvalidOperationException("Missing actual input-feedback control: "+name));
        static void Click(Window owner,string name)=>Control<Button>(owner,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        string root=Path.Combine(testRoot,"input-feedback");Directory.CreateDirectory(root);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var oldCulture=CultureInfo.CurrentUICulture;
        string? oldClipboard=Clipboard.ContainsText()?Clipboard.GetText():null;
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        NativeWindow? subject=null;
        DownloadCoordinator? session=null;
        try
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
            subject=new NativeWindow(window,storage,"must-not-launch",
                automaticUpdates:false,automaticSubscriptionChecks:false);
            window.Show();window.UpdateLayout();
            var url=Control<TextBox>(window,"UrlInput");
            var feedback=Control<TextBlock>(window,"InputFeedbackText");
            var empty=Control<FrameworkElement>(window,"QueueEmptyState");
            var queue=Control<DataGrid>(window,"QueueGrid");
            Require(empty.Visibility==Visibility.Visible && subject.SessionState.Queue.Count==0,
                "An empty queue must show the invitation to paste a link");

            string invalid="  not-a-link   another-invalid-token  ";
            url.Text=invalid;Click(window,"AddUrlButton");
            Require(url.Text==invalid && subject.SessionState.Queue.Count==0 &&
                feedback.Visibility==Visibility.Visible && feedback.Text.Contains("https://",StringComparison.Ordinal),
                "Invalid manual input must be kept verbatim and explained inline without adding rows");

            string first="https://input-feedback.invalid/Video?token=%2f";
            url.Text=first;
            var source=PresentationSource.FromVisual(window)!;
            url.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
            Require(subject.SessionState.Queue.Count==1 && subject.SessionState.Queue[0].Url==first && url.Text.Length==0 &&
                empty.Visibility==Visibility.Collapsed && queue.SelectedItem is null,
                "Enter must consume an accepted exact URL, hide the invitation and keep manual ingress selection unchanged");
            var existing=subject.SessionState.Queue[0];
            url.Text=first;Click(window,"AddUrlButton");
            Require(subject.SessionState.Queue.Count==1 && ReferenceEquals(queue.SelectedItem,existing) &&
                url.Text.Length==0 && feedback.Text.Contains("уже в очереди",StringComparison.OrdinalIgnoreCase),
                "An exact duplicate must surface and select the existing visible row without adding or resetting it");

            string second="https://input-feedback.invalid/second";
            url.Text=first+Environment.NewLine+second+" "+second+" not-a-link";
            Click(window,"AddUrlButton");
            Require(subject.SessionState.Queue.Select(row=>row.Url).SequenceEqual(new[]{first,second}) &&
                url.Text=="not-a-link" && feedback.Text.Contains("Добавлено: 1",StringComparison.Ordinal) &&
                feedback.Text.Contains("Уже в очереди: 2",StringComparison.Ordinal) &&
                feedback.Text.Contains("Не распознано: 1",StringComparison.Ordinal),
                "A mixed batch must report accepted URLs, existing/repeated tokens and invalid tokens while retaining invalid input");

            string differentCase="https://input-feedback.invalid/video?token=%2F";
            url.Text=differentCase;Click(window,"AddUrlButton");
            Require(subject.SessionState.Queue.Count==3 && subject.SessionState.Queue.Last().Url==differentCase,
                "Feedback must not introduce URL normalization or merge differently cased exact identities");

            Clipboard.SetText(second+" clipboard-invalid");Click(window,"PasteClipboardButton");
            Require(subject.SessionState.Queue.Count==3 && url.Text=="clipboard-invalid" &&
                feedback.Text.Contains("Уже в очереди: 1",StringComparison.Ordinal) &&
                feedback.Text.Contains("Не распознано: 1",StringComparison.Ordinal),
                "Explicit Paste must use the same duplicate/invalid summary and preserve unrecognized text");
            string draft="  original typed draft  ";url.Text=draft;
            Clipboard.SetText(second+" second-clipboard-invalid");Click(window,"PasteClipboardButton");
            Require(subject.SessionState.Queue.Count==3 && url.Text==draft+Environment.NewLine+"second-clipboard-invalid",
                "Explicit Paste must preserve an existing typed draft verbatim and append its unrecognized remainder");
            string priorFeedback=feedback.Text,priorInput=url.Text;
            queue.SelectedItem=existing;
            Control<CheckBox>(window,"WatchClipboardCheck").IsChecked=true;
            string watched="https://input-feedback.invalid/watched";
            Clipboard.SetText(watched+" incidental-clipboard-text");subject.Input.PollClipboard();
            Require(subject.SessionState.Queue.Count==4 && subject.SessionState.Queue.Last().Url==watched &&
                ReferenceEquals(queue.SelectedItem,existing) && feedback.Text==priorFeedback && url.Text==priorInput,
                "Clipboard watching must retain its silent parsing, preserve input and avoid stealing the selection");
            Require(subject.SessionState.Coordinator is null && subject.SessionState.StartedAt is null,
                "Input validation and duplicate feedback must never start downloads");

            // The history confirmation pumps a real nested dispatcher loop.
            // A later explicit input must keep its feedback when the earlier
            // admission finishes; checking only Text equality is insufficient.
            string historical="https://input-feedback.invalid/history";
            storage.AppendSuccess(historical);
            session=new DownloadCoordinator("must-not-launch",storage,new DownloadOptions{Output=Path.Combine(root,"output")},_=>{},_=>{});
            subject.SessionState.AttachCoordinator(session);
            bool nested=false;string nestedFeedback="";Exception? nestedFailure=null;
            var observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            var watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
            Window? DuplicateDialog()=>Application.Current.Windows.Cast<Window>().LastOrDefault(candidate=>
                candidate.Owner==window && candidate.FindName("YesButton") is Button);
            observer.Tick+=(_,_)=>
            {
                var dialog=DuplicateDialog();if(dialog?.IsVisible!=true)return;
                observer.Stop();
                try
                {
                    url.Text="newer-invalid-input";Click(window,"AddUrlButton");
                    nestedFeedback=feedback.Text;nested=true;
                }
                catch(Exception error){nestedFailure=error;}
                finally{Control<Button>(dialog,"YesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
            };
            watchdog.Tick+=(_,_)=>
            {
                observer.Stop();watchdog.Stop();
                nestedFailure=new TimeoutException("The real nested input/history dialog did not finish");
                DuplicateDialog()?.Close();
            };
            url.Text=historical;observer.Start();watchdog.Start();
            try{Click(window,"AddUrlButton");}
            finally{observer.Stop();watchdog.Stop();}
            if(nestedFailure is not null)throw nestedFailure;
            Require(nested && subject.SessionState.Queue.Last().Url==historical && session.Outstanding==1 &&
                url.Text=="newer-invalid-input" && feedback.Text==nestedFeedback &&
                feedback.Text.Contains("Ссылка не распознана",StringComparison.Ordinal),
                "An older admission completing after a nested input must not overwrite its input or inline feedback");
            Console.WriteLine("PASS: manual Add/Enter/Paste preserve invalid input, report exact duplicates and batch counts, while clipboard watching stays silent");
        }
        finally
        {
            if(session is not null)
            {
                subject?.SessionState.AttachCoordinator(null);session.Stop();
                session.RunAsync().GetAwaiter().GetResult();session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            subject?.Dispose();window.Close();
            CultureInfo.CurrentUICulture=oldCulture;
            if(oldClipboard is null)Clipboard.Clear();else Clipboard.SetText(oldClipboard);
        }
    }
}

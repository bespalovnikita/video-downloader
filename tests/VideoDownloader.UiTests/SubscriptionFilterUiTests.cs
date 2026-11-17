using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestSubscriptionFilters(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Subscription-filter WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(8);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        string root=Path.Combine(testRoot,"subscription-filters-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store=new SubscriptionStore(root);
        const string upper="https://subscription-filter.invalid/Video?case=A";
        const string lower="https://subscription-filter.invalid/video?case=A";
        const string queued="https://subscription-filter.invalid/queued";
        const string processed="https://subscription-filter.invalid/processed";
        const string duplicate="https://subscription-filter.invalid/duplicate";
        const string manual="https://subscription-filter.invalid/manual";
        const string baseline="https://subscription-filter.invalid/baseline";
        var snapshot=new SubscriptionSnapshot
        {
            Subscriptions=[
                new(){Id="source-a",Url="https://subscription-filter.invalid/a",Title="Одинаковое имя",ShortName="Канал A",Initialized=true},
                new(){Id="source-b",Url="https://subscription-filter.invalid/b",Title="Одинаковое имя",ShortName="Канал B",Initialized=true}],
            Items=[
                new(){SubscriptionId="source-a",Url=upper,Title="Ёжик верх",State="New"},
                new(){SubscriptionId="source-a",Url=lower,Title="ёжик низ",State="New"},
                new(){SubscriptionId="source-a",Url=queued,Title="В очереди",State="Queued"},
                new(){SubscriptionId="source-a",Url=processed,Title="Успешный",State="Processed"},
                new(){SubscriptionId="source-a",Url=duplicate,Title="Подтверждённый повтор",State="AlreadyProcessed",DuplicateSubscriptionId="source-b",DuplicateNotified=true},
                new(){SubscriptionId="source-a",Url=manual,Title="Готово вручную",State="Skipped",ManuallyCompleted=true},
                new(){SubscriptionId="source-a",Url=baseline,Title="Исходный снимок",State="Baseline"},
                new(){SubscriptionId="source-b",Url=upper,Title="Другой ёжик",State="New"}]
        };
        AppPersistence.AtomicWrite(store.Path,JsonSerializer.Serialize(snapshot,AppPersistence.Json));
        byte[] original=File.ReadAllBytes(store.Path);
        var accepted=new List<(string Source,string Url)>();
        int scans=0,prefetches=0;
        using var reader=XmlReader.Create(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        var owner=(Window)XamlReader.Load(reader);
        owner.Show();
        using var subject=new SubscriptionController(owner,store,"not-started.exe",
            Path.Combine(AppContext.BaseDirectory,"ui","SubscriptionsWindow.xaml"),
            ()=>new Dictionary<string,DateTimeOffset?>(StringComparer.Ordinal),()=>{scans++;return "";},
            (urls,source,_,_)=>
            {
                var values=urls.ToArray();accepted.AddRange(values.Select(url=>(source,url)));return values;
            },_=>{},_=>{},_=>prefetches++);
        var driver=new SubscriptionScenarioDriver(subject);
        try
        {
            driver.Open();
            PumpUntil(()=>driver.TestWindow?.IsVisible==true && driver.TestInbox.Items.Count==7,
                "The real modeless window must load the selected source's ordered materials.");
            var window=driver.TestWindow!;
            T Control<T>(string name) where T:class=>driver.TestControl<T>(name);
            var search=Control<TextBox>("SubscriptionInboxSearchBox");
            var state=Control<ComboBox>("SubscriptionInboxStateFilter");
            var add=Control<Button>("QueueSubscriptionItemsButton");
            SubscriptionController.SubscriptionItemView[] Rows()=>driver.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().ToArray();
            void Click(string name)=>Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Source(string id)=>driver.TestSources.SelectedItem=driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single(row=>row.Id==id);
            void Reset()=>Click("ResetSubscriptionInboxFiltersButton");
            string Choice(string key)
            {
                state.SelectedValue=key;
                return state.SelectedItem?.GetType().GetProperty("Label")?.GetValue(state.SelectedItem)?.ToString()??"";
            }
            Require(Rows().Select(row=>row.Url).SequenceEqual(snapshot.Items.Take(7).Select(item=>item.Url)) &&
                Control<TextBlock>("SubscriptionInboxCountText").Text=="Показано 7 из 7",
                "All states must retain the exact saved source order.");
            Require(Choice("New")=="Новые · 2" && Rows().Select(row=>row.Url).SequenceEqual(new[]{upper,lower}),
                "New must show both case-distinct URLs, with a real count.");
            Require(Choice("Processed")=="Обработанные · 2" && Rows().Select(row=>row.Url).SequenceEqual(new[]{processed,duplicate}),
                "Confirmed successes from either source must be processed; manual Done must not be treated as a download.");
            Require(Choice("Queued")=="В очереди · 1" && Rows().Single().Url==queued,"Queued must have its independent count and state predicate.");
            Reset();
            var selected=Rows().Where(row=>row.Url==upper || row.Url==lower).ToArray();
            foreach(var row in selected)driver.TestInbox.SelectedItems.Add(row);
            search.Text="ЁЖИК ВЕРХ";
            PumpUntil(()=>Rows().Length==1,"Unicode case-insensitive title search must filter the real grid.");
            Require(Rows().Single().Url==upper && driver.TestInbox.SelectedItems.Count==1 &&
                ((SubscriptionController.SubscriptionItemView)driver.TestInbox.SelectedItem).Url==upper,
                "A hidden selected material must be deselected; a surviving selection must preserve its exact identity.");
            search.Text="SUBSCRIPTION-FILTER.INVALID/VIDEO?CASE=A";
            PumpUntil(()=>Rows().Length==2,"Case-insensitive URL display search must match both distinct identities.");
            Require(Rows().Select(row=>row.Url).SequenceEqual(new[]{upper,lower}),"Search must never normalize or merge exact URLs.");
            search.Text="нет такого материала";
            PumpUntil(()=>Rows().Length==0,"An unmatched query must hide every material.");
            Require(driver.TestInbox.SelectedItems.Count==0 && !add.IsEnabled &&
                Control<TextBlock>("SubscriptionInboxEmptyText").Visibility==Visibility.Visible,
                "An empty result must explain recovery and leave no hidden selection actionable.");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(accepted.Count==0,"A directly raised queue event must not admit a hidden selection.");
            Reset();
            Require(File.ReadAllBytes(store.Path).SequenceEqual(original) && scans==0 && prefetches==0,
                "Searching, state filters and resetting must not mutate subscriptions, scan, enqueue or fetch images.");

            foreach(var row in Rows().Where(row=>row.Url==upper || row.Url==lower))driver.TestInbox.SelectedItems.Add(row);
            search.Text="Ёжик верх"; // Click before the debounce: the action must flush the current draft.
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>!driver.TestIsMutating && accepted.Count==1,"The actual queue action must complete its durable acknowledgement.");
            Require(accepted.SequenceEqual(new[]{("source-a",upper)}) && store.Read().Items.Single(item=>item.SubscriptionId=="source-a" && item.Url==upper).State=="Queued" &&
                store.Read().Items.Single(item=>item.SubscriptionId=="source-b" && item.Url==upper).State=="New",
                "Only the still-visible captured SubscriptionId plus exact URL may be acknowledged; a hidden peer and another source must remain untouched.");
            Reset();Source("source-b");
            Require(Rows().Length==1 && Rows()[0].Item.SubscriptionId=="source-b" && Rows()[0].Url==upper,
                "Sources with equal names must stay distinct by ID.");
            driver.TestSources.SelectedItems.Clear();
            Require(Rows().Length==8,"Clearing source selection must show the full ordered cross-source projection.");
            Source("source-a");state.SelectedValue="New";
            store.MarkQueued("source-a",[lower]);driver.Refresh();
            PumpUntil(()=>Rows().Length==0,"A store refresh must update active status filters and their counts.");
            Require(state.SelectedValue as string=="New" && state.Text=="Новые · 0" && scans==0 && prefetches==0,
                "Reload must retain the selected filter while observing changed states without starting background work.");
            Reset();

            window.Width=860;window.Height=600;window.UpdateLayout();
            Require(Control<FrameworkElement>("SubscriptionDetailsPanel").Visibility==Visibility.Collapsed &&
                Control<ColumnDefinition>("SubscriptionDetailsColumn").Width.Value==0 &&
                Control<CheckBox>("SubscriptionPreviewToggle").Visibility==Visibility.Collapsed &&
                Control<Button>("OpenSubscriptionDetailsButton").IsVisible && search.ActualWidth>100,
                "A narrow window must reserve list width and expose explicit navigation to the selected video.");
            var selectedVideo=Rows().Single(row=>row.Url==upper);
            driver.TestInbox.SelectedItem=selectedVideo;
            Click("OpenSubscriptionDetailsButton");window.UpdateLayout();
            var details=Control<FrameworkElement>("SubscriptionDetailsPanel");
            Require(details.IsVisible && Grid.GetColumn(details)==0 && Grid.GetColumnSpan(details)==3 &&
                !Control<FrameworkElement>("SubscriptionMainScroll").IsVisible &&
                Control<Button>("BackToSubscriptionsListButton").IsVisible &&
                Control<TextBox>("SubscriptionPreviewUrlBox").Text==upper &&
                Control<TextBlock>("SubscriptionPreviewTitle").Text==selectedVideo.Title,
                "Compact information must display the same selected exact URL and title at full width without requiring a larger monitor.");
            Click("BackToSubscriptionsListButton");window.UpdateLayout();
            Require(!details.IsVisible && Control<FrameworkElement>("SubscriptionMainScroll").IsVisible &&
                driver.TestInbox.SelectedItems.Count==1 &&
                ((SubscriptionController.SubscriptionItemView)driver.TestInbox.SelectedItem).Url==upper &&
                search.Text.Length==0 && state.SelectedValue as string=="All",
                "Returning from information must preserve the source, filter criteria and exact selected material.");
            Click("OpenSubscriptionDetailsButton");window.UpdateLayout();
            RaiseCompactSearchShortcut(window);
            Require(Control<FrameworkElement>("SubscriptionMainScroll").IsVisible && !details.IsVisible && search.IsKeyboardFocusWithin,
                "Ctrl+F from compact subscription information must return to the visible list and focus search.");
            var addForm=Control<Expander>("AddSubscriptionExpander");
            var editForm=Control<Expander>("SubscriptionEditorExpander");
            Require(!addForm.IsExpanded && !editForm.IsExpanded,"Forms must start collapsed to reserve space for materials.");
            addForm.IsExpanded=true;editForm.IsExpanded=true;
            Require(editForm.IsExpanded && !addForm.IsExpanded,"Opening settings must release the add form's vertical space.");
            editForm.IsExpanded=false;
            var scroll=Control<ScrollViewer>("SubscriptionMainScroll");
            window.UpdateLayout();scroll.ScrollToEnd();window.UpdateLayout();
            var bounds=add.TransformToAncestor(window).TransformBounds(new Rect(add.RenderSize));
            Require(bounds.Top>=0 && bounds.Bottom<=window.ActualHeight && double.IsFinite(driver.TestInbox.ActualHeight) &&
                driver.TestInbox.ActualHeight>=80 && scroll.ScrollableHeight>0,
                "A short work area must scroll finite table viewports and reveal the actual queue action rather than clipping it.");
            double wide=Math.Min(1200,SystemParameters.WorkArea.Width-24);
            if(wide>=1080)
            {
                window.Width=wide;window.UpdateLayout();
                var toggle=Control<CheckBox>("SubscriptionPreviewToggle");
                Require(toggle.IsEnabled && Control<FrameworkElement>("SubscriptionDetailsPanel").Visibility==Visibility.Visible,
                    "Widening must restore the requested preview panel.");
                toggle.IsChecked=false;window.UpdateLayout();
                Require(Control<FrameworkElement>("SubscriptionDetailsPanel").Visibility==Visibility.Collapsed,
                    "The user must be able to hide the panel explicitly even in a wide window.");
            }
            Console.WriteLine("PASS: subscription source/title/URL/state filtering, exact identity and source order, no hidden admission, durable acknowledgement and scrollable compact layout");
        }
        finally {driver.TestWindow?.Close();owner.Close();}
    }
}

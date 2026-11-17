using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Point=System.Windows.Point;

namespace VideoDownloader.Native;

internal sealed partial class SubscriptionController
{
    private sealed record InboxStateChoice(string Key,string Label);
    private DispatcherTimer? _inboxSearchTimer;
    private bool _bindingInboxFilters;
    private bool _subscriptionCompactDetails;
    private double _subscriptionPreviewWidth=320;

    private void BindInboxFilters(Window window)
    {
        var search=Control<TextBox>("SubscriptionInboxSearchBox");
        var state=Control<ComboBox>("SubscriptionInboxStateFilter");
        _inboxSearchTimer=new DispatcherTimer(DispatcherPriority.Background,window.Dispatcher)
            {Interval=TimeSpan.FromMilliseconds(180)};
        _inboxSearchTimer.Tick+=(_,_)=>RefreshInboxFilters();
        search.TextChanged+=(_,_)=>
        {
            if(_bindingInboxFilters)return;
            _inboxSearchTimer?.Stop();_inboxSearchTimer?.Start();
        };
        state.SelectionChanged+=(_,_)=>{if(!_bindingInboxFilters)RefreshInboxFilters();};
        Control<Button>("ResetSubscriptionInboxFiltersButton").Click+=(_,_)=>ResetInboxFilters();
        window.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.F && Keyboard.Modifiers==ModifierKeys.Control)
            {
                e.Handled=true;_subscriptionCompactDetails=false;RefreshSubscriptionsLayout();
                search.BringIntoView();search.Focus();search.SelectAll();
            }
            else if(e.Key==Key.Escape && (search.IsKeyboardFocusWithin || state.IsKeyboardFocusWithin))
            {e.Handled=true;ResetInboxFilters();}
        };
    }

    private void StopInboxFilterTimer()
    {_inboxSearchTimer?.Stop();_inboxSearchTimer=null;}

    private void RefreshInboxFilters()
    {
        _inboxSearchTimer?.Stop();
        if(_window is null || _disposed)return;
        ShowInbox();UpdateButtons();
    }

    private void ResetInboxFilters()
    {
        if(_window is null)return;
        _bindingInboxFilters=true;
        try
        {
            Control<TextBox>("SubscriptionInboxSearchBox").Clear();
            Control<ComboBox>("SubscriptionInboxStateFilter").SelectedValue="All";
        }
        finally{_bindingInboxFilters=false;}
        RefreshInboxFilters();
    }

    private SubscriptionItemView[] FilterInboxItems(SubscriptionItem[] sourceItems)
    {
        string draft=Control<TextBox>("SubscriptionInboxSearchBox").Text;
        string query=draft.Trim();
        var searched=sourceItems.Where(item=>query.Length==0 ||
            item.Title.Contains(query,StringComparison.OrdinalIgnoreCase) ||
            item.Url.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        var state=Control<ComboBox>("SubscriptionInboxStateFilter");
        string selected=state.SelectedValue as string??"All";
        bool Matches(SubscriptionItem item,string key)=>key switch
        {
            "New"=>item.State=="New",
            "Queued"=>item.State=="Queued",
            "Processed"=>item.State is "Processed" or "AlreadyProcessed",
            _=>true
        };
        _bindingInboxFilters=true;
        try
        {
            state.ItemsSource=new[]{new InboxStateChoice("All",$"Все · {searched.Length}"),
                new InboxStateChoice("New",$"Новые · {searched.Count(item=>Matches(item,"New"))}"),
                new InboxStateChoice("Queued",$"В очереди · {searched.Count(item=>Matches(item,"Queued"))}"),
                new InboxStateChoice("Processed",$"Обработанные · {searched.Count(item=>Matches(item,"Processed"))}")};
            state.SelectedValue=selected;
        }
        finally{_bindingInboxFilters=false;}
        // Filtering is a projection of the persisted source order. Display
        // matching never normalizes URL identity or changes subscription state.
        var visible=searched.Where(item=>Matches(item,selected)).Select(item=>new SubscriptionItemView(item)).ToArray();
        Control<TextBlock>("SubscriptionInboxCountText").Text=$"Показано {visible.Length} из {sourceItems.Length}";
        var empty=Control<TextBlock>("SubscriptionInboxEmptyText");
        empty.Text=sourceItems.Length==0?"Материалов пока нет. Выполните проверку подписки.":
            "Ничего не найдено. Измените поиск или сбросьте фильтры.";
        empty.Visibility=visible.Length==0?Visibility.Visible:Visibility.Collapsed;
        Control<Button>("ResetSubscriptionInboxFiltersButton").IsEnabled=draft.Length>0 || selected!="All";
        return visible;
    }

    private SubscriptionItemView[] VisibleInboxSelection()=>Inbox.SelectedItems.Cast<SubscriptionItemView>()
        .Where(row=>Inbox.Items.Contains(row)).ToArray();

    private void ConfigureSubscriptionsLayout(Window window)
    {
        _subscriptionCompactDetails=false;
        var work=SystemParameters.WorkArea;
        var ownerSource=PresentationSource.FromVisual(_owner);
        if(ownerSource?.CompositionTarget is {} target)
        {
            var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(_owner).Handle).WorkingArea;
            work=new Rect(target.TransformFromDevice.Transform(new Point(area.Left,area.Top)),
                target.TransformFromDevice.Transform(new Point(area.Right,area.Bottom)));
        }
        window.MinWidth=Math.Min(window.MinWidth,Math.Max(320,work.Width-24));
        window.MinHeight=Math.Min(window.MinHeight,Math.Max(320,work.Height-24));
        window.Width=Math.Min(window.Width,Math.Max(window.MinWidth,work.Width-24));
        window.Height=Math.Min(window.Height,Math.Max(window.MinHeight,work.Height-24));
        window.WindowStartupLocation=WindowStartupLocation.Manual;
        window.Left=Math.Clamp(_owner.Left+(_owner.ActualWidth-window.Width)/2,work.Left,Math.Max(work.Left,work.Right-window.Width));
        window.Top=Math.Clamp(_owner.Top+(_owner.ActualHeight-window.Height)/2,work.Top,Math.Max(work.Top,work.Bottom-window.Height));
        var toggle=Control<CheckBox>("SubscriptionPreviewToggle");
        toggle.Checked+=(_,_)=>RefreshSubscriptionsLayout();
        toggle.Unchecked+=(_,_)=>RefreshSubscriptionsLayout();
        var openDetails=Control<Button>("OpenSubscriptionDetailsButton");
        openDetails.IsEnabled=VisibleInboxSelection().Length==1;
        Inbox.SelectionChanged+=(_,_)=>
        {
            if(CurrentWindow(window))openDetails.IsEnabled=VisibleInboxSelection().Length==1;
        };
        openDetails.Click+=(_,_)=>
        {
            RefreshInboxFilters();
            if(VisibleInboxSelection().Length!=1)return;
            _subscriptionCompactDetails=true;RefreshSubscriptionsLayout();
            window.UpdateLayout();
            var back=Control<Button>("BackToSubscriptionsListButton");back.BringIntoView();back.Focus();
        };
        Control<Button>("BackToSubscriptionsListButton").Click+=(_,_)=>
        {
            _subscriptionCompactDetails=false;RefreshSubscriptionsLayout();
            Inbox.BringIntoView();Inbox.Focus();
        };
        window.SizeChanged+=(_,_)=>RefreshSubscriptionsLayout();
        var add=Control<Expander>("AddSubscriptionExpander");
        var edit=Control<Expander>("SubscriptionEditorExpander");
        add.Expanded+=(_,_)=>edit.IsExpanded=false;
        edit.Expanded+=(_,_)=>add.IsExpanded=false;
        RefreshSubscriptionsLayout();
    }

    private void RefreshSubscriptionsLayout()
    {
        if(_window is not {} window)return;
        var toggle=Control<CheckBox>("SubscriptionPreviewToggle");
        double width=window.ActualWidth>0?window.ActualWidth:window.Width;
        double height=window.ActualHeight>0?window.ActualHeight:window.Height;
        bool wide=width>=1080;
        if(wide)_subscriptionCompactDetails=false;
        bool show=wide?toggle.IsChecked==true:_subscriptionCompactDetails;
        toggle.Visibility=wide?Visibility.Visible:Visibility.Collapsed;
        toggle.IsEnabled=true;
        Control<Button>("OpenSubscriptionDetailsButton").Visibility=wide?Visibility.Collapsed:Visibility.Visible;
        Control<Button>("BackToSubscriptionsListButton").Visibility=wide?Visibility.Collapsed:Visibility.Visible;
        Control<FrameworkElement>("SubscriptionMainScroll").Visibility=!wide && show?Visibility.Collapsed:Visibility.Visible;
        // Give virtualized grids a finite viewport. Short work areas scroll
        // this pane rather than cutting off forms or the bottom actions.
        Control<FrameworkElement>("SubscriptionMainPanel").Height=Math.Max(720,height-76);
        var column=Control<ColumnDefinition>("SubscriptionDetailsColumn");
        if(!show && column.Width.Value>0)_subscriptionPreviewWidth=column.Width.Value;
        bool sidebar=wide && show;
        column.MinWidth=sidebar?260:0;
        if(!sidebar)column.Width=new GridLength(0);
        else if(column.Width.Value==0)column.Width=new GridLength(_subscriptionPreviewWidth);
        Control<ColumnDefinition>("SubscriptionDetailsGapColumn").Width=new GridLength(sidebar?12:0);
        var details=Control<FrameworkElement>("SubscriptionDetailsPanel");
        Grid.SetColumn(details,wide?2:0);Grid.SetColumnSpan(details,wide?1:3);
        details.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        Control<FrameworkElement>("SubscriptionDetailsSplitter").Visibility=sidebar?Visibility.Visible:Visibility.Collapsed;
        foreach(string name in new[]{"AddSubscriptionExpander","SubscriptionEditorExpander"})
            if(Control<Expander>(name).Content is ScrollViewer scroll)
                scroll.MaxHeight=Math.Min(name=="AddSubscriptionExpander"?260:210,Math.Max(100,height*.28));
    }
}

using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

internal sealed partial class SubscriptionController
{
    // Queue admission and subscription persistence are separate operations.
    // Preserve the known admission count when a later source/write fails.
    private sealed class QueueAcceptanceException(int acceptedCount,Exception inner)
        : IOException(inner.Message,inner)
    {
        internal int AcceptedCount { get; }=acceptedCount;
    }

    private async Task<int> OfferNewItemsAsync(Window window,IReadOnlySet<string> successfulIds)
    {
        if (!CurrentWindow(window) || !window.IsVisible || _mutation) return 0;
        _mutation=true;
        UpdateButtons();
        try
        {
            var snapshot=await Task.Run(()=>_store.ReconcileSuccess(_readHistory().Keys));
            if (!CurrentWindow(window) || !window.IsVisible) return 0;
            var sources=snapshot.Subscriptions.Where(source=>successfulIds.Contains(source.Id) &&
                !source.Paused && source.LastError.Length==0).ToDictionary(source=>source.Id,StringComparer.Ordinal);
            var offered=snapshot.Items.Where(item=>sources.ContainsKey(item.SubscriptionId) &&
                item.State=="New" && !item.InitialQueuePending).ToArray();
            if (offered.Length==0) return 0;
            var offeredKeys=offered.Select(item=>(item.SubscriptionId,item.Url)).ToHashSet();
            if (!ConfirmNewItems(window,offered,sources) || !CurrentWindow(window) || !window.IsVisible) return 0;

            // A modal window pumps the dispatcher. Read again: another success,
            // skip, deletion or pause may have changed the offered decisions.
            // Newly discovered rows not shown by this offer are not admitted.
            var current=await Task.Run(()=>_store.ReconcileSuccess(_readHistory().Keys));
            if (!CurrentWindow(window) || !window.IsVisible) return 0;
            var allowedSources=current.Subscriptions.Where(source=>successfulIds.Contains(source.Id) &&
                !source.Paused && source.LastError.Length==0).Select(source=>source.Id).ToHashSet(StringComparer.Ordinal);
            var candidates=current.Items.Where(item=>offeredKeys.Contains((item.SubscriptionId,item.Url)) &&
                allowedSources.Contains(item.SubscriptionId) && item.State=="New" && !item.InitialQueuePending).ToArray();
            return await QueueCandidatesAsync(candidates,()=>CurrentWindow(window) && window.IsVisible,newOnly:true);
        }
        finally { _mutation=false;UpdateButtons(); }
    }

    private static bool ConfirmNewItems(Window owner,IReadOnlyList<SubscriptionItem> items,
        IReadOnlyDictionary<string,SubscriptionDefinition> sources)
    {
        int unique=items.Select(item=>item.Url).Distinct(StringComparer.Ordinal).Count();
        var groups=items.GroupBy(item=>item.SubscriptionId,StringComparer.Ordinal).ToArray();
        var dialog=new Window
        {
            Title="Новые материалы подписок",Owner=owner,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Width=620,MinWidth=480,SizeToContent=SizeToContent.Height,MaxHeight=600,
            Background=owner.Background,Foreground=owner.Foreground,FontFamily=owner.FontFamily,
            ShowInTaskbar=false
        };
        dialog.Resources.MergedDictionaries.Add(owner.Resources);
        NameScope.SetNameScope(dialog,new NameScope());
        var panel=new StackPanel{Margin=new Thickness(20)};
        var summary=new TextBlock
        {
            Name="SubscriptionQueueOfferSummary",TextWrapping=TextWrapping.Wrap,FontSize=16,
            Text=$"В проверенных подписках — {items.Count} материалов со статусом «Новое»."+Environment.NewLine+
                $"Уникальных ссылок: {unique}. Подписок: {groups.Length}."+Environment.NewLine+Environment.NewLine+
                "Добавить все в обычную очередь? Скачивание запускается только кнопкой Start."
        };
        panel.Children.Add(summary);dialog.RegisterName(summary.Name,summary);
        string listing=string.Join(Environment.NewLine,groups.Take(20).Select(group=>
        {
            var source=sources[group.Key];
            string title=source.Title.Length>0?source.Title:source.Url;
            return $"{title} — {group.Count()}";
        }));
        if(groups.Length>20)listing+=Environment.NewLine+$"И ещё подписок: {groups.Length-20}.";
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,14,0,16),
            Content=new TextBlock{Text=listing,TextWrapping=TextWrapping.Wrap}
        });
        var buttons=new WrapPanel{HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
        var yes=new Button{Name="SubscriptionQueueOfferYesButton",Content="Добавить в очередь"};
        var no=new Button{Name="SubscriptionQueueOfferNoButton",Content="Не сейчас",IsDefault=true,IsCancel=true};
        bool approved=false;
        yes.Click+=(_,_)=>{approved=true;dialog.Close();};
        no.Click+=(_,_)=>dialog.Close();
        buttons.Children.Add(yes);buttons.Children.Add(no);
        dialog.RegisterName(yes.Name,yes);dialog.RegisterName(no.Name,no);
        panel.Children.Add(buttons);dialog.Content=panel;
        dialog.ShowDialog();
        return approved;
    }
}

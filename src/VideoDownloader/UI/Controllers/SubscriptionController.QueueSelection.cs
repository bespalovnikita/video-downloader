using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

internal sealed partial class SubscriptionController
{
    private enum QueueSelectionChoice { NewOnly,AllSelected }

    private static bool CanSelectForQueue(SubscriptionItem item)=>
        item.State is "Baseline" or "New" or "Skipped" or "Queued" or "Processed" or "AlreadyProcessed";

    private async Task QueueSelectedAsync()
    {
        if(Busy || _window is not {} window)return;
        // Capture identities before an await or modal loop can change the
        // current view. A later selection never enlarges this explicit action.
        var keys=Inbox.SelectedItems.Cast<SubscriptionItemView>().Select(row=>row.Item)
            .Where(CanSelectForQueue).Select(item=>(item.SubscriptionId,item.Url)).ToHashSet();
        if(keys.Count==0)return;
        _mutation=true;UpdateButtons();
        bool CanQueue()=>CurrentWindow(window) && window.IsVisible && !IsSchedulingSuspended;
        try
        {
            var snapshot=await Task.Run(()=>_store.ReconcileSuccess(_readHistory().Keys));
            if(!CanQueue())return;
            var selected=snapshot.Items.Where(item=>keys.Contains((item.SubscriptionId,item.Url)) && CanSelectForQueue(item)).ToArray();
            if(selected.Length==0)return;
            var offeredNew=selected.Where(item=>item.State=="New")
                .Select(item=>(item.SubscriptionId,item.Url)).ToHashSet();
            QueueSelectionChoice? choice=selected.Select(item=>item.State).Distinct(StringComparer.Ordinal).Skip(1).Any()
                ?ChooseQueueSelection(window,selected):QueueSelectionChoice.AllSelected;
            if(choice is null || !CanQueue())return;
            // NewOnly applies only to New rows shown by this choice. The queue
            // callback re-reads history and requires them to still be New.
            var candidates=choice==QueueSelectionChoice.NewOnly
                ?selected.Where(item=>offeredNew.Contains((item.SubscriptionId,item.Url))).ToArray():selected;
            int count=await QueueCandidatesAsync(candidates,CanQueue,selection:choice);
            if(!CanQueue())return;
            await ReloadAsync();
            if(!CanQueue())return;
            Status(count>0?$"В обычной очереди: {count}. Запуск только кнопкой «Скачать всю очередь».":
                "В очередь ничего не добавлено. Состояния материалов сохранены.");
        }
        catch(Exception e)
        {
            if(!CanQueue())return;
            string partial=e is QueueAcceptanceException accepted?$"В обычной очереди: {accepted.AcceptedCount}. ":"";
            Status(partial+"Не удалось подтвердить добавление в очередь: "+DisplayError(e)+
                " Уже принятые ссылки могут быть в обычной очереди; повторите действие после исправления.",true);
        }
        finally { _mutation=false;UpdateButtons(); }
    }

    private static QueueSelectionChoice? ChooseQueueSelection(Window owner,IReadOnlyList<SubscriptionItem> items)
    {
        int newCount=items.Count(item=>item.State=="New");
        var dialog=new Window
        {
            Name="SubscriptionQueueSelectionDialog",Title="Добавить материалы в очередь",Owner=owner,
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
            Name="SubscriptionQueueSelectionSummary",TextWrapping=TextWrapping.Wrap,FontSize=16,
            Text=$"Выбрано материалов: {items.Count}. Новых: {newCount}."+Environment.NewLine+Environment.NewLine+
                "Добавить только материалы со статусом «Новое» или всё выделенное независимо от состояния?"+
                Environment.NewLine+"Скачивание начнётся только по кнопке «Скачать всю очередь»."
        };
        panel.Children.Add(summary);dialog.RegisterName(summary.Name,summary);
        panel.Children.Add(new TextBlock
        {
            Margin=new Thickness(0,14,0,16),TextWrapping=TextWrapping.Wrap,
            Text=string.Join(Environment.NewLine,items.GroupBy(item=>item.State,StringComparer.Ordinal)
                .Select(group=>$"{group.First().StateLabel}: {group.Count()}"))
        });
        var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};
        var onlyNew=new Button
        {
            Name="SubscriptionQueueSelectionNewOnlyButton",Content="Только новые",IsEnabled=newCount>0,IsDefault=newCount>0
        };
        var all=new Button{Name="SubscriptionQueueSelectionAllSelectedButton",Content="Всё выделенное"};
        var cancel=new Button
        {
            Name="SubscriptionQueueSelectionCancelButton",Content="Отмена",IsCancel=true,IsDefault=newCount==0
        };
        QueueSelectionChoice? result=null;
        onlyNew.Click+=(_,_)=>{if(newCount>0){result=QueueSelectionChoice.NewOnly;dialog.Close();}};
        all.Click+=(_,_)=>{result=QueueSelectionChoice.AllSelected;dialog.Close();};
        cancel.Click+=(_,_)=>dialog.Close();
        foreach(var button in new[]{onlyNew,all,cancel})
        {
            buttons.Children.Add(button);dialog.RegisterName(button.Name,button);
        }
        panel.Children.Add(buttons);dialog.Content=panel;
        dialog.ShowDialog();
        return result;
    }
}

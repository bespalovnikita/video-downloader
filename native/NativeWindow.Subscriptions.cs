using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private Window? _subscriptionsWindow;
    private SubscriptionStore? _subscriptionStore;
    private SubscriptionSnapshot? _subscriptionSnapshot;
    private CancellationTokenSource? _subscriptionCancellation;
    private bool _subscriptionMutation;
    private bool _subscriptionNoticeShowing;
    private bool _subscriptionsDisposed;
    private int _subscriptionRefreshVersion;
    private bool _subscriptionRefreshQueued;
    private bool _subscriptionBinding;
    private string _subscriptionNameSelectionId="";
    private string _subscriptionNameSavedValue="";

    private SubscriptionStore SubscriptionStorage => _subscriptionStore ??=
        new SubscriptionStore(Path.GetDirectoryName(_storage.SessionPath)!);

    private T SubscriptionControl<T>(string name) where T:class =>
        (T)(_subscriptionsWindow?.FindName(name) ?? throw new InvalidOperationException("Missing subscription control: "+name));
    private DataGrid SubscriptionSources => SubscriptionControl<DataGrid>("SubscriptionGrid");
    private DataGrid SubscriptionInbox => SubscriptionControl<DataGrid>("SubscriptionInboxGrid");
    private bool SubscriptionBusy => _subscriptionMutation || _subscriptionCancellation!=null;

    private void OpenSubscriptions()
    {
        if (_disposed || _subscriptionsDisposed) return;
        if (_subscriptionsWindow!=null)
        {
            _subscriptionsWindow.Show();
            _subscriptionsWindow.WindowState=WindowState.Normal;
            _subscriptionsWindow.Activate();
            RefreshSubscriptions();
            return;
        }
        try
        {
            string path=Path.Combine(AppContext.BaseDirectory,"ui","SubscriptionsWindow.xaml");
            using var reader=XmlReader.Create(path);
            var window=(Window)XamlReader.Load(reader);
            window.Owner=_window;
            // The XAML has standalone dark styles for both runtimes. Also make
            // the main window's resource catalog available to this owned window.
            window.Resources.MergedDictionaries.Add(_window.Resources);
            _subscriptionsWindow=window;
            SubscriptionControl<Button>("AddSubscriptionButton").Click+=async(_,_)=>await AddSubscriptionAsync();
            SubscriptionControl<Button>("SaveSubscriptionNameButton").Click+=async(_,_)=>await SaveSubscriptionNameAsync();
            SubscriptionControl<Button>("CheckSubscriptionButton").Click+=async(_,_)=>
            {
                if (SubscriptionSources.SelectedItem is SubscriptionSourceView source)
                    await CheckSubscriptionsAsync([source.Id]);
            };
            SubscriptionControl<Button>("CheckAllSubscriptionsButton").Click+=async(_,_)=>
            {
                if (SubscriptionBusy) return;
                var snapshot=await ReloadSubscriptionsAsync();
                if (snapshot!=null)
                    await CheckSubscriptionsAsync(snapshot.Subscriptions.Where(x=>!x.Paused).Select(x=>x.Id).ToArray());
            };
            SubscriptionControl<Button>("CancelSubscriptionCheckButton").Click+=(_,_)=>
            {
                _subscriptionCancellation?.Cancel();
                SubscriptionStatus("Проверка отменяется. Уже сохранённые результаты и обычная очередь останутся на месте.");
            };
            SubscriptionControl<Button>("PauseSubscriptionButton").Click+=async(_,_)=>await ToggleSubscriptionPauseAsync();
            SubscriptionControl<Button>("RemoveSubscriptionButton").Click+=async(_,_)=>await RemoveSubscriptionAsync();
            SubscriptionControl<Button>("QueueSubscriptionItemsButton").Click+=async(_,_)=>await QueueSelectedSubscriptionItemsAsync();
            SubscriptionControl<Button>("SkipSubscriptionItemsButton").Click+=async(_,_)=>await SkipSelectedSubscriptionItemsAsync();
            SubscriptionControl<Button>("OpenSubscriptionItemButton").Click+=(_,_)=>OpenSubscriptionItem();
            SubscriptionControl<TextBox>("SubscriptionUrlBox").KeyDown+=async(_,e)=>
            {
                if (e.Key==Key.Enter && !SubscriptionBusy) { e.Handled=true;await AddSubscriptionAsync(); }
            };
            SubscriptionControl<TextBox>("SubscriptionNameBox").TextChanged+=(_,_)=>UpdateSubscriptionButtons();
            SubscriptionControl<TextBox>("SubscriptionNameBox").KeyDown+=async(_,e)=>
            {
                if (e.Key==Key.Enter && !SubscriptionBusy) { e.Handled=true;await SaveSubscriptionNameAsync(); }
            };
            SubscriptionSources.SelectionChanged+=(_,_)=>
            {
                if (_subscriptionBinding) return;
                ShowSubscriptionInbox();UpdateSubscriptionNameEditor();UpdateSubscriptionButtons();
            };
            SubscriptionInbox.SelectionChanged+=(_,_)=>UpdateSubscriptionButtons();
            SubscriptionInbox.MouseDoubleClick+=(_,_)=>OpenSubscriptionItem();
            window.Closed+=(_,_)=>
            {
                if (!ReferenceEquals(_subscriptionsWindow,window)) return;
                _subscriptionCancellation?.Cancel();
                _subscriptionsWindow=null;
                _subscriptionNameSelectionId="";
                _subscriptionNameSavedValue="";
                _subscriptionRefreshVersion++;
            };
            window.Show();
            UpdateSubscriptionButtons();
            RefreshSubscriptions();
        }
        catch(Exception e)
        {
            _subscriptionsWindow=null;
            MessageBox.Show(_window,"Не удалось открыть подписки:\n"+e.Message,"Подписки",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }

    private void DisposeSubscriptions()
    {
        _subscriptionsDisposed=true;
        _subscriptionCancellation?.Cancel();
        _subscriptionsWindow?.Close();
        _subscriptionsWindow=null;
        _subscriptionRefreshVersion++;
    }

    private void RefreshSubscriptions()
    {
        if (_disposed || _subscriptionsDisposed || _subscriptionsWindow==null) return;
        if (!Dispatcher.CheckAccess()) { _=Dispatcher.BeginInvoke(RefreshSubscriptions);return; }
        if (_subscriptionRefreshQueued) return;
        _subscriptionRefreshQueued=true;
        _=Dispatcher.BeginInvoke(async () =>
        {
            _subscriptionRefreshQueued=false;
            await ReloadSubscriptionsAsync();
        });
    }

    private bool CurrentSubscriptionsWindow(Window? window) =>
        window!=null && !_disposed && !_subscriptionsDisposed && ReferenceEquals(window,_subscriptionsWindow);

    private async Task<SubscriptionSnapshot?> ReloadSubscriptionsAsync(bool notify=true)
    {
        var window=_subscriptionsWindow;
        if (!CurrentSubscriptionsWindow(window)) return null;
        int version=++_subscriptionRefreshVersion;
        try
        {
            var snapshot=await Task.Run(() =>
            {
                // Recover a success that reached the global journal before a
                // previous UI instance could update its subscription item.
                var history=_storage.ReadHistory();
                return SubscriptionStorage.ReconcileSuccess(history.Keys);
            });
            if (!CurrentSubscriptionsWindow(window)) return null;
            if (version!=_subscriptionRefreshVersion) return snapshot;
            string? selected=(SubscriptionSources.SelectedItem as SubscriptionSourceView)?.Id;
            _subscriptionSnapshot=snapshot;
            var rows=snapshot.Subscriptions.Select(x=>new SubscriptionSourceView(x)).ToArray();
            _subscriptionBinding=true;
            try
            {
                SubscriptionSources.ItemsSource=rows;
                SubscriptionSources.SelectedItem=rows.FirstOrDefault(x=>x.Id==selected) ?? rows.FirstOrDefault();
            }
            finally { _subscriptionBinding=false; }
            ShowSubscriptionInbox();
            UpdateSubscriptionNameEditor();
            UpdateSubscriptionButtons();
            if (notify) await NotifySubscriptionDuplicatesAsync(snapshot,window!);
            return snapshot;
        }
        catch(Exception e)
        {
            if (CurrentSubscriptionsWindow(window))
                SubscriptionStatus("Не удалось прочитать подписки или историю успехов: "+e.Message+
                    " Сохранённый файл не заменён пустым списком. После исправления повторите «Проверить все».",true);
            return null;
        }
    }

    private void ShowSubscriptionInbox()
    {
        if (_subscriptionsWindow==null) return;
        var selected=SubscriptionSources.SelectedItem as SubscriptionSourceView;
        var previouslySelected=SubscriptionInbox.SelectedItems.Cast<SubscriptionItemView>()
            .Select(x=>(x.Item.SubscriptionId,x.Url)).ToHashSet();
        var items=(_subscriptionSnapshot?.Items ?? [])
            .Where(x=>selected==null || x.SubscriptionId==selected.Id)
            .Where(x=>x.State!="Baseline")
            .OrderBy(x=>x.State=="New"?0:x.State=="Queued"?1:2)
            .ThenByDescending(x=>x.DiscoveredAtUtc).ThenBy(x=>x.Url,StringComparer.Ordinal)
            .Select(x=>new SubscriptionItemView(x)).ToArray();
        SubscriptionInbox.ItemsSource=items;
        foreach (var item in items)
            if (previouslySelected.Contains((item.Item.SubscriptionId,item.Url))) SubscriptionInbox.SelectedItems.Add(item);
        SubscriptionControl<TextBlock>("SubscriptionInboxTitle").Text=selected==null
            ? $"Входящие и результаты · {items.Length}"
            : $"Входящие и результаты: {selected.Title} · {items.Length}";
    }

    private void UpdateSubscriptionButtons()
    {
        if (_subscriptionsWindow==null) return;
        var source=SubscriptionSources.SelectedItem as SubscriptionSourceView;
        var items=SubscriptionInbox.SelectedItems.Cast<SubscriptionItemView>().ToArray();
        bool ready=!SubscriptionBusy;
        SubscriptionControl<Button>("AddSubscriptionButton").IsEnabled=ready;
        SubscriptionControl<Button>("CheckSubscriptionButton").IsEnabled=ready && source!=null && !source.Definition.Paused;
        // This button also retries reading a repaired persistence file.
        SubscriptionControl<Button>("CheckAllSubscriptionsButton").IsEnabled=ready;
        SubscriptionControl<Button>("CancelSubscriptionCheckButton").IsEnabled=_subscriptionCancellation!=null && !_subscriptionCancellation.IsCancellationRequested;
        var pause=SubscriptionControl<Button>("PauseSubscriptionButton");
        pause.IsEnabled=ready && source!=null;
        pause.Content=source?.Definition.Paused==true?"Возобновить":"Приостановить";
        SubscriptionControl<Button>("RemoveSubscriptionButton").IsEnabled=ready && source!=null;
        SubscriptionControl<Button>("QueueSubscriptionItemsButton").IsEnabled=ready && items.Any(x=>x.Item.State is "New" or "Skipped" or "Queued");
        SubscriptionControl<Button>("SkipSubscriptionItemsButton").IsEnabled=ready && items.Any(x=>x.Item.State=="New");
        SubscriptionControl<Button>("OpenSubscriptionItemButton").IsEnabled=items.Length==1;
        SubscriptionControl<TextBox>("SubscriptionUrlBox").IsEnabled=ready;
        SubscriptionControl<TextBox>("SubscriptionFeedBox").IsEnabled=ready;
        SubscriptionControl<TextBox>("NewSubscriptionNameBox").IsEnabled=ready;
        var nameBox=SubscriptionControl<TextBox>("SubscriptionNameBox");
        nameBox.IsEnabled=ready && source!=null;
        SubscriptionControl<Button>("SaveSubscriptionNameButton").IsEnabled=ready && source!=null &&
            !string.IsNullOrWhiteSpace(nameBox.Text) && nameBox.Text!=source.Definition.ShortName;
        SubscriptionControl<CheckBox>("DownloadExistingCheck").IsEnabled=ready;
    }

    private void UpdateSubscriptionNameEditor()
    {
        if (_subscriptionsWindow==null) return;
        var source=SubscriptionSources.SelectedItem as SubscriptionSourceView;
        string id=source?.Id??"";
        string saved=source?.Definition.ShortName??"";
        // A refresh replaces grid projections. Preserve an unsaved edit when
        // the same source and stored name are still selected.
        if (_subscriptionNameSelectionId==id && _subscriptionNameSavedValue==saved) return;
        _subscriptionNameSelectionId=id;
        _subscriptionNameSavedValue=saved;
        SubscriptionControl<TextBox>("SubscriptionNameBox").Text=saved;
    }

    private void SubscriptionStatus(string message,bool error=false)
    {
        if (_subscriptionsWindow==null || _subscriptionsDisposed || _disposed) return;
        var text=SubscriptionControl<TextBlock>("SubscriptionStatusText");
        text.Text=message;
        text.Foreground=error?Brushes.LightSalmon:Brushes.LightGray;
        UpdateSubscriptionButtons();
    }

    private async Task AddSubscriptionAsync()
    {
        if (SubscriptionBusy || _subscriptionsWindow==null) return;
        var window=_subscriptionsWindow;
        string url=SubscriptionControl<TextBox>("SubscriptionUrlBox").Text.Trim();
        string feed=SubscriptionControl<TextBox>("SubscriptionFeedBox").Text.Trim();
        string shortName=SubscriptionControl<TextBox>("NewSubscriptionNameBox").Text.Trim();
        bool existing=SubscriptionControl<CheckBox>("DownloadExistingCheck").IsChecked==true;
        if (!AppPersistence.ValidUrl(url) || (feed.Length>0 && !AppPersistence.ValidUrl(feed)))
        { SubscriptionStatus("Укажите полный HTTP(S) URL канала или плейлиста и, при необходимости, RSS / Atom.",true);return; }
        SubscriptionDefinition? added=null;
        _subscriptionMutation=true;
        SubscriptionStatus("Сохраняем подписку…");
        try
        {
            added=await Task.Run(()=>SubscriptionStorage.Add(url,existing,feed,shortName));
            if (!CurrentSubscriptionsWindow(window)) return;
            SubscriptionControl<TextBox>("SubscriptionUrlBox").Clear();
            SubscriptionControl<TextBox>("SubscriptionFeedBox").Clear();
            SubscriptionControl<TextBox>("NewSubscriptionNameBox").Clear();
            await ReloadSubscriptionsAsync();
            SubscriptionSources.SelectedItem=SubscriptionSources.Items.Cast<SubscriptionSourceView>().FirstOrDefault(x=>x.Id==added.Id);
        }
        catch(Exception e) { if (CurrentSubscriptionsWindow(window)) SubscriptionStatus("Не удалось добавить подписку: "+e.Message,true); }
        finally { _subscriptionMutation=false;UpdateSubscriptionButtons(); }
        if (added!=null && CurrentSubscriptionsWindow(window)) await CheckSubscriptionsAsync([added.Id]);
    }

    private async Task SaveSubscriptionNameAsync()
    {
        if (SubscriptionBusy || _subscriptionsWindow==null || SubscriptionSources.SelectedItem is not SubscriptionSourceView selected) return;
        string name=SubscriptionControl<TextBox>("SubscriptionNameBox").Text;
        if (string.IsNullOrWhiteSpace(name)) { SubscriptionStatus("Введите краткое имя подписки для папки загрузки.",true);return; }
        _subscriptionMutation=true;
        UpdateSubscriptionButtons();
        try
        {
            await Task.Run(()=>SubscriptionStorage.SetShortName(selected.Id,name));
            await ReloadSubscriptionsAsync();
            SubscriptionStatus("Имя сохранено. Новые задания будут скачиваться в подпапку с этим именем; уже добавленные задания сохранят прежнюю папку.");
        }
        catch(Exception e) { SubscriptionStatus("Не удалось сохранить имя подписки: "+e.Message,true); }
        finally { _subscriptionMutation=false;UpdateSubscriptionButtons(); }
    }

    private async Task CheckSubscriptionsAsync(IEnumerable<string> subscriptionIds)
    {
        if (SubscriptionBusy || _subscriptionsWindow==null) return;
        var ids=subscriptionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length==0) { SubscriptionStatus("Нет активных подписок для проверки. Приостановленные подписки можно возобновить.");return; }
        var window=_subscriptionsWindow;
        // Capture WPF input before starting any background work.
        string cookies=B("CookiesBox").Text.Trim();
        using var cancel=new CancellationTokenSource();
        _subscriptionCancellation=cancel;
        UpdateSubscriptionButtons();
        int succeeded=0,failed=0,queued=0;
        bool cancelled=false;
        string queueError="";
        try
        {
            using var scanner=new SubscriptionScanner(_ytDlp);
            foreach (string id in ids)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var source=await Task.Run(()=>SubscriptionStorage.Read().Subscriptions.FirstOrDefault(x=>x.Id==id),cancel.Token);
                if (source==null || source.Paused) continue;
                string kind=PornHubChannel.ListingUrl(source.Url)!=null?"Проверка публикаций Pornhub (до 15 минут)":"Полная проверка";
                SubscriptionStatus($"{kind}: {(source.Title.Length>0?source.Title:source.Url)}… Отмена не меняет исходную отметку.");
                try
                {
                    var scan=await Task.Run(()=>scanner.ScanAsync(source,cookies,cancel.Token),cancel.Token);
                    var snapshot=await Task.Run(()=>
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        var history=_storage.ReadHistory();
                        cancel.Token.ThrowIfCancellationRequested();
                        return SubscriptionStorage.ApplyScan(id,scan,history.Keys,cancel.Token);
                    },cancel.Token);
                    succeeded++;
                    if (!CurrentSubscriptionsWindow(window)) break;
                    // Only the explicit first-scan checkbox adds existing items.
                    // Queue acceptance is recorded after the main UI confirms it.
                    var initial=snapshot.Items.Where(x=>x.SubscriptionId==id && x.State=="New" && x.InitialQueuePending).ToArray();
                    if (initial.Length>0 && !cancel.IsCancellationRequested)
                    {
                        try { queued+=await QueueSubscriptionCandidatesAsync(initial); }
                        catch(Exception e)
                        {
                            // Enumeration was complete and is already durable.
                            // An acceptance/write failure must not pretend that
                            // the scan itself failed or undo its initial choice.
                            failed++;
                            queueError="Не удалось подтвердить очередь: "+e.Message+
                                " Уже принятые ссылки могут быть в обычной очереди; повторите ручную проверку после исправления.";
                        }
                    }
                    await ReloadSubscriptionsAsync();
                }
                catch(OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    cancelled=true;
                    await RecordSubscriptionScanFailureAsync(id,"Проверка отменена пользователем; предыдущие результаты сохранены.");
                    break;
                }
                catch(Exception e)
                {
                    failed++;
                    await RecordSubscriptionScanFailureAsync(id,e.Message);
                    if (CurrentSubscriptionsWindow(window))
                    {
                        await ReloadSubscriptionsAsync();
                        SubscriptionStatus("Не удалось проверить "+(source.Title.Length>0?source.Title:source.Url)+": "+e.Message,true);
                    }
                }
            }
        }
        catch(OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled=true; }
        catch(Exception e) { failed++;SubscriptionStatus("Не удалось начать проверку: "+e.Message,true); }
        finally
        {
            if (ReferenceEquals(_subscriptionCancellation,cancel)) _subscriptionCancellation=null;
            if (CurrentSubscriptionsWindow(window))
            {
                await ReloadSubscriptionsAsync();
                SubscriptionStatus((cancelled?"Проверка отменена.":"Проверка завершена.")+
                    $" Полных проверок: {succeeded}; ошибок: {failed}; добавлено в очередь: {queued}. "+
                    "Новые материалы — во «Входящих». Скачивание только кнопкой Start."+
                    (queueError.Length>0?" "+queueError:""),failed>0);
            }
            UpdateSubscriptionButtons();
        }
    }

    private async Task RecordSubscriptionScanFailureAsync(string id,string message)
    {
        try { await Task.Run(()=>SubscriptionStorage.RecordFailure(id,message)); }
        catch(Exception e) { SubscriptionStatus("Не удалось сохранить ошибку проверки: "+e.Message,true); }
    }

    private async Task<int> QueueSubscriptionCandidatesAsync(IReadOnlyList<SubscriptionItem> candidates)
    {
        if (candidates.Count==0) return 0;
        var reconciled=await Task.Run(()=>SubscriptionStorage.ReconcileSuccess(_storage.ReadHistory().Keys));
        var eligibleKeys=reconciled.Items.Where(x=>x.State is "New" or "Skipped" or "Queued")
            .Select(x=>(x.SubscriptionId,x.Url)).ToHashSet();
        var currentItems=reconciled.Items.ToDictionary(x=>(x.SubscriptionId,x.Url));
        candidates=candidates.Where(x=>eligibleKeys.Contains((x.SubscriptionId,x.Url)))
            .Select(x=>currentItems[(x.SubscriptionId,x.Url)]).ToArray();
        if (candidates.Count==0) return 0;
        var sources=reconciled.Subscriptions.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var acknowledgements=new List<(string Id,string[] Urls)>();
        var acceptedUrls=new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(x=>x.SubscriptionId,StringComparer.Ordinal))
        {
            if (_subscriptionCancellation?.IsCancellationRequested==true) break;
            if (!sources.TryGetValue(group.Key,out var source)) continue;
            string suggestion=AppPersistence.ValidUrl(source.Title)?"":source.Title;
            string name=source.ShortName.Length>0?source.ShortName:SubscriptionNames.Normalize(suggestion,
                "Подписка "+source.Id[..Math.Min(8,source.Id.Length)]);
            var requested=group.Select(x=>x.Url).ToHashSet(StringComparer.Ordinal);
            var titles=group.ToDictionary(x=>x.Url,x=>x.Title,StringComparer.Ordinal);
            var accepted=QueueSubscriptionUrls(requested,source.Id,name,titles).Where(requested.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (accepted.Length==0) continue;
            acknowledgements.Add((group.Key,accepted));
            acceptedUrls.UnionWith(accepted);
        }
        if (acknowledgements.Count==0) return 0;
        await Task.Run(()=>
        {
            var current=SubscriptionStorage.Read();
            foreach (var acknowledgement in acknowledgements)
            {
                var urls=acknowledgement.Urls.ToHashSet(StringComparer.Ordinal);
                // A genuine concurrent success may already have reconciled a
                // row. Never turn Processed/AlreadyProcessed back into Queued.
                var eligible=current.Items.Where(x=>x.SubscriptionId==acknowledgement.Id && urls.Contains(x.Url) && x.State is "New" or "Skipped" or "Queued")
                    .Select(x=>x.Url).ToArray();
                if (eligible.Length>0) SubscriptionStorage.MarkQueued(acknowledgement.Id,eligible);
            }
        });
        return acceptedUrls.Count;
    }

    private async Task QueueSelectedSubscriptionItemsAsync()
    {
        if (SubscriptionBusy || _subscriptionsWindow==null) return;
        var selected=SubscriptionInbox.SelectedItems.Cast<SubscriptionItemView>().Select(x=>x.Item)
            .Where(x=>x.State is "New" or "Skipped" or "Queued").ToArray();
        if (selected.Length==0) return;
        _subscriptionMutation=true;
        UpdateSubscriptionButtons();
        try
        {
            int count=await QueueSubscriptionCandidatesAsync(selected);
            await ReloadSubscriptionsAsync();
            SubscriptionStatus(count>0?$"В обычной очереди: {count}. Запуск только кнопкой Start.":"В очередь ничего не добавлено. Материалы остаются во «Входящих».");
        }
        catch(Exception e) { SubscriptionStatus("Не удалось подтвердить добавление в очередь: "+e.Message+" Уже принятые ссылки могут быть в обычной очереди; повторите действие после исправления.",true); }
        finally { _subscriptionMutation=false;UpdateSubscriptionButtons(); }
    }

    private async Task SkipSelectedSubscriptionItemsAsync()
    {
        if (SubscriptionBusy || _subscriptionsWindow==null) return;
        var selected=SubscriptionInbox.SelectedItems.Cast<SubscriptionItemView>().Select(x=>x.Item).Where(x=>x.State=="New").ToArray();
        if (selected.Length==0) return;
        _subscriptionMutation=true;
        UpdateSubscriptionButtons();
        try
        {
            await Task.Run(()=>
            {
                foreach (var group in selected.GroupBy(x=>x.SubscriptionId,StringComparer.Ordinal))
                    SubscriptionStorage.Skip(group.Key,group.Select(x=>x.Url));
            });
            await ReloadSubscriptionsAsync();
            SubscriptionStatus("Материалы пропущены. Они не считаются обработанными и доступны для ручного добавления в очередь.");
        }
        catch(Exception e) { SubscriptionStatus("Не удалось пропустить материалы: "+e.Message,true); }
        finally { _subscriptionMutation=false;UpdateSubscriptionButtons(); }
    }

    private async Task ToggleSubscriptionPauseAsync()
    {
        if (SubscriptionBusy || SubscriptionSources.SelectedItem is not SubscriptionSourceView selected) return;
        _subscriptionMutation=true;
        UpdateSubscriptionButtons();
        try
        {
            await Task.Run(()=>SubscriptionStorage.SetPaused(selected.Id,!selected.Definition.Paused));
            await ReloadSubscriptionsAsync();
            SubscriptionStatus(selected.Definition.Paused?"Подписка возобновлена. Следующая проверка запускается вручную.":"Подписка приостановлена: «Проверить все» её пропустит.");
        }
        catch(Exception e) { SubscriptionStatus("Не удалось изменить подписку: "+e.Message,true); }
        finally { _subscriptionMutation=false;UpdateSubscriptionButtons(); }
    }

    private async Task RemoveSubscriptionAsync()
    {
        if (SubscriptionBusy || SubscriptionSources.SelectedItem is not SubscriptionSourceView selected || _subscriptionsWindow==null) return;
        if (MessageBox.Show(_subscriptionsWindow,"Удалить подписку и её список материалов?\n"+selected.Title+
            "\n\nФайлы, история успешных скачиваний и обычная очередь останутся на месте.","Удалить подписку",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        if (SubscriptionBusy) return;
        _subscriptionMutation=true;
        UpdateSubscriptionButtons();
        try
        {
            await Task.Run(()=>SubscriptionStorage.Remove(selected.Id));
            await ReloadSubscriptionsAsync();
            SubscriptionStatus("Подписка удалена. Файлы, история и обычная очередь сохранены.");
        }
        catch(Exception e) { SubscriptionStatus("Не удалось удалить подписку: "+e.Message,true); }
        finally { _subscriptionMutation=false;UpdateSubscriptionButtons(); }
    }

    private void OpenSubscriptionItem()
    {
        if (_subscriptionsWindow==null || SubscriptionInbox.SelectedItem is not SubscriptionItemView row) return;
        try { OpenUrl(row.Url); }
        catch(Exception e) { SubscriptionStatus("Не удалось открыть ссылку: "+e.Message,true); }
    }

    private async Task NotifySubscriptionDuplicatesAsync(SubscriptionSnapshot snapshot,Window window)
    {
        if (_subscriptionNoticeShowing || !CurrentSubscriptionsWindow(window) || !window.IsVisible) return;
        var notices=snapshot.Items.Where(x=>x.State=="AlreadyProcessed" && !x.DuplicateNotified).ToArray();
        if (notices.Length==0) return;
        _subscriptionNoticeShowing=true;
        bool acknowledged=false;
        try
        {
            var names=snapshot.Subscriptions.ToDictionary(x=>x.Id,x=>x.Title.Length>0?x.Title:x.Url,StringComparer.Ordinal);
            string Description(SubscriptionItem item)
            {
                string destination=names.GetValueOrDefault(item.SubscriptionId,"Подписка");
                string origin=names.GetValueOrDefault(item.DuplicateSubscriptionId,"другая / удалённая подписка");
                return destination+" ← "+origin+"\n"+(item.Title.Length>0?item.Title:item.Url);
            }
            string list=string.Join("\n\n",notices.Take(8).Select(Description));
            if (notices.Length>8) list+=$"\n\nИ ещё {notices.Length-8}. Полный список доступен во «Входящих».";
            MessageBox.Show(window,"Эти материалы уже успешно обработаны в другой подписке. Повторного скачивания нет.\n\n"+
                list,"Материалы уже обработаны",MessageBoxButton.OK,MessageBoxImage.Information);
            await Task.Run(()=>SubscriptionStorage.AcknowledgeDuplicates(notices));
            acknowledged=true;
        }
        catch(Exception e) { SubscriptionStatus("Не удалось сохранить подтверждение уведомления: "+e.Message,true); }
        finally
        {
            _subscriptionNoticeShowing=false;
            // A second real success may have arrived while the modal notice
            // was open. A fresh snapshot displays its independent notice.
            if (acknowledged && CurrentSubscriptionsWindow(window)) RefreshSubscriptions();
        }
    }

    private sealed class SubscriptionSourceView(SubscriptionDefinition definition)
    {
        public SubscriptionDefinition Definition { get; }=definition;
        public string Id=>Definition.Id;
        public string Url=>Definition.Url;
        public string Title=>Definition.Title.Length>0?Definition.Title:Definition.Url;
        public string ShortName=>Definition.ShortName.Length>0?Definition.ShortName:"После проверки";
        public string StatusLabel=>Definition.Paused?"Приостановлена":!Definition.Initialized?"Нужна первая проверка":Definition.LastError.Length>0?"Ошибка проверки":"Активна";
        public string LastSuccessLabel=>Definition.LastSuccessUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"Ещё не проверена";
        public string Message=>Definition.LastError.Length>0?Definition.LastError:Definition.LastWarning;
    }

    private sealed class SubscriptionItemView(SubscriptionItem item)
    {
        public SubscriptionItem Item { get; }=item;
        public string Url=>Item.Url;
        public string Title=>Item.Title.Length>0?Item.Title:Item.Url;
        public string StateLabel=>Item.StateLabel;
        public string DiscoveredLabel=>Item.DiscoveredAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    }
}

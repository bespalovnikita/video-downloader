using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.Native;

internal delegate IReadOnlyList<string> QueueSubscriptionItems(IEnumerable<string> urls,string subscriptionId,
    string subscriptionName,IReadOnlyDictionary<string,string>? titles);

// Owns the modeless subscriptions window and all of its persisted and in-flight
// state. Main-window queue acceptance is an explicit callback, never live Add.
internal sealed partial class SubscriptionController : IDisposable
{
    private readonly Window _owner;
    private readonly SubscriptionStore _store;
    private readonly string _ytDlp;
    private readonly string _viewPath;
    private readonly Func<Dictionary<string,DateTimeOffset?>> _readHistory;
    private readonly Func<string> _readCookies;
    private readonly QueueSubscriptionItems _queueUrls;
    private readonly Action<string> _openUrl;
    private readonly Action<string> _log;
    private readonly HashSet<string> _successes=new(StringComparer.Ordinal);
    private readonly SubscriptionCheckProgressController _checkProgress;
    private Window? _window;
    private SubscriptionSnapshot? _snapshot;
    private CancellationTokenSource? _cancellation;
    private bool _mutation;
    private bool _noticeShowing;
    private bool _disposed;
    private int _refreshVersion;
    private bool _refreshQueued;
    private bool _binding;
    private string _nameSelectionId="";
    private string _nameSavedValue="";
    private Dispatcher Dispatcher=>_owner.Dispatcher;

    internal SubscriptionController(Window owner,SubscriptionStore store,string ytDlp,string viewPath,
        Func<Dictionary<string,DateTimeOffset?>> readHistory,Func<string> readCookies,
        QueueSubscriptionItems queueUrls,Action<string> openUrl,Action<string> log)
    {
        _owner=owner;_store=store;_ytDlp=ytDlp;_viewPath=viewPath;
        _readHistory=readHistory;_readCookies=readCookies;_queueUrls=queueUrls;_openUrl=openUrl;_log=log;
        _checkProgress=new SubscriptionCheckProgressController(owner.Dispatcher,CurrentWindow,
            ()=>_cancellation?.IsCancellationRequested==true);
    }

    private T Control<T>(string name) where T:class =>
        (T)(_window?.FindName(name) ?? throw new InvalidOperationException("Missing subscription control: "+name));
    private DataGrid Sources => Control<DataGrid>("SubscriptionGrid");
    private DataGrid Inbox => Control<DataGrid>("SubscriptionInboxGrid");
    private bool Busy => _mutation || _cancellation!=null || _checkProgress.IsActive;

    internal void Open()
    {
        if (_disposed) return;
        if (_window!=null)
        {
            _window.Show();
            _window.WindowState=WindowState.Normal;
            _window.Activate();
            Refresh();
            return;
        }
        try
        {
            string path=_viewPath;
            using var reader=XmlReader.Create(path);
            var window=(Window)XamlReader.Load(reader);
            window.Owner=_owner;
            // The XAML has standalone dark styles for both runtimes. Also make
            // the main window's resource catalog available to this owned window.
            window.Resources.MergedDictionaries.Add(_owner.Resources);
            _window=window;
            Control<Button>("AddSubscriptionButton").Click+=async(_,_)=>await AddAsync();
            Control<Button>("SaveSubscriptionNameButton").Click+=async(_,_)=>await SaveNameAsync();
            Control<Button>("CheckSubscriptionButton").Click+=async(_,_)=>
            {
                if (Sources.SelectedItem is SubscriptionSourceView source)
                    await CheckAsync([source.Id]);
            };
            Control<Button>("CheckAllSubscriptionsButton").Click+=async(_,_)=>
            {
                if (Busy) return;
                var snapshot=await ReloadAsync();
                if (snapshot!=null)
                    await CheckAsync(snapshot.Subscriptions.Where(x=>!x.Paused).Select(x=>x.Id).ToArray());
            };
            Control<Button>("CancelSubscriptionCheckButton").Click+=(_,_)=>
            {
                _cancellation?.Cancel();
                _checkProgress.Cancel();
                Status("Проверка отменяется. Уже сохранённые результаты и обычная очередь останутся на месте.");
            };
            Control<Button>("PauseSubscriptionButton").Click+=async(_,_)=>await TogglePauseAsync();
            Control<Button>("RemoveSubscriptionButton").Click+=async(_,_)=>await RemoveAsync();
            Control<Button>("QueueSubscriptionItemsButton").Click+=async(_,_)=>await QueueSelectedAsync();
            Control<Button>("SkipSubscriptionItemsButton").Click+=async(_,_)=>await SkipSelectedAsync();
            Control<Button>("OpenSubscriptionItemButton").Click+=(_,_)=>OpenItem();
            Control<TextBox>("SubscriptionUrlBox").KeyDown+=async(_,e)=>
            {
                if (e.Key==Key.Enter && !Busy) { e.Handled=true;await AddAsync(); }
            };
            Control<TextBox>("SubscriptionNameBox").TextChanged+=(_,_)=>UpdateButtons();
            Control<TextBox>("SubscriptionNameBox").KeyDown+=async(_,e)=>
            {
                if (e.Key==Key.Enter && !Busy) { e.Handled=true;await SaveNameAsync(); }
            };
            Sources.SelectionChanged+=(_,_)=>
            {
                if (_binding) return;
                ShowInbox();UpdateNameEditor();UpdateButtons();
            };
            Inbox.SelectionChanged+=(_,_)=>UpdateButtons();
            Inbox.MouseDoubleClick+=(_,_)=>OpenItem();
            window.Closed+=(_,_)=>
            {
                if (!ReferenceEquals(_window,window)) return;
                _cancellation?.Cancel();
                _checkProgress.Stop();
                _window=null;
                _nameSelectionId="";
                _nameSavedValue="";
                _refreshVersion++;
            };
            window.Show();
            UpdateButtons();
            Refresh();
        }
        catch(Exception e)
        {
            _window=null;
            MessageBox.Show(_owner,"Не удалось открыть подписки:\n"+DisplayError(e),"Подписки",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }

    public void Dispose()
    {
        _disposed=true;
        _cancellation?.Cancel();
        _checkProgress.Stop();
        _window?.Close();
        _window=null;
        _refreshVersion++;
    }

    internal void Refresh()
    {
        if (_disposed || _window==null) return;
        if (!Dispatcher.CheckAccess()) { _=Dispatcher.BeginInvoke(Refresh);return; }
        if (_refreshQueued) return;
        _refreshQueued=true;
        _=Dispatcher.BeginInvoke(async () =>
        {
            _refreshQueued=false;
            await ReloadAsync();
        });
    }

    private bool CurrentWindow(Window? window) =>
        window!=null && !_disposed && ReferenceEquals(window,_window);

    private async Task<SubscriptionSnapshot?> ReloadAsync(bool notify=true)
    {
        var window=_window;
        if (!CurrentWindow(window)) return null;
        int version=++_refreshVersion;
        try
        {
            var snapshot=await Task.Run(() =>
            {
                // Recover a success that reached the global journal before a
                // previous UI instance could update its subscription item.
                var history=_readHistory();
                return _store.ReconcileSuccess(history.Keys);
            });
            if (!CurrentWindow(window)) return null;
            if (version!=_refreshVersion) return snapshot;
            string? selected=(Sources.SelectedItem as SubscriptionSourceView)?.Id;
            _snapshot=snapshot;
            var rows=snapshot.Subscriptions.Select(x=>new SubscriptionSourceView(x)).ToArray();
            _binding=true;
            try
            {
                Sources.ItemsSource=rows;
                Sources.SelectedItem=rows.FirstOrDefault(x=>x.Id==selected) ?? rows.FirstOrDefault();
            }
            finally { _binding=false; }
            ShowInbox();
            UpdateNameEditor();
            UpdateButtons();
            if (notify) await NotifyDuplicatesAsync(snapshot,window!);
            return snapshot;
        }
        catch(Exception e)
        {
            if (CurrentWindow(window))
                Status("Не удалось прочитать подписки или историю успехов: "+DisplayError(e)+
                    " Сохранённый файл не заменён пустым списком. После исправления повторите «Проверить все».",true);
            return null;
        }
    }

    private void ShowInbox()
    {
        if (_window==null) return;
        var selected=Sources.SelectedItem as SubscriptionSourceView;
        var previouslySelected=Inbox.SelectedItems.Cast<SubscriptionItemView>()
            .Select(x=>(x.Item.SubscriptionId,x.Url)).ToHashSet();
        var items=(_snapshot?.Items ?? [])
            .Where(x=>selected==null || x.SubscriptionId==selected.Id)
            .Where(x=>x.State!="Baseline")
            .OrderBy(x=>x.State=="New"?0:x.State=="Queued"?1:2)
            .ThenByDescending(x=>x.DiscoveredAtUtc).ThenBy(x=>x.Url,StringComparer.Ordinal)
            .Select(x=>new SubscriptionItemView(x)).ToArray();
        Inbox.ItemsSource=items;
        foreach (var item in items)
            if (previouslySelected.Contains((item.Item.SubscriptionId,item.Url))) Inbox.SelectedItems.Add(item);
        Control<TextBlock>("SubscriptionInboxTitle").Text=selected==null
            ? $"Входящие и результаты · {items.Length}"
            : $"Входящие и результаты: {selected.Title} · {items.Length}";
    }

    private void UpdateButtons()
    {
        if (_window==null) return;
        var source=Sources.SelectedItem as SubscriptionSourceView;
        var items=Inbox.SelectedItems.Cast<SubscriptionItemView>().ToArray();
        bool ready=!Busy;
        Control<Button>("AddSubscriptionButton").IsEnabled=ready;
        Control<Button>("CheckSubscriptionButton").IsEnabled=ready && source!=null && !source.Definition.Paused;
        // This button also retries reading a repaired persistence file.
        Control<Button>("CheckAllSubscriptionsButton").IsEnabled=ready;
        Control<Button>("CancelSubscriptionCheckButton").IsEnabled=_cancellation!=null && !_cancellation.IsCancellationRequested;
        var pause=Control<Button>("PauseSubscriptionButton");
        pause.IsEnabled=ready && source!=null;
        pause.Content=source?.Definition.Paused==true?"Возобновить":"Приостановить";
        Control<Button>("RemoveSubscriptionButton").IsEnabled=ready && source!=null;
        Control<Button>("QueueSubscriptionItemsButton").IsEnabled=ready && items.Any(x=>x.Item.State is "New" or "Skipped" or "Queued");
        Control<Button>("SkipSubscriptionItemsButton").IsEnabled=ready && items.Any(x=>x.Item.State=="New");
        Control<Button>("OpenSubscriptionItemButton").IsEnabled=items.Length==1;
        Control<TextBox>("SubscriptionUrlBox").IsEnabled=ready;
        Control<TextBox>("SubscriptionFeedBox").IsEnabled=ready;
        Control<TextBox>("NewSubscriptionNameBox").IsEnabled=ready;
        var nameBox=Control<TextBox>("SubscriptionNameBox");
        nameBox.IsEnabled=ready && source!=null;
        Control<Button>("SaveSubscriptionNameButton").IsEnabled=ready && source!=null &&
            !string.IsNullOrWhiteSpace(nameBox.Text) && nameBox.Text!=source.Definition.ShortName;
        Control<CheckBox>("DownloadExistingCheck").IsEnabled=ready;
    }

    private void UpdateNameEditor()
    {
        if (_window==null) return;
        var source=Sources.SelectedItem as SubscriptionSourceView;
        string id=source?.Id??"";
        string saved=source?.Definition.ShortName??"";
        // A refresh replaces grid projections. Preserve an unsaved edit when
        // the same source and stored name are still selected.
        if (_nameSelectionId==id && _nameSavedValue==saved) return;
        _nameSelectionId=id;
        _nameSavedValue=saved;
        Control<TextBox>("SubscriptionNameBox").Text=saved;
    }

    private void Status(string message,bool error=false)
    {
        if (_window==null || _disposed) return;
        var text=Control<TextBlock>("SubscriptionStatusText");
        text.Text=message;
        text.Foreground=error?Brushes.LightSalmon:Brushes.LightGray;
        UpdateButtons();
    }

    private string DisplayError(Exception error)
    {
        // Keep the original diagnostic available in the main log even when
        // this operation has no persisted LastError (for example a UI edit).
        _log(error.Message);
        return ErrorMessages.ForException(error);
    }

    private async Task AddAsync()
    {
        if (Busy || _window==null) return;
        var window=_window;
        string url=Control<TextBox>("SubscriptionUrlBox").Text.Trim();
        string feed=Control<TextBox>("SubscriptionFeedBox").Text.Trim();
        string shortName=Control<TextBox>("NewSubscriptionNameBox").Text.Trim();
        bool existing=Control<CheckBox>("DownloadExistingCheck").IsChecked==true;
        if (!AppPersistence.ValidUrl(url) || (feed.Length>0 && !AppPersistence.ValidUrl(feed)))
        { Status("Укажите полный HTTP(S) URL канала или плейлиста и, при необходимости, RSS / Atom.",true);return; }
        SubscriptionDefinition? added=null;
        _mutation=true;
        Status("Сохраняем подписку…");
        try
        {
            added=await Task.Run(()=>_store.Add(url,existing,feed,shortName));
            if (!CurrentWindow(window)) return;
            Control<TextBox>("SubscriptionUrlBox").Clear();
            Control<TextBox>("SubscriptionFeedBox").Clear();
            Control<TextBox>("NewSubscriptionNameBox").Clear();
            await ReloadAsync();
            Sources.SelectedItem=Sources.Items.Cast<SubscriptionSourceView>().FirstOrDefault(x=>x.Id==added.Id);
        }
        catch(Exception e) { if (CurrentWindow(window)) Status("Не удалось добавить подписку: "+DisplayError(e),true); }
        finally { _mutation=false;UpdateButtons(); }
        if (added!=null && CurrentWindow(window)) await CheckAsync([added.Id]);
    }

    private async Task SaveNameAsync()
    {
        if (Busy || _window==null || Sources.SelectedItem is not SubscriptionSourceView selected) return;
        string name=Control<TextBox>("SubscriptionNameBox").Text;
        if (string.IsNullOrWhiteSpace(name)) { Status("Введите краткое имя подписки для папки загрузки.",true);return; }
        _mutation=true;
        UpdateButtons();
        try
        {
            await Task.Run(()=>_store.SetShortName(selected.Id,name));
            await ReloadAsync();
            Status("Имя сохранено. Новые задания будут скачиваться в подпапку с этим именем; уже добавленные задания сохранят прежнюю папку.");
        }
        catch(Exception e) { Status("Не удалось сохранить имя подписки: "+DisplayError(e),true); }
        finally { _mutation=false;UpdateButtons(); }
    }

    private async Task CheckAsync(IEnumerable<string> subscriptionIds)
    {
        if (Busy || _window==null) return;
        var ids=subscriptionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length==0) { Status("Нет активных подписок для проверки. Приостановленные подписки можно возобновить.");return; }
        var window=_window;
        // Capture WPF input before starting any background work.
        string cookies=_readCookies();
        using var cancel=new CancellationTokenSource();
        _cancellation=cancel;
        long progressVersion=_checkProgress.Begin(window,ids.Length);
        UpdateButtons();
        int succeeded=0,failed=0,queued=0;
        bool cancelled=false;
        string queueError="";
        try
        {
            using var scanner=new SubscriptionScanner(_ytDlp);
            foreach (string id in ids)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var source=await Task.Run(()=>_store.Read().Subscriptions.FirstOrDefault(x=>x.Id==id),cancel.Token);
                if (source==null || source.Paused) {_checkProgress.SkipSource(progressVersion);continue;}
                _checkProgress.StartSource(progressVersion,source);
                string kind=PornHubChannel.ListingUrl(source.Url)!=null?"Проверка публикаций Pornhub (до 15 минут)":"Полная проверка";
                Status($"{kind}: {(source.Title.Length>0?source.Title:source.Url)}… Отмена не меняет исходную отметку.");
                try
                {
                    var progress=_checkProgress.CreateObserver(progressVersion,id);
                    var scan=await Task.Run(()=>scanner.ScanAsync(source,cookies,cancel.Token,progress),cancel.Token);
                    _checkProgress.Report(progressVersion,id,new("Ready",scan.Items.Count));
                    _checkProgress.SetStage(progressVersion,id,5,"Сравниваем и сохраняем результат…");
                    var snapshot=await Task.Run(()=>
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        var history=_readHistory();
                        cancel.Token.ThrowIfCancellationRequested();
                        return _store.ApplyScan(id,scan,history.Keys,cancel.Token);
                    },cancel.Token);
                    succeeded++;
                    if (!CurrentWindow(window)) break;
                    // Only the explicit first-scan checkbox adds existing items.
                    // Queue acceptance is recorded after the main UI confirms it.
                    var initial=snapshot.Items.Where(x=>x.SubscriptionId==id && x.State=="New" && x.InitialQueuePending).ToArray();
                    if (initial.Length>0 && !cancel.IsCancellationRequested)
                    {
                        _checkProgress.SetStage(progressVersion,id,6,"Добавляем исходные материалы в очередь…");
                        try { queued+=await QueueCandidatesAsync(initial); }
                        catch(Exception e)
                        {
                            // Enumeration was complete and is already durable.
                            // An acceptance/write failure must not pretend that
                            // the scan itself failed or undo its initial choice.
                            failed++;
                            queueError="Не удалось подтвердить очередь: "+DisplayError(e)+
                                " Уже принятые ссылки могут быть в обычной очереди; повторите ручную проверку после исправления.";
                        }
                    }
                    await ReloadAsync();
                    _checkProgress.CompleteSource(progressVersion,id);
                }
                catch(OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    cancelled=true;
                    await RecordScanFailureAsync(id,"Проверка отменена пользователем; предыдущие результаты сохранены.");
                    break;
                }
                catch(Exception e)
                {
                    failed++;
                    string displayError=DisplayError(e);
                    _checkProgress.SetStage(progressVersion,id,7,"Источник не проверен: "+displayError);
                    await RecordScanFailureAsync(id,e.Message);
                    if (CurrentWindow(window))
                    {
                        await ReloadAsync();
                        Status("Не удалось проверить "+(source.Title.Length>0?source.Title:source.Url)+": "+displayError,true);
                    }
                    _checkProgress.CompleteSource(progressVersion,id);
                }
            }
        }
        catch(OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled=true; }
        catch(Exception e) { failed++;Status("Не удалось начать проверку: "+DisplayError(e),true); }
        finally
        {
            if (ReferenceEquals(_cancellation,cancel)) _cancellation=null;
            if (CurrentWindow(window))
            {
                await ReloadAsync();
                _checkProgress.Finish(progressVersion,cancelled,succeeded,failed,queued);
                Status((cancelled?"Проверка отменена.":"Проверка завершена.")+
                    $" Полных проверок: {succeeded}; ошибок: {failed}; добавлено в очередь: {queued}. "+
                    "Новые материалы — во «Входящих». Скачивание только кнопкой Start."+
                    (queueError.Length>0?" "+queueError:""),failed>0);
            }
            UpdateButtons();
        }
    }

    private async Task RecordScanFailureAsync(string id,string message)
    {
        try { await Task.Run(()=>_store.RecordFailure(id,message)); }
        catch(Exception e) { Status("Не удалось сохранить ошибку проверки: "+DisplayError(e),true); }
    }

    private async Task<int> QueueCandidatesAsync(IReadOnlyList<SubscriptionItem> candidates)
    {
        if (candidates.Count==0) return 0;
        var reconciled=await Task.Run(()=>_store.ReconcileSuccess(_readHistory().Keys));
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
            if (_cancellation?.IsCancellationRequested==true) break;
            if (!sources.TryGetValue(group.Key,out var source)) continue;
            string suggestion=AppPersistence.ValidUrl(source.Title)?"":source.Title;
            string name=source.ShortName.Length>0?source.ShortName:SubscriptionNames.Normalize(suggestion,
                "Подписка "+source.Id[..Math.Min(8,source.Id.Length)]);
            var requested=group.Select(x=>x.Url).ToHashSet(StringComparer.Ordinal);
            var titles=group.ToDictionary(x=>x.Url,x=>x.Title,StringComparer.Ordinal);
            var accepted=_queueUrls(requested,source.Id,name,titles).Where(requested.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (accepted.Length==0) continue;
            acknowledgements.Add((group.Key,accepted));
            acceptedUrls.UnionWith(accepted);
        }
        if (acknowledgements.Count==0) return 0;
        await Task.Run(()=>
        {
            var current=_store.Read();
            foreach (var acknowledgement in acknowledgements)
            {
                var urls=acknowledgement.Urls.ToHashSet(StringComparer.Ordinal);
                // A genuine concurrent success may already have reconciled a
                // row. Never turn Processed/AlreadyProcessed back into Queued.
                var eligible=current.Items.Where(x=>x.SubscriptionId==acknowledgement.Id && urls.Contains(x.Url) && x.State is "New" or "Skipped" or "Queued")
                    .Select(x=>x.Url).ToArray();
                if (eligible.Length>0) _store.MarkQueued(acknowledgement.Id,eligible);
            }
        });
        return acceptedUrls.Count;
    }

    private async Task QueueSelectedAsync()
    {
        if (Busy || _window==null) return;
        var selected=Inbox.SelectedItems.Cast<SubscriptionItemView>().Select(x=>x.Item)
            .Where(x=>x.State is "New" or "Skipped" or "Queued").ToArray();
        if (selected.Length==0) return;
        _mutation=true;
        UpdateButtons();
        try
        {
            int count=await QueueCandidatesAsync(selected);
            await ReloadAsync();
            Status(count>0?$"В обычной очереди: {count}. Запуск только кнопкой Start.":"В очередь ничего не добавлено. Материалы остаются во «Входящих».");
        }
        catch(Exception e) { Status("Не удалось подтвердить добавление в очередь: "+DisplayError(e)+" Уже принятые ссылки могут быть в обычной очереди; повторите действие после исправления.",true); }
        finally { _mutation=false;UpdateButtons(); }
    }

    private async Task SkipSelectedAsync()
    {
        if (Busy || _window==null) return;
        var selected=Inbox.SelectedItems.Cast<SubscriptionItemView>().Select(x=>x.Item).Where(x=>x.State=="New").ToArray();
        if (selected.Length==0) return;
        _mutation=true;
        UpdateButtons();
        try
        {
            await Task.Run(()=>
            {
                foreach (var group in selected.GroupBy(x=>x.SubscriptionId,StringComparer.Ordinal))
                    _store.Skip(group.Key,group.Select(x=>x.Url));
            });
            await ReloadAsync();
            Status("Материалы пропущены. Они не считаются обработанными и доступны для ручного добавления в очередь.");
        }
        catch(Exception e) { Status("Не удалось пропустить материалы: "+DisplayError(e),true); }
        finally { _mutation=false;UpdateButtons(); }
    }

    private async Task TogglePauseAsync()
    {
        if (Busy || Sources.SelectedItem is not SubscriptionSourceView selected) return;
        _mutation=true;
        UpdateButtons();
        try
        {
            await Task.Run(()=>_store.SetPaused(selected.Id,!selected.Definition.Paused));
            await ReloadAsync();
            Status(selected.Definition.Paused?"Подписка возобновлена. Следующая проверка запускается вручную.":"Подписка приостановлена: «Проверить все» её пропустит.");
        }
        catch(Exception e) { Status("Не удалось изменить подписку: "+DisplayError(e),true); }
        finally { _mutation=false;UpdateButtons(); }
    }

    private async Task RemoveAsync()
    {
        if (Busy || Sources.SelectedItem is not SubscriptionSourceView selected || _window==null) return;
        if (MessageBox.Show(_window,"Удалить подписку и её список материалов?\n"+selected.Title+
            "\n\nФайлы, история успешных скачиваний и обычная очередь останутся на месте.","Удалить подписку",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        if (Busy) return;
        _mutation=true;
        UpdateButtons();
        try
        {
            await Task.Run(()=>_store.Remove(selected.Id));
            await ReloadAsync();
            Status("Подписка удалена. Файлы, история и обычная очередь сохранены.");
        }
        catch(Exception e) { Status("Не удалось удалить подписку: "+DisplayError(e),true); }
        finally { _mutation=false;UpdateButtons(); }
    }

    private void OpenItem()
    {
        if (_window==null || Inbox.SelectedItem is not SubscriptionItemView row) return;
        try { _openUrl(row.Url); }
        catch(Exception e) { Status("Не удалось открыть ссылку: "+DisplayError(e),true); }
    }

    private async Task NotifyDuplicatesAsync(SubscriptionSnapshot snapshot,Window window)
    {
        if (_noticeShowing || !CurrentWindow(window) || !window.IsVisible) return;
        var notices=snapshot.Items.Where(x=>x.State=="AlreadyProcessed" && !x.DuplicateNotified).ToArray();
        if (notices.Length==0) return;
        _noticeShowing=true;
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
            await Task.Run(()=>_store.AcknowledgeDuplicates(notices));
            acknowledged=true;
        }
        catch(Exception e) { Status("Не удалось сохранить подтверждение уведомления: "+DisplayError(e),true); }
        finally
        {
            _noticeShowing=false;
            // A second real success may have arrived while the modal notice
            // was open. A fresh snapshot displays its independent notice.
            if (acknowledged && CurrentWindow(window)) Refresh();
        }
    }

    internal sealed class SubscriptionSourceView(SubscriptionDefinition definition)
    {
        public SubscriptionDefinition Definition { get; }=definition;
        public string Id=>Definition.Id;
        public string Url=>Definition.Url;
        public string Title=>Definition.Title.Length>0?Definition.Title:Definition.Url;
        public string ShortName=>Definition.ShortName.Length>0?Definition.ShortName:"После проверки";
        public string StatusLabel=>Definition.Paused?"Приостановлена":!Definition.Initialized?"Нужна первая проверка":Definition.LastError.Length>0?"Ошибка проверки":"Активна";
        public string LastSuccessLabel=>Definition.LastSuccessUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"Ещё не проверена";
        public string RawMessage=>Definition.LastError.Length>0?Definition.LastError:Definition.LastWarning;
        public string Message=>ErrorMessages.ForDisplay(RawMessage);
        public string DiagnosticDetails=>Message==RawMessage?RawMessage:Message+"\n\n"+
            ErrorMessages.Text("Исходная диагностика:","Original diagnostic:")+"\n"+RawMessage;
    }

    internal sealed class SubscriptionItemView(SubscriptionItem item)
    {
        public SubscriptionItem Item { get; }=item;
        public string Url=>Item.Url;
        public string Title=>Item.Title.Length>0?Item.Title:Item.Url;
        public string StateLabel=>Item.StateLabel;
        public string DiscoveredLabel=>Item.DiscoveredAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    }

    internal void RestoreQueueTitles(IEnumerable<DownloadItem> rows)
    {
        var pending=rows.Where(row=>row.SubscriptionId.Length>0 &&
            (string.IsNullOrWhiteSpace(row.Title) || string.Equals(row.Title,row.Url,StringComparison.Ordinal))).ToArray();
        if(pending.Length==0)return;
        try
        {
            if(!File.Exists(_store.Path))
            {
                _log("Названия подписок недоступны: файл подписок отсутствует. Очередь восстановлена без изменения ссылок.");
                return;
            }
            var wanted=pending.Select(row=>(row.SubscriptionId,row.Url)).ToHashSet();
            // Tuple string equality is ordinal. Read the optional store once,
            // before prefetch, without changing its contents or any routing.
            var titles=_store.Read().Items.Where(item=>wanted.Contains((item.SubscriptionId,item.Url)) &&
                !string.IsNullOrWhiteSpace(item.Title) && !string.Equals(item.Title.Trim(),item.Url,StringComparison.Ordinal))
                .ToDictionary(item=>(item.SubscriptionId,item.Url),item=>item.Title.Trim());
            foreach(var row in pending)
                if(titles.TryGetValue((row.SubscriptionId,row.Url),out string? title))row.Title=title;
        }
        catch(Exception error)
        {
            _log("Не удалось восстановить названия из подписок; сохранённая очередь продолжает работать: "+error.Message);
        }
    }

    internal void RecordSuccess(string url)
    {
        if (!File.Exists(_store.Path) || !_successes.Add(url)) return;
        try
        {
            _store.MarkSuccessful(url);
            Refresh();
        }
        catch(Exception error)
        {
            // Global successful history remains the source for recovery if a
            // subscription write or a UI continuation fails after completion.
            _log("Не удалось обновить подписки после успешной загрузки: "+error.Message);
        }
    }
}

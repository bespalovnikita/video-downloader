using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orientation=System.Windows.Controls.Orientation;
using Panel=System.Windows.Controls.Panel;

namespace VideoDownloader.Native;

internal sealed record ProfileBackupFilePickers(Func<Window,string?> Open,Func<Window,string?> Save)
{
    internal static ProfileBackupFilePickers Windows {get;}=new(owner=>
    {
        var picker=new Microsoft.Win32.OpenFileDialog{Title="Открыть резервную копию Windows",Filter="Резервная копия Video Downloader|*.vdbackup",CheckFileExists=true};
        return picker.ShowDialog(owner)==true?picker.FileName:null;
    },owner=>
    {
        var picker=new Microsoft.Win32.SaveFileDialog{Title="Создать резервную копию Windows",Filter="Резервная копия Video Downloader|*.vdbackup",
            DefaultExt=".vdbackup",AddExtension=true,FileName="video-downloader-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".vdbackup",OverwritePrompt=false};
        return picker.ShowDialog(owner)==true?picker.FileName:null;
    });
}

// This view owns only the reviewed archive/plan and cancellation. The main
// controller freezes profile writers before any background snapshot or apply.
internal sealed class ProfileBackupWindow:Window
{
    private readonly WindowsProfileBackup _service;
    private readonly Func<CancellationToken,Task<IDisposable>> _enter;
    private readonly Func<DownloadOptions> _options;
    private readonly Func<IReadOnlyList<DownloadItem>> _rows;
    private readonly Action<DownloadOptions> _applied;
    private readonly Action<Exception> _failed;
    private readonly ProfileBackupFilePickers _pickers;
    private readonly Button _create,_open,_restore,_confirm,_cancel;
    private readonly ComboBox _policy;
    private readonly TextBlock _summary,_changes,_status;
    private readonly StackPanel _confirmation;
    private ProfileBackupArchive? _archive;
    private ProfileRestorePlan? _plan;
    private CancellationTokenSource? _stop;
    private Task _operation=Task.CompletedTask;
    private bool _closed,_closeRequested,_recoveryRequired;
    internal bool IsBusy=>_stop is not null;
    internal Task ActiveOperation=>_operation;

    internal ProfileBackupWindow(Window owner,WindowsProfileBackup service,Func<CancellationToken,Task<IDisposable>> enter,
        Func<DownloadOptions> options,Func<IReadOnlyList<DownloadItem>> rows,Action<DownloadOptions> applied,
        Action<Exception> failed,ProfileBackupFilePickers? pickers=null)
    {
        _service=service;_enter=enter;_options=options;_rows=rows;_applied=applied;_failed=failed;
        _pickers=pickers??ProfileBackupFilePickers.Windows;
        Name="ProfileBackupWindow";Title="Резервная копия Windows";Owner=owner;Width=740;Height=650;MinWidth=540;MinHeight=450;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=owner.Background;Foreground=owner.Foreground;FontFamily=owner.FontFamily;
        Resources.MergedDictionaries.Add(owner.Resources);NameScope.SetNameScope(this,new NameScope());
        var body=new StackPanel{Margin=new Thickness(18)};Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        TextBlock Text(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)};
        T Named<T>(string name,T element) where T:FrameworkElement{element.Name=name;RegisterName(name,element);return element;}
        Button Action(string name,string label,Panel panel)
        {var button=Named(name,new Button{Content=label,Margin=new Thickness(0,3,8,3)});panel.Children.Add(button);return button;}
        body.Children.Add(new TextBlock{Text="Настройки, подписки и история успехов",FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        body.Children.Add(Text("Копия предназначена для Windows. В неё не входят очередь, файлы загрузок, права на их удаление, локальные пути, cookies и кеш. Адреса с приватными параметрами исключаются; перед восстановлением показываем состав и пропуски."));
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};body.Children.Add(actions);
        _create=Action("CreateProfileBackupButton","Создать копию…",actions);_open=Action("OpenProfileBackupButton","Открыть копию…",actions);
        body.Children.Add(Text("Для экспорта выберите новое имя: существующие архивы не заменяются."));
        _summary=Named("ProfileBackupSummary",Text("Откройте копию для просмотра или создайте новую."));body.Children.Add(_summary);
        body.Children.Add(Text("Подписки при восстановлении:"));
        _policy=Named("ProfileBackupPolicyCombo",new ComboBox{Margin=new Thickness(0,4,0,6)});
        _policy.Items.Add("Объединить с текущими подписками");_policy.Items.Add("Заменить только подписки");_policy.SelectedIndex=0;body.Children.Add(_policy);
        body.Children.Add(Text("Настройки применяются из копии. Текущие папки, cookies и разрешение следить за буфером остаются на этом компьютере. История всегда объединяется; прежние записи и права на локальные файлы сохраняются. Материалы не начинают скачиваться автоматически."));
        _changes=Named("ProfileBackupChanges",Text("Изменения ещё не подготовлены."));body.Children.Add(_changes);
        _restore=Action("RestoreProfileBackupButton","Перейти к подтверждению",body);
        _confirmation=Named("ProfileBackupConfirmation",new StackPanel{Visibility=Visibility.Collapsed,Margin=new Thickness(0,10,0,8)});body.Children.Add(_confirmation);
        _confirmation.Children.Add(Text("Подтвердите применение показанных изменений к текущему профилю. При замене подписок текущий список подписок будет заменён. Очередь и файлы загрузок сохранятся."));
        _confirm=Action("ConfirmProfileRestoreButton","Подтвердить восстановление",_confirmation);
        var back=Action("CancelProfileRestoreConfirmationButton","Вернуться к просмотру",_confirmation);
        _status=Named("ProfileBackupStatus",Text("Выберите действие."));body.Children.Add(_status);
        _cancel=Action("CancelProfileBackupOperationButton","Отменить операцию",body);_cancel.IsEnabled=false;
        var close=Action("CloseProfileBackupButton","Закрыть",body);
        _create.Click+=(_,_)=>Begin(CreateAsync);_open.Click+=(_,_)=>Begin(LoadAsync);
        _policy.SelectionChanged+=(_,_)=>{if(!IsBusy && _archive is not null)Begin(PrepareAsync);};
        _restore.Click+=(_,_)=>{if(!IsBusy && _plan is not null){_confirmation.Visibility=Visibility.Visible;_confirm.Focus();}};
        _confirm.Click+=(_,_)=>{if(!IsBusy && _plan is not null && _confirmation.Visibility==Visibility.Visible)Begin(RestoreAsync);};
        back.Click+=(_,_)=>_confirmation.Visibility=Visibility.Collapsed;
        _cancel.Click+=(_,_)=>RequestCancellation();close.Click+=(_,_)=>Close();
        Closing+=OnClosing;Closed+=(_,_)=>{_closed=true;_plan?.Dispose();_plan=null;_archive=null;};RefreshControls();
    }
    private void Begin(Func<CancellationToken,Task> action)
    {
        if(IsBusy || _closed || _recoveryRequired)return;
        // A file picker pumps the dispatcher before the first await. Publish
        // the join task first so owner-close cannot mistake that nested modal
        // operation for an already completed one.
        var joined=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);_operation=joined.Task;
        _=CompleteAsync();
        async Task CompleteAsync(){try{await RunAsync(action);}finally{joined.TrySetResult();}}
    }
    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        var stop=new CancellationTokenSource();_stop=stop;_confirmation.Visibility=Visibility.Collapsed;RefreshControls();
        try{await action(stop.Token);}
        catch(OperationCanceledException) when(stop.IsCancellationRequested){if(!_closed)_status.Text="Операция отменена. Проверьте состав копии перед новой попыткой.";}
        catch(Exception error)
        {
            _failed(error);_plan?.Dispose();_plan=null;
            _recoveryRequired=error is ProfileRestoreException {RecoveryRequired:true} || _service.RecoveryRequired;
            if(!_closed)_status.Text=_recoveryRequired
                ?"Работа с профилем заблокирована. Перезапустите приложение для завершения восстановления. "+SafeError(error)
                :"Операция не выполнена: "+SafeError(error);
        }
        finally
        {
            if(ReferenceEquals(_stop,stop))_stop=null;stop.Dispose();
            if(!_closed){RefreshControls();if(_closeRequested)Close();}
        }
    }
    private static string SafeError(Exception error)
    {string value=JournalSanitizer.Redact(ErrorMessages.ForException(error),multiline:false);return value.Length>800?value[..800]:value;}
    private void ShowArchive(ProfileBackupArchive archive)
    {
        var summary=archive.Summary;
        _summary.Text=$"Копия от {archive.CreatedAtUtc.LocalDateTime:g} · версия {archive.ApplicationVersion}\n"+
            $"Подписок: {summary.Sources}; материалов: {summary.Items}; записей истории: {summary.HistoryEntries}.\n"+
            $"Приватные адреса пропущены: подписки {summary.PrivateSourcesSkipped}, материалы {summary.PrivateItemsSkipped}, история {summary.PrivateHistorySkipped}, RSS {summary.PrivateFeedsSkipped}. Очищено текстовых полей: {summary.SanitizedFields}.";
    }
    private async Task CreateAsync(CancellationToken token)
    {
        string? destination=_pickers.Save(this);if(destination is null)return;token.ThrowIfCancellationRequested();
        using var busy=await _enter(token);_status.Text="Собираем и сохраняем копию…";
        var archive=await _service.CreateAsync(_options(),token);token.ThrowIfCancellationRequested();
        await _service.SaveAsync(archive,destination,token);
        _plan?.Dispose();_plan=null;_archive=null;ShowArchive(archive);
        _changes.Text="Экспорт завершён. Откройте копию, чтобы сравнить её с текущим профилем перед восстановлением.";
        _status.Text="Копия сохранена. Существующий файл не перезаписывается; для повторного экспорта выберите новое имя.";
    }
    private async Task LoadAsync(CancellationToken token)
    {
        string? source=_pickers.Open(this);if(source is null)return;token.ThrowIfCancellationRequested();
        using var busy=await _enter(token);_status.Text="Читаем и проверяем копию…";
        _plan?.Dispose();_plan=null;_archive=null;_summary.Text="Проверяем выбранную копию…";_changes.Text="Изменения ещё не подготовлены.";
        var archive=await _service.LoadAsync(source,token);token.ThrowIfCancellationRequested();
        _archive=archive;ShowArchive(archive);await PrepareCoreAsync(token);
    }
    private async Task PrepareAsync(CancellationToken token)
    {using var busy=await _enter(token);await PrepareCoreAsync(token);}
    private async Task PrepareCoreAsync(CancellationToken token)
    {
        _plan?.Dispose();_plan=null;if(_archive is null)return;
        _changes.Text="Изменения ещё сравниваются с текущим профилем.";
        _status.Text="Сравниваем с текущим профилем…";
        var policy=_policy.SelectedIndex==1?BackupSubscriptionsPolicy.Replace:BackupSubscriptionsPolicy.Merge;
        var plan=await _service.PrepareRestoreAsync(_archive,_options(),_rows(),
            policy,token);
        if(token.IsCancellationRequested){plan.Dispose();token.ThrowIfCancellationRequested();}
        if(policy!=(_policy.SelectedIndex==1?BackupSubscriptionsPolicy.Replace:BackupSubscriptionsPolicy.Merge))
        {plan.Dispose();throw new InvalidOperationException("Режим подписок изменился. Откройте копию повторно для нового сравнения.");}
        _plan=plan;var summary=plan.Summary;
        _changes.Text=$"Будет добавлено: подписок {summary.AddedSources}, материалов {summary.AddedItems}, записей истории {summary.AddedHistoryEntries}.\n"+
            (summary.Policy==BackupSubscriptionsPolicy.Replace?$"Заменяем текущих подписок: {summary.ReplacedSources}.":"Текущие подписки сохраняются и объединяются.");
        _status.Text="Копия проверена. Просмотрите состав и режим, затем перейдите к подтверждению.";
    }
    private async Task RestoreAsync(CancellationToken token)
    {
        var plan=_plan;if(plan is null)return;
        if(plan.Summary.Policy!=(_policy.SelectedIndex==1?BackupSubscriptionsPolicy.Replace:BackupSubscriptionsPolicy.Merge))
            throw new InvalidOperationException("Режим подписок изменился. Откройте копию повторно для нового сравнения.");
        using var busy=await _enter(token);_status.Text="Восстанавливаем профиль…";
        await _service.RestoreAsync(plan,token);_applied(plan.Settings);
        _plan=null;plan.Dispose();_status.Text="Профиль восстановлен. Очередь сохранена; загрузки и проверки сами не запускаются.";
        _changes.Text="Восстановление завершено. Для нового сравнения откройте копию повторно.";
    }
    private void RefreshControls()
    {
        _create.IsEnabled=_open.IsEnabled=_policy.IsEnabled=!IsBusy && !_recoveryRequired;
        _restore.IsEnabled=_confirm.IsEnabled=!IsBusy && !_recoveryRequired && _plan is not null;_cancel.IsEnabled=IsBusy;
    }
    internal void RequestCancellation(){try{_stop?.Cancel();}catch(ObjectDisposedException){ }}
    internal async Task CancelAndWaitAsync(){RequestCancellation();await _operation;}
    private void OnClosing(object? sender,CancelEventArgs e)
    {if(IsBusy){e.Cancel=true;_closeRequested=true;RequestCancellation();}}
}

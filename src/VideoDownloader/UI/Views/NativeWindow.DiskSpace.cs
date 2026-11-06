using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private DiskSpaceService? _diskSpaceService;
    private readonly CancellationTokenSource _diskSpaceStop=new();
    private readonly HashSet<DownloadItem> _diskObservedRows=new();
    private readonly DiskSpacePressure _diskPressure=new();
    private CancellationTokenSource? _diskRefresh;
    private DownloadCoordinator? _diskSession;
    private DiskSpaceAssessment? _diskAssessment;
    private DiskSpaceAssessment? _diskApprovedAssessment;
    private DateTime _diskCheckedAt=DateTime.MinValue,_diskChangedAt=DateTime.MinValue;
    private bool _diskDirty=true,_diskChecking,_diskClosed;
    private long _diskGeneration;
    private string _diskAdmissionMessage="";
    private sealed record DiskQueueState(DownloadItem Row,string Url,DownloadSelection? Selection,string Status,
        string SubscriptionName,bool PendingRemoval,string CleanupError,string StageDir,string RunDir);

    private void InitializeDiskSpace(IDiskSpaceProvider? provider)
    {
        _diskSpaceService=new(provider??new LocalDiskSpaceProvider());
        B("OutputBox").TextChanged+=DiskDestinationChanged;
        K("ArchiveCheck").Checked+=DiskDestinationChanged;K("ArchiveCheck").Unchecked+=DiskDestinationChanged;
        SessionState.Queue.CollectionChanged+=DiskQueueChanged;
        ObserveDiskRows();
        T("DiskSpaceSummaryText").Text=ErrorMessages.Text("Проверяем свободное место…","Checking free disk space…");
    }
    private void DiskDestinationChanged(object sender,RoutedEventArgs args)=>InvalidateDiskSpace();
    private void DiskQueueChanged(object? sender,NotifyCollectionChangedEventArgs args)
    {
        if(args.Action==NotifyCollectionChangedAction.Reset)ObserveDiskRows();
        else if(args.Action!=NotifyCollectionChangedAction.Move)
        {
            if(args.OldItems is not null)
                foreach(DownloadItem row in args.OldItems)
                    if(_diskObservedRows.Remove(row))row.PropertyChanged-=DiskRowChanged;
            if(args.NewItems is not null)
                foreach(DownloadItem row in args.NewItems)
                    if(_diskObservedRows.Add(row))row.PropertyChanged+=DiskRowChanged;
        }
        InvalidateDiskSpace();
    }
    private void ObserveDiskRows()
    {
        var current=new HashSet<DownloadItem>(SessionState.Queue);
        foreach(var previous in _diskObservedRows.Where(row=>!current.Contains(row)).ToArray())
        {previous.PropertyChanged-=DiskRowChanged;_diskObservedRows.Remove(previous);}
        foreach(var row in current)
            if(_diskObservedRows.Add(row))row.PropertyChanged+=DiskRowChanged;
    }
    private void DiskRowChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName is nameof(DownloadItem.Selection) or nameof(DownloadItem.Status) or nameof(DownloadItem.PendingRemoval))
            InvalidateDiskSpace();
    }
    private void InvalidateDiskSpace()
    {
        if(_diskClosed)return;
        _diskDirty=true;
        // A running session has pinned volumes. Progress/status changes or edits
        // for the next Start must not cancel its measurement or increase polling.
        if(SessionState.Coordinator?.IsRunning==true)return;
        _diskChangedAt=DateTime.UtcNow;
        _diskGeneration++;_diskRefresh?.Cancel();
    }
    private void PollDiskSpace()
    {
        if(_diskClosed || _diskSpaceService is null || _closing || _disposed || _diskChecking)return;
        var session=SessionState.Coordinator;
        if(!ReferenceEquals(_diskSession,session))
        {
            _diskSession=session;_diskPressure.Reset();
            if(session is not null && _diskApprovedAssessment is {} approved)
                foreach(var volume in approved.Volumes)_diskPressure.Observe(volume.Space);
            _diskApprovedAssessment=null;
            _diskDirty=true;_diskCheckedAt=DateTime.MinValue;
        }
        if(_diskRefresh is not null)return;
        var now=DateTime.UtcNow;
        if(now-_diskChangedAt<TimeSpan.FromMilliseconds(350))return;
        if(session?.IsRunning==true && now-_diskCheckedAt<TimeSpan.FromSeconds(5))return;
        if(!_diskDirty && now-_diskCheckedAt<TimeSpan.FromSeconds(5))return;
        _=RefreshDiskSpaceAsync();
    }
    internal async Task RefreshDiskSpaceAsync()
    {
        if(_diskClosed || _diskSpaceService is null || _closing || _disposed || _diskChecking)return;
        _diskRefresh?.Cancel();
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(_diskSpaceStop.Token);
        _diskRefresh=cancel;
        long generation=++_diskGeneration;
        var session=SessionState.Coordinator;
        var options=session?.IsRunning==true?SessionState.Options:ReadOptions();
        DiskSpaceCandidate[] candidates;
        try {candidates=CaptureDiskCandidates(options,session?.IsRunning==true);}
        catch(Exception error) when(error is IOException or ArgumentException or NotSupportedException)
        {
            T("DiskSpaceSummaryText").Text=ErrorMessages.Text("Не удалось определить папку для проверки места.","The destination could not be resolved for a free-space check.");
            _diskRefresh=null;cancel.Dispose();_diskDirty=false;_diskCheckedAt=DateTime.UtcNow;return;
        }
        _diskDirty=false;
        try
        {
            var assessment=await _diskSpaceService.AssessAsync(candidates,cancel.Token);
            if(_diskClosed || _disposed || _closing || cancel.IsCancellationRequested || generation!=_diskGeneration ||
                !ReferenceEquals(session,SessionState.Coordinator))return;
            _diskAssessment=assessment;_diskCheckedAt=DateTime.UtcNow;
            PresentDiskSpace(assessment,session?.IsRunning==true);
        }
        catch(OperationCanceledException) when(cancel.IsCancellationRequested) { }
        catch(Exception error)
        {
            if(!_diskClosed && !_disposed && generation==_diskGeneration)
            {
                _diskCheckedAt=DateTime.UtcNow;
                T("DiskSpaceSummaryText").Text=ErrorMessages.Text("Свободное место не удалось проверить.","Free disk space could not be checked.");
                Log("Disk space check failed: "+JournalSanitizer.Redact(error.Message));
            }
        }
        finally
        {
            if(ReferenceEquals(_diskRefresh,cancel))_diskRefresh=null;
            cancel.Dispose();
        }
    }
    private DiskSpaceCandidate[] CaptureDiskCandidates(DownloadOptions options,bool running)
    {
        string output=options.OutputDirectory;
        string temporary=running?SessionState.Coordinator!.StagingDirectory:Path.GetTempPath();
        var rows=(running?CurrentSessionRows():SessionState.Queue.ToArray())
            .Where(row=>running?!row.AttemptFinished:row.Status!="Completed" && row.CanResume).ToArray();
        var jobs=new List<DiskSpaceCandidate>(Math.Max(2,rows.Length*2));
        foreach(var row in rows)
        {
            string destination=running && row.OutputRoot.Length>0?row.OutputRoot:
                SubscriptionNames.ResolveOutputDirectory(output,row.SubscriptionName);
            jobs.Add(new(row.Url,destination,row.Selection,DiskRanges(row.Selection)));
            jobs.Add(new(row.Url,temporary,row.Selection,DiskRanges(row.Selection),ReservedCopies:2));
        }
        if(jobs.Count==0)
        {
            jobs.Add(new("",output,null,ProbeOnly:true));
            jobs.Add(new("",temporary,null,ProbeOnly:true));
        }
        return jobs.ToArray();
    }
    // The ranges are passed independently to the estimator so a batch of
    // fragments reserves their sum rather than a full stream for every range.
    private static IReadOnlyList<DownloadSection> DiskRanges(DownloadSelection? selection)=>
        selection?.EffectiveSections??Array.Empty<DownloadSection>();

    private DiskQueueState[] FreezeDiskQueue()=>SessionState.Queue.Select(row=>new DiskQueueState(row,row.Url,row.Selection,
        row.Status,row.SubscriptionName,row.PendingRemoval,row.CleanupError,row.StageDir,row.RunDir)).ToArray();
    private bool DiskAdmissionUnchanged(DiskQueueState[] queue,string options,string temporary,bool readOnly)
    {
        if(_diskClosed || _disposed || _closing || _appUpdateHandoff || SessionState.Coordinator?.IsRunning==true ||
            _checkpointReadOnly!=readOnly || !string.Equals(Path.GetTempPath(),temporary,StringComparison.Ordinal))return false;
        return queue.SequenceEqual(FreezeDiskQueue()) && options==JsonSerializer.Serialize(ReadOptions(),AppPersistence.Json);
    }
    private bool DiskAdmissionChanged()
    {
        _diskAdmissionMessage=ErrorMessages.Text("Очередь или настройки изменились во время проверки места. Повторите Start.",
            "The queue or settings changed while disk space was being checked. Press Start again.");
        T("DiskSpaceWarningText").Text=_diskAdmissionMessage;T("DiskSpaceWarningText").Visibility=Visibility.Visible;
        Log("Start postponed: queue or options changed during the disk-space check.");
        return false;
    }
    private async Task<bool> ConfirmDiskSpaceBeforeStartAsync(IReadOnlyList<DownloadItem> approved,
        DownloadOptions options,IReadOnlyDictionary<DownloadItem,string> outputRoots)
    {
        if(_diskSpaceService is null)return true;
        _diskAdmissionMessage="";
        _diskRefresh?.Cancel();_diskGeneration++;
        _diskChecking=true;
        T("DiskSpaceSummaryText").Text=ErrorMessages.Text("Проверяем место перед запуском…","Checking disk space before starting…");
        F<Button>("StartButton").IsEnabled=false;
        var queue=FreezeDiskQueue();
        string settings=JsonSerializer.Serialize(options,AppPersistence.Json);
        string temporary=Path.GetTempPath();bool readOnly=_checkpointReadOnly;
        var jobs=new List<DiskSpaceCandidate>(approved.Count*2);
        foreach(var row in approved)
        {
            jobs.Add(new(row.Url,outputRoots[row],row.Selection,DiskRanges(row.Selection)));
            jobs.Add(new(row.Url,temporary,row.Selection,DiskRanges(row.Selection),ReservedCopies:2));
        }
        try
        {
            var assessment=await _diskSpaceService.AssessAsync(jobs,_diskSpaceStop.Token);
            if(!DiskAdmissionUnchanged(queue,settings,temporary,readOnly))return DiskAdmissionChanged();
            _diskAssessment=assessment;_diskCheckedAt=DateTime.UtcNow;_diskDirty=false;
            PresentDiskSpace(assessment,false);
            if(assessment.ShouldWarn)
            {
                var dialog=new DiskSpaceWarningDialog(_window,DiskSpaceDescription(assessment));
                if(dialog.ShowDialog()!=true)return false;
            }
            if(!DiskAdmissionUnchanged(queue,settings,temporary,readOnly))return DiskAdmissionChanged();
            _diskApprovedAssessment=assessment.ShouldWarn?assessment:null;
            return true;
        }
        catch(OperationCanceledException) when(_diskSpaceStop.IsCancellationRequested){return false;}
        finally {_diskChecking=false;}
    }
    private void PresentDiskSpace(DiskSpaceAssessment assessment,bool running)
    {
        T("DiskSpaceSummaryText").Text=DiskSpaceDescription(assessment);
        T("DiskSpaceSummaryText").Foreground=assessment.ShouldWarn?Brushes.Orange:Brushes.LightGray;
        var low=running?assessment.Volumes.Where(volume=>volume.Space.AvailableBytes is {} free &&
            free<DiskSpaceAssessment.LowSpaceBytes).ToArray():Array.Empty<DiskSpaceVolume>();
        string warning=_diskAdmissionMessage;
        if(low.Length>0)
        {
            warning=ErrorMessages.Text("На диске осталось менее 1 ГиБ. При необходимости приостановите или остановите загрузку.",
                "Less than 1 GiB remains. Pause or stop the download if needed.");
            foreach(var volume in low)
                if(_diskPressure.Observe(volume.Space))
                {
                    string message=ErrorMessages.Text("Мало места на диске ","Low disk space on ")+volume.Space.Volume+
                        ": "+DownloadFormat.Bytes(volume.Space.AvailableBytes!.Value);
                    _journal.Write(new JournalRecord(JournalLevel.Warning,"disk",message));
                    _lifecycle.NotifyWarning(ErrorMessages.Text("Мало места на диске","Low disk space"),message);
                }
        }
        if(running)foreach(var volume in assessment.Volumes)_diskPressure.Observe(volume.Space);
        T("DiskSpaceWarningText").Text=warning;
        T("DiskSpaceWarningText").Visibility=warning.Length>0?Visibility.Visible:Visibility.Collapsed;
    }
    private static string DiskSpaceDescription(DiskSpaceAssessment assessment)
    {
        var text=new StringBuilder();
        foreach(var volume in assessment.Volumes)
        {
            if(text.Length>0)text.AppendLine();
            string place=volume.Space.Volume.Length>0?volume.Space.Volume:volume.Space.Destination;
            if(place.Length>180)place=place[..180]+"…";
            text.Append(place).Append(": ");
            if(volume.Space.Availability==DiskSpaceAvailability.Available && volume.Space.AvailableBytes is {} available)
                text.Append(ErrorMessages.Text("свободно ","free ")).Append(DownloadFormat.Bytes(available));
            else text.Append(volume.Space.Detail.Length>0?volume.Space.Detail:
                ErrorMessages.Text("свободное место неизвестно","free space is unknown"));
            var estimate=volume.Estimate;
            if(volume.ItemCount==0)continue;
            if(estimate.HasKnownSize)
                text.Append(ErrorMessages.Text(" · известная часть потоков ≈ "," · known streams ≈ ")).Append(DownloadFormat.Bytes(estimate.KnownPayloadBytes))
                    .Append(ErrorMessages.Text(" · резерв на этом диске ≈ "," · allowance on this disk ≈ ")).Append(DownloadFormat.Bytes(estimate.RequiredBytes));
            if(estimate.UnknownItems>0)
                text.Append(ErrorMessages.Text(" · размер неизвестен у "," · size unknown for ")).Append(estimate.UnknownItems).Append(" URL");
        }
        return text.ToString();
    }
    private void DisposeDiskSpace()
    {
        if(_diskClosed)return;
        _diskClosed=true;_diskGeneration++;
        _diskSpaceStop.Cancel();_diskRefresh?.Cancel();
        B("OutputBox").TextChanged-=DiskDestinationChanged;
        K("ArchiveCheck").Checked-=DiskDestinationChanged;K("ArchiveCheck").Unchecked-=DiskDestinationChanged;
        SessionState.Queue.CollectionChanged-=DiskQueueChanged;
        foreach(var row in _diskObservedRows)row.PropertyChanged-=DiskRowChanged;
        _diskObservedRows.Clear();
        // In-flight owners dispose their linked tokens after their operations
        // have joined. The lifetime source has no wait handle or native owner.
    }
}

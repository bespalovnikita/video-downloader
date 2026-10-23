#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.Core.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $root 'lib/VideoDownloader.Cancellation.psm1') -Force
Import-Module (Join-Path $root 'lib/VideoDownloader.LiveQueue.psm1') -Force
Import-Module (Join-Path $root 'lib/VideoDownloader.Session.psm1') -Force
Import-Module (Join-Path $root 'lib/VideoDownloader.Profiles.psm1') -Force

function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
Add-Type @'
using System;
namespace VdGuiReview {
 public enum MessageBoxButton { OK, YesNo }
 public enum MessageBoxImage { Warning }
 public enum MessageBoxResult { Yes }
 public static class MessageBox {
  public static Action BeforeAnswer;
  public static int WarningCount;
  public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon) {
   if (buttons == MessageBoxButton.OK) WarningCount++;
   var callback=BeforeAnswer;
   BeforeAnswer=null;
   if (callback != null) callback();
   return MessageBoxResult.Yes;
  }
  public static MessageBoxResult Show(string message, string title) { return MessageBoxResult.Yes; }
 }
}
'@

# Execute production function bodies, replacing only the platform dialog boundary.
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$tokens,[ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) 'GUI syntax failed'
foreach ($name in @(
 'Format-Bytes','Is-ValidUrl','Add-Urls','Add-FileToQueue','Find-QueueItem','Find-Download','Set-Download',
 'Set-QueueState','Set-QueueFields','Set-RowProgress','Update-TransferBytes','Tail-Events','Reindex-Queue','Register-SubscriptionSuccess',
 'Get-QueueEngineContext','Test-GuiEngineAlive','Show-QueueCleanupFailure','Remove-QueueRowFiles','Remove-QueueRow',
 'Complete-EngineQueue','Clear-QueueEntries','Remove-SelectedQueueEntries','Sync-RecoveredEngine','Initialize-RecoveredEngine',
 'Clean-Temp','Get-SessionSnapshot','Apply-Profile','Add-SubscriptionUrls'
)) {
 $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
 Assert ($null -ne $node) "Missing production function $name"
 . ([scriptblock]::Create($node.Extent.Text.Replace('[System.Windows.MessageBox','[VdGuiReview.MessageBox')))
}
function Save-SessionCheckpoint { $script:saveCount++ }
function Update-StartState {}
function Update-LiveStats {}
function Queue-PreviewPrefetch {}
function Log-Line {}
function Import-CompletedUrlsToHistory {}
function Get-SessionSettings { @{Output=$OutputBox.Text} }
function Confirm-DuplicateDownloads($Urls) { @{Cancelled=$false;Selected=@($Urls)} }
function Test-VdSessionEngineAlive($Engine) { return [bool]$Engine.Alive }
function Remove-VdOwnedFiles($OutputRoot,$Paths) {
 if ($script:failDelete) { throw 'Simulated locked file' }
 VideoDownloader.Cancellation\Remove-VdOwnedFiles -OutputRoot $OutputRoot -Paths $Paths
}

$testRoot = Join-Path $root ('work/gui-regression-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
function Reset-State {
 $script:runDir=Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
 $script:outputRoot=Join-Path $script:runDir 'output'
 [void][IO.Directory]::CreateDirectory($script:outputRoot)
 $script:runEvents=Join-Path $script:runDir 'events.jsonl'
 $script:runLog=Join-Path $script:runDir 'run.log'
 $script:liveQueuePath=Join-Path $script:runDir 'incoming.txt'
 $script:cancelDir=Join-Path $script:runDir 'cancel'
 [void][IO.Directory]::CreateDirectory($script:cancelDir)
 [IO.File]::WriteAllText($script:runEvents,'')
 [IO.File]::WriteAllText($script:liveQueuePath,'')
 $script:eventCursor=@{Offset=0L}; $script:recoveredEventCursor=@{Offset=0L}
 $script:queue=[Collections.ObjectModel.ObservableCollection[object]]::new()
 $script:downloads=[Collections.ObjectModel.ObservableCollection[object]]::new()
 $script:failedUrls=[Collections.Generic.List[string]]::new()
 $script:removedUrls=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $script:runUrls=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $script:transferredByUrl=[Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
 $script:sessionTotal=1; $script:sessionSuccess=0; $script:sessionFailed=0; $script:sessionBytes=0
 $script:speeds=@{}; $script:lastFiles=@{}
 $script:proc=[pscustomobject]@{HasExited=$false}
 $script:activeEngine=[ordered]@{Pid=123;StartedUtc='2000-01-01T00:00:00Z';EventsPath=$script:runEvents;RunDir=$script:runDir;CancelDir=$script:cancelDir;OutputRoot=$script:outputRoot;Alive=$true}
 $script:recoveredEngine=$null
 $script:OutputBox=[pscustomobject]@{Text=$script:outputRoot}
 $items=[pscustomobject]@{}; $items|Add-Member ScriptMethod Refresh {}
 $script:QueueGrid=[pscustomobject]@{Items=$items}; $script:DownloadGrid=[pscustomobject]@{Items=$items}
 $script:QueueCount=[pscustomobject]@{Text=''}; $script:OverallProgress=[pscustomobject]@{Value=0}
 $script:ClipboardHint=[pscustomobject]@{Text=''}
 $script:checkpointReadOnly=$false; $script:lastSessionError=''
 $script:saveCount=0; $script:failDelete=$false
 [VdGuiReview.MessageBox]::WarningCount=0; [VdGuiReview.MessageBox]::BeforeAnswer=$null
}
function Add-TestRow([string]$Url='https://example.com/video',[string]$Status='Running') {
 $row=[pscustomobject]@{
  Order=1;Url=$Url;State=$Status;Status=$Status;Path='';OwnedPaths=@()
  OutputRoot=$script:outputRoot;RunDir=$script:runDir;StageDir='';CleanupPaths=@();CleanupError=''
  PendingRemoval=$false;AttemptFinished=($Status -in @('Completed','Failed'));DeferredToNextStart=$false;AttachedToRecovered=$false
  Progress=0;Speed='';Downloaded='';ETA=''
 }
 $script:queue.Add($row)
 [void]$script:runUrls.Add($Url)
 return $row
}
function Append-Result([object]$Row,[hashtable]$Changes=@{}) {
 $event=@{Kind='Result';Slot=1;Url=$Row.Url;Success=$false;Cancelled=$true;OutputRoot=$Row.OutputRoot;OwnedPaths=@();CleanupPaths=@();CleanupError='';StageDir='';TransferredBytes=0;Path=''}
 foreach ($key in $Changes.Keys) { $event[$key]=$Changes[$key] }
 [IO.File]::AppendAllText($script:runEvents,($event|ConvertTo-Json -Compress)+"`n")
}
try {
 Reset-State
 $runningRow=Add-TestRow
 $ingressBefore=[IO.File]::ReadAllText($script:liveQueuePath)
 $first='https://example.com/subscription/B';$second='https://example.com/subscription/b'
 $accepted=@(Add-SubscriptionUrls @($first,$second,$first))
 Assert ($accepted.Count -eq 2 -and $accepted[0] -ceq $first -and $accepted[1] -ceq $second) 'Subscription queue acceptance must preserve exact URLs and input order'
 Assert ($script:queue.Count -eq 3 -and $script:queue[1].DeferredToNextStart -and $script:queue[2].DeferredToNextStart) 'Subscription jobs must wait for explicit Start even during an active session'
 Assert ([IO.File]::ReadAllText($script:liveQueuePath) -ceq $ingressBefore -and $script:sessionTotal -eq 1 -and $script:runUrls.Count -eq 1) 'Subscription import must not enter live ingress, active URL accounting or session totals'
 $acceptedAgain=@(Add-SubscriptionUrls @($first,$second))
 Assert ($acceptedAgain.Count -eq 2 -and $script:queue.Count -eq 3) 'Retrying queue acceptance after a lost store acknowledgement must be idempotent'
 $script:checkpointReadOnly=$true
 $refused=$false;try { Add-SubscriptionUrls @('https://example.com/no-save') | Out-Null } catch {$refused=$true}
 Assert ($refused -and $script:queue.Count -eq 3) 'Read-only checkpoint must block durable subscription queue acceptance'

 Reset-State
 $row=Add-TestRow
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 1 -and $row.PendingRemoval) 'Active Remove must retain the row until cleanup confirmation'
 Assert (Test-VdCancellation -Directory $script:cancelDir -Url $row.Url) 'Active Remove must publish its cancel marker'
 Append-Result $row
 Tail-Events
 Assert ($script:queue.Count -eq 0 -and $script:sessionTotal -eq 0) 'Confirmed cancellation must finish removal'

 Reset-State
 $row=Add-TestRow -Status Interrupted
 $script:recoveredEngine=$script:activeEngine; $script:activeEngine=$null; $script:proc=$null
 $row.AttachedToRecovered=$true
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 1 -and $row.PendingRemoval) 'Recovered live worker must retain its cancelling row'
 Assert (Test-VdCancellation -Directory $script:cancelDir -Url $row.Url) 'Recovered Remove must reach the original cancel directory'
 Append-Result $row
 Assert (-not (Sync-RecoveredEngine)) 'Other work in the recovered engine should remain active'
 Assert ($script:queue.Count -eq 0) 'Recovered cancellation result must be consumed before the entire engine exits'

 foreach ($duringDialog in @($false,$true)) {
  Reset-State
  $row=Add-TestRow
  $file=Join-Path $script:outputRoot 'completed.mp4'
  [IO.File]::WriteAllText($file,'downloaded')
  $script:raceRow=$row; $script:raceFile=$file
  if ($duringDialog) {
   [VdGuiReview.MessageBox]::BeforeAnswer=[Action]{
    Append-Result $script:raceRow @{Success=$true;Cancelled=$false;Path=$script:raceFile;OwnedPaths=@($script:raceFile)}
    $script:proc.HasExited=$true
   }
  } else {
   Append-Result $row @{Success=$true;Cancelled=$false;Path=$file;OwnedPaths=@($file)}
   $script:proc.HasExited=$true
  }
  Remove-SelectedQueueEntries @($row)
  Tail-Events
  Assert ($script:queue.Count -eq 0 -and $script:downloads.Count -eq 0 -and -not [IO.File]::Exists($file)) "Final Result must be reconciled before Remove (duringDialog=$duringDialog)"
 }

 Reset-State
 $row=Add-TestRow
 Remove-SelectedQueueEntries @($row)
 $file=Join-Path $script:outputRoot 'locked.mp4'; [IO.File]::WriteAllText($file,'published')
 $stage=Join-Path $script:runDir ('download-1-'+[guid]::NewGuid().ToString('N'))
 [void][IO.Directory]::CreateDirectory($stage); [IO.File]::WriteAllText((Join-Path $stage 'video.part'),'partial')
 Append-Result $row @{CleanupError='Access denied';CleanupPaths=@($file);OwnedPaths=@($file);StageDir=$stage}
 Tail-Events
 Assert ($script:queue.Count -eq 1 -and $row.PendingRemoval -and $row.CleanupError) 'Cleanup failure must keep a retryable row'
 Assert ([VdGuiReview.MessageBox]::WarningCount -gt 0) 'Cleanup failure must be visible'
 $snapshot=Get-SessionSnapshot
 Assert ($snapshot.Queue[0].RunDir -eq $script:runDir -and $snapshot.Queue[0].StageDir -eq $stage -and $snapshot.Queue[0].CleanupPaths[0] -eq $file) 'Cleanup ownership must be checkpointed'
 Clear-QueueEntries
 Assert ($script:queue.Count -eq 1) 'Clear Queue must retain cleanup failures for retry'
 $retainedRun=$script:runDir
 Clean-Temp
 Assert ([IO.Directory]::Exists($stage)) 'Starting another session must not delete retained cleanup files'
 $script:OutputBox.Text=Join-Path $testRoot 'different-output'
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 0 -and -not [IO.File]::Exists($file) -and -not [IO.Directory]::Exists($stage)) 'Retry Remove must delete published/staging files using their original roots'

 Reset-State
 $row=Add-TestRow -Status Completed
 $script:proc.HasExited=$true
 $file=Join-Path $script:outputRoot 'legacy.mp4'; [IO.File]::WriteAllText($file,'legacy file')
 $row.Path=$file
 $script:sessionSuccess=1
 $script:failDelete=$true
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 1 -and $row.Status -eq 'Interrupted' -and $row.CleanupPaths.Count -eq 0 -and [IO.File]::Exists($file)) 'Legacy Path alone must never authorize deletion'
 $script:failDelete=$false
 $OutputBox.Text=Join-Path $testRoot 'new-output'
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 1 -and [IO.File]::Exists($file)) 'Retry must still refuse unowned legacy files after status changes'
 [IO.File]::Delete($file)
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 0) 'A legacy row can be removed after its file was deleted manually'
 Assert ($script:sessionSuccess -eq 0 -and $script:sessionTotal -eq 0) 'Removal retry must remove the original terminal outcome from session counters'

 Reset-State
 $row=Add-TestRow
 $url=$row.Url
 Remove-SelectedQueueEntries @($row)
 Append-Result $row
 Tail-Events
 Add-Urls @($url)
 Assert ($script:queue.Count -eq 1 -and $script:queue[0].Status -eq 'Queued' -and $script:queue[0].DeferredToNextStart) 'Removed URL must be queued for the next attempt'
 Assert (@(Read-VdLiveQueueUrls -Path $script:liveQueuePath).Count -eq 0 -and $script:sessionTotal -eq 0 -and -not $script:runUrls.Contains($url)) 'Deferred re-add must not enter the active ingress or totals'
 $progress=@{Kind='Event';EventType='Progress';Slot=1;Url=$url;Percent=99;Speed='1MiB/s';DownloadedBytes=99;TotalBytes=100;TransferredBytes=99}|ConvertTo-Json -Compress
 [IO.File]::AppendAllText($script:runEvents,$progress+"`n")
 Tail-Events
 Assert ($script:queue[0].Status -eq 'Queued' -and $script:queue[0].Progress -eq 0) 'Old events must not update the deferred row'
 Assert (@(Get-VdResumeUrls -Queue @($script:queue)).Count -eq 1) 'Deferred row must remain available to the next Start'

 Reset-State
 $script:proc=$null; $script:activeEngine=$null
 $literalFile=Join-Path $script:runDir 'urls[1].txt'
 [IO.File]::WriteAllText($literalFile,'https://example.com/literal')
 [IO.File]::WriteAllText((Join-Path $script:runDir 'urls1.txt'),'https://example.com/wrong')
 Add-FileToQueue $literalFile
 Assert ($script:queue.Count -eq 1 -and $script:queue[0].Url -ceq 'https://example.com/literal') 'Bracketed list filename must be read literally'

 Reset-State
 $context=$script:activeEngine
 $context.Remove('RunDir'); $context.Remove('CancelDir')
 $script:activeEngine=$null; $script:proc=$null
 Initialize-RecoveredEngine $context
 $row=Add-TestRow -Status Interrupted
 $row.RunDir=''; $row.AttachedToRecovered=$true
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 1 -and [VdGuiReview.MessageBox]::WarningCount -gt 0) 'An unvalidated legacy engine must not lose its active row without cancellation'

 foreach ($control in @('QualityCombo','CodecCombo','ContainerCombo')) { Set-Variable -Name $control -Value ([pscustomobject]@{SelectedItem=''}) -Scope Script }
 foreach ($control in @('AudioOnlyCheck','ArchiveCheck','WriteSubsCheck','AutoSubsCheck','EmbedSubsCheck','EmbedThumbnailCheck','EmbedMetadataCheck','EmbedChaptersCheck')) { Set-Variable -Name $control -Value ([pscustomobject]@{IsChecked=$false}) -Scope Script }
 function Profile-State { @($QualityCombo.SelectedItem,$CodecCombo.SelectedItem,$ContainerCombo.SelectedItem,$AudioOnlyCheck.IsChecked,$WriteSubsCheck.IsChecked,$AutoSubsCheck.IsChecked,$EmbedSubsCheck.IsChecked,$EmbedThumbnailCheck.IsChecked,$EmbedMetadataCheck.IsChecked,$EmbedChaptersCheck.IsChecked,$ArchiveCheck.IsChecked) | ConvertTo-Json -Compress }
 $profiles=@('Universal','4K archive','MP4 compatibility','Music MP3','Subtitles archive')
 $expected=@{}
 foreach ($name in $profiles) { Apply-Profile Universal; Apply-Profile $name; $expected[$name]=Profile-State }
 foreach ($previous in $profiles) { foreach ($name in $profiles) {
  Apply-Profile $previous; Apply-Profile $name
  Assert ((Profile-State) -eq $expected[$name]) "Profile $name depends on previous profile $previous"
 } }
 $captions=@(Get-VdProfileCaptions)
 foreach ($previous in $captions) { foreach ($caption in $captions) {
  Apply-Profile $previous; Apply-Profile $caption
  $id=Get-VdProfileId $caption
  Assert ((Profile-State) -eq $expected[$id]) "Russian profile $caption depends on previous profile $previous"
 } }
 $beforeUnknown=Profile-State
 Apply-Profile 'Unknown profile'
 Assert ((Profile-State) -eq $beforeUnknown) 'An unknown profile must not reset the selected settings'
 Apply-Profile 'Subtitles archive'; Apply-Profile Universal
 Assert (-not $AutoSubsCheck.IsChecked -and -not $WriteSubsCheck.IsChecked -and -not $EmbedSubsCheck.IsChecked) 'Universal must turn off subtitle options inherited from a subtitle profile'
 Apply-Profile 'Music MP3'; Apply-Profile '4K archive'
 Assert (-not $AudioOnlyCheck.IsChecked -and $ArchiveCheck.IsChecked -and $QualityCombo.SelectedItem -eq '2160') '4K profile must select video and dated archive after MP3'
 Apply-Profile Universal
 Assert (-not $ArchiveCheck.IsChecked) 'Universal must reset dated archive mode'

 # Exercise a retry whose process fails to launch. Replace the process boundary
 # with a deterministic failure, and place its temporary run under this harness.
 $startNode=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Start-Download'},$true)
 $startCode=$startNode.Extent.Text.Replace('[System.Windows.MessageBox','[VdGuiReview.MessageBox').
  Replace('[Diagnostics.Process]::Start($psi)',"(& { throw 'Simulated launch failure' })").
  Replace('[IO.Path]::GetTempPath()','$testRoot')
 . ([scriptblock]::Create($startCode))
 function Set-Running {}
 Reset-State
 $script:proc=$null; $script:activeEngine=$null
 $row=Add-TestRow -Status Completed
 $oldRoot=Join-Path $testRoot 'previous-output'; [void][IO.Directory]::CreateDirectory($oldRoot)
 $oldFile=Join-Path $oldRoot 'previous.mp4'; [IO.File]::WriteAllText($oldFile,'previous successful file')
 $row.OutputRoot=$oldRoot; $row.Path=$oldFile; $row.OwnedPaths=@($oldFile)
 $newRoot=Join-Path $testRoot 'retry-output'
 $OutputBox.Text=$newRoot
 $script:ThreadsBox=[pscustomobject]@{Text='1'}; $script:CookiesBox=[pscustomobject]@{Text=''}
 $script:FilenameTemplateBox=[pscustomobject]@{Text='%(id)s.%(ext)s'}
 $script:RateLimitCombo=[pscustomobject]@{Text='Unlimited'}; $script:FragmentsCombo=[pscustomobject]@{SelectedItem='Auto'}
 $script:SubtitleLangsBox=[pscustomobject]@{Text=''}; $script:PauseButton=[pscustomobject]@{Content='Pause'}
 $script:ArchiveCheck=[pscustomobject]@{IsChecked=$false}; $script:SponsorCheck=[pscustomobject]@{IsChecked=$false}
 $script:WatchClipboardCheck=[pscustomobject]@{IsChecked=$false}
 $script:LogBox=[pscustomobject]@{}; $script:LogBox|Add-Member ScriptMethod Clear {}
 # Execute real persistence mapping with localized ComboBox choices. The saved
 # profile remains the stable English ID accepted by both application runtimes.
 foreach ($name in @('Get-SessionSettings','Restore-SessionSettings')) {
  $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
  . ([scriptblock]::Create($node.Extent.Text))
 }
 $script:ProfileCombo=[pscustomobject]@{SelectedItem='';Items=$captions}
 foreach ($control in @('QualityCombo','CodecCombo','ContainerCombo','FragmentsCombo')) {
  (Get-Variable -Name $control -ValueOnly)|Add-Member -NotePropertyName Items -NotePropertyValue @('Best','2160','1080','Auto','H264','MP4','MKV') -Force
 }
 foreach ($id in $profiles) {
  $caption=Get-VdProfileCaption $id
  $ProfileCombo.SelectedItem=$caption
  Apply-Profile $caption
  $settings=Get-SessionSettings
  Assert ($settings.Profile -ceq $id) "Russian selection $caption must save canonical profile ID $id"
  $snapshot=Get-SessionSnapshot
  Assert ($snapshot.Settings.Profile -ceq $id) "Session snapshot must keep English profile ID $id"
  $ProfileCombo.SelectedItem=Get-VdProfileCaption Universal
  Restore-SessionSettings $settings
  Assert ($ProfileCombo.SelectedItem -ceq $caption) "English checkpoint ID $id must restore its Russian caption"
 }
 Apply-Profile Universal
 $engine=Join-Path $root 'ytdl-manager-v8.ps1'
 $originalRoot=$root
 $root=Join-Path $testRoot 'retry-app'
 [void][IO.Directory]::CreateDirectory($root)
 foreach ($tool in @('ffmpeg.exe','ffprobe.exe')) { [IO.File]::WriteAllText((Join-Path $root $tool),'dependency fixture') }
 try { Start-Download @([string]$row.Url) } finally { $root=$originalRoot }
 Assert ($row.Status -eq 'Interrupted' -and $row.OutputRoot -eq $newRoot -and -not $row.Path -and $row.OwnedPaths.Count -eq 0) 'A failed retry launch must not combine old owned paths with the new output root'
 Remove-SelectedQueueEntries @($row)
 Assert ($script:queue.Count -eq 0 -and [IO.File]::Exists($oldFile)) 'Removing a failed new attempt must preserve the previous successful file'
} finally {
 [VdGuiReview.MessageBox]::BeforeAnswer=$null
 Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'GUI removal/recovery/cleanup/import/profile regression tests passed.'

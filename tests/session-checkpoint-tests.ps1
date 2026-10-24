#requires -Version 7.0
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.Session.psm1") -Force

function Assert-Vd([bool]$Value,[string]$Message) {
    if (-not $Value) { throw $Message }
}
function Assert-Throws([scriptblock]$Action,[string]$Message) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-Vd $threw $Message
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("vd-session-test-" + [guid]::NewGuid().ToString("N"))
$path = Join-Path $dir "session.json"
[void][IO.Directory]::CreateDirectory($dir)
try {
    Assert-Vd ($null -eq (Read-VdSessionCheckpoint -Path $path)) "Missing checkpoint must be null"
    Write-VdSessionCheckpoint -Path $path -Session @{Queue=@();Settings=@{Output='C:\saved-empty';Cookies='C:\cookies.txt';Threads='7'};Engine=$null}
    $empty=Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($empty.Queue.Count -eq 0 -and $empty.Settings.Output -eq 'C:\saved-empty' -and $empty.Settings.Threads -eq '7') 'An empty queue must retain independently restorable application settings'
    $session = [ordered]@{
        Queue = @(
            [ordered]@{ Url="https://example.com/a"; Status="Completed"; DisplayState="Done"; Path="C:\a.mp4" },
            [ordered]@{ Url="https://example.com/b"; Status="Running"; DisplayState="75.3%"; Path=""; SubscriptionId='source-123'; SubscriptionName='Канал 123' },
            [ordered]@{ Url="https://example.com/c"; Status="Queued"; DisplayState="Queued"; Path="" },
            [ordered]@{ Url="https://example.com/d"; Status="Failed"; DisplayState="Network error"; Path="" }
        )
        Settings = [ordered]@{
            Quality = "1080"
            Container = "MP4"
            Output = "C:\Downloads"
            FilenameTemplate = "%(title)s.%(ext)s"
            Cookies = "C:\private cookies.txt"
            AudioOnly = $false
        }
        Engine = $null
    }
    Write-VdSessionCheckpoint -Path $path -Session $session
    $loaded = Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($loaded.Version -eq 1 -and $loaded.Queue.Count -eq 4) "Checkpoint roundtrip lost items"
    Assert-Vd ($loaded.Settings.Quality -eq "1080" -and $loaded.Settings.Cookies -eq "C:\private cookies.txt") "Settings roundtrip lost values"
    Assert-Vd (Test-VdSessionHasPending -Queue $loaded.Queue) "Incomplete checkpoint not detected"
    $restored = @(Get-VdRestoredQueue -Session $loaded)
    Assert-Vd ($restored[0].Status -eq "Completed" -and $restored[0].State -eq "Done") "Completed item changed during restore"
    Assert-Vd ($restored[1].Status -eq "Interrupted" -and $restored[1].State -eq "Interrupted") "Running item must recover as interrupted"
    Assert-Vd ($restored[1].SubscriptionId -ceq 'source-123' -and $restored[1].SubscriptionName -ceq 'Канал 123') 'Restoring a queued subscription must retain its pinned source and folder'
    Assert-Vd ($restored[0].SubscriptionId -ceq '' -and $restored[0].SubscriptionName -ceq '') 'Legacy ordinary queue rows must restore with an empty source and no extra output folder'
    Assert-Vd ($restored[2].Status -eq "Queued" -and $restored[3].Status -eq "Failed") "Queued/failed statuses lost"
    $remaining = @(Get-VdResumeUrls -Queue $restored)
    Assert-Vd ($remaining.Count -eq 3 -and $remaining[0] -eq "https://example.com/b") "Already completed item selected for download"

    $events = Join-Path $dir "events.jsonl"
    $lines = @(
        '{"Kind":"Event","EventType":"Progress","Url":"https://example.com/c"}',
        '{"Kind":"Result","Url":"https://example.com/b","Success":true,"Path":"C:\\b.mp4"}',
        'truncated-{',
        '{"Kind":"Result","Url":"https://example.com/c","Success":false,"FriendlyError":"404"}',
        '{"Kind":"Result","Url":"https://unknown.invalid/","Success":true}'
    )
    [IO.File]::WriteAllLines($events,$lines)
    $merged = Merge-VdSessionResultEvents -Session $loaded -EventsPath $events
    Assert-Vd ($merged.Queue[1].Status -eq "Completed" -and $merged.Queue[1].Path -eq "C:\b.mp4") "Completed event not reconciled"
    Assert-Vd ($merged.Queue[2].Status -eq "Failed") "Failure event not reconciled"
    Assert-Vd ((@(Get-VdResumeUrls -Queue @(Get-VdRestoredQueue -Session $merged))).Count -eq 2) "Recovered results should not re-download"

    # Cleanup ownership and a pending cancellation must survive a restart.
    $cleanupSession = @{
        Queue = @(@{ Url='https://example.com/cleanup'; Status='Interrupted'; DisplayState='Cleanup failed'; Path=''
            OutputRoot='C:\original-output'; RunDir='C:\session-run'; StageDir='C:\session-run\download-1-0123456789abcdef0123456789abcdef'
            CleanupPaths=@('C:\original-output\locked.mp4'); CleanupError='Access denied'; PendingRemoval=$true; AttemptFinished=$true })
        Settings = @{Output='C:\current-output'}
        Engine = @{Pid=123;StartedUtc='2000-01-01T00:00:00Z';CancelDir='C:\session-run\cancel';OutputRoot='C:\original-output';RunDir='C:\session-run'}
    }
    Write-VdSessionCheckpoint -Path $path -Session $cleanupSession
    $cleanupLoaded = Read-VdSessionCheckpoint -Path $path
    $cleanupRows = @(Get-VdRestoredQueue -Session $cleanupLoaded)
    Assert-Vd ($cleanupRows[0].OutputRoot -eq 'C:\original-output' -and $cleanupRows[0].RunDir -eq 'C:\session-run') 'Original cleanup roots lost'
    Assert-Vd ($cleanupRows[0].PendingRemoval -and $cleanupRows[0].CleanupPaths.Count -eq 1 -and $cleanupRows[0].StageDir) 'Pending cleanup paths lost'
    Assert-Vd (@(Get-VdResumeUrls -Queue $cleanupRows).Count -eq 0) 'Cleanup failures must not restart a download'
    Assert-Vd ($cleanupLoaded.Engine.CancelDir -eq 'C:\session-run\cancel') 'Recovered engine cancellation context lost'
    $legacyRows = @(Get-VdRestoredQueue -Session @{Queue=@(@{Url='https://example.com/legacy';Status='Completed';Path='C:\old-output\done.mp4'});Settings=@{Output='C:\old-output'}})
    Assert-Vd ($legacyRows[0].OutputRoot -eq 'C:\old-output') 'Legacy output root must come from the saved session, not current UI'

    $partialPublish = @{Queue=@(@{Url='https://example.com/partial-publish';Status='Interrupted';
        PendingRemoval=$false;CleanupPaths=@('C:\original-output\part.mp4');StageDir='';RunDir='C:\run'});Settings=@{Output='C:\original-output'}}
    $partialRows=@(Get-VdRestoredQueue -Session $partialPublish)
    Assert-Vd ($partialRows[0].PendingRemoval -and @(Get-VdResumeUrls -Queue $partialRows).Count -eq 0) 'A manifest saved during native publication must require cleanup before another attempt'
    $deferred=@{Queue=@(@{Url='https://example.com/readded';Status='Queued';DeferredToNextStart=$true});Settings=@{}}
    [IO.File]::WriteAllText($events,'{"Kind":"Result","Url":"https://example.com/readded","Success":true,"Path":"C:\\deleted.mp4"}')
    $deferred=Merge-VdSessionResultEvents -Session $deferred -EventsPath $events
    Assert-Vd ($deferred.Queue[0].Status -eq 'Queued' -and -not $deferred.Queue[0].Path) 'Previous Result must not complete a deferred new attempt'

    # URL paths are case-sensitive: a result must not complete the wrong row.
    $caseSession = [ordered]@{
        Queue = @(
            [ordered]@{ Url="https://example.com/Video"; Status="Queued"; DisplayState="Queued"; Path="" },
            [ordered]@{ Url="https://example.com/video"; Status="Queued"; DisplayState="Queued"; Path="" }
        )
        Settings = [ordered]@{}
        Engine = $null
    }
    [IO.File]::WriteAllText($events,'{"Kind":"Result","Url":"https://example.com/Video","Success":true}')
    $caseMerged = Merge-VdSessionResultEvents -Session $caseSession -EventsPath $events
    Assert-Vd ($caseMerged.Queue[0].Status -eq "Completed" -and $caseMerged.Queue[1].Status -eq "Queued") "Result URL matching must be case-sensitive"

    Write-VdSessionCheckpoint -Path $path -Session $merged
    Assert-Vd (@(Get-ChildItem -LiteralPath $dir -Filter "*.tmp").Count -eq 0) "Atomic write left temp files"
    $readAgain = Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($readAgain.Queue.Count -eq 4) "Replacement checkpoint invalid"

    $allDone = @([pscustomobject]@{Url="https://example.com/a";Status="Completed"})
    Assert-Vd (-not (Test-VdSessionHasPending -Queue $allDone)) "Fully completed session should not trigger restore"
    Assert-Vd (-not (Test-VdSessionHasPending -Queue @())) "Empty queue should not trigger restore"
    Assert-Vd (-not (Test-VdSessionEngineAlive -Engine $null)) "Null worker must not be treated as running"
    Assert-Vd (-not (Test-VdSessionEngineAlive -Engine @{ Pid=$PID; StartedUtc="2000-01-01T00:00:00Z" })) "PID reuse must not produce false running worker"
    if ($IsWindows) {
        $selfProcess = Get-Process -Id $PID
        $selfEngine = @{ Pid=$PID; StartedUtc=$selfProcess.StartTime.ToUniversalTime().ToString("o") }
        Assert-Vd (Test-VdSessionEngineAlive -Engine $selfEngine) "Running pwsh process with matching creation timestamp must be detected"
        $session.Engine = $selfEngine
        Write-VdSessionCheckpoint -Path $path -Session $session
        $roundtrip = Read-VdSessionCheckpoint -Path $path
        Assert-Vd (Test-VdSessionEngineAlive -Engine $roundtrip.Engine) "JSON date conversion must preserve the worker PID guard across restart"
        $selfProcess.Dispose()
    }

    [IO.File]::WriteAllText($path,'{"Version":999,"Queue":[],"Settings":{}}')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Unsupported version must fail"
    [IO.File]::WriteAllText($path,'{"Version":1,"Queue":[{"Url":"file:///etc/passwd","Status":"Queued"}],"Settings":{}}')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Non-http URL must fail"
    [IO.File]::WriteAllText($path,'{"Version":1,"Queue":[{"Url":"https://example.com","Status":"Queued"},{"Url":"https://example.com","Status":"Queued"}],"Settings":{}}')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Duplicate URL must fail"
    [IO.File]::WriteAllText($path,'{"Version":1,"Queue":[],"Settings":{}}')
    Assert-Vd ($null -ne (Read-VdSessionCheckpoint -Path $path)) "Empty valid session must load"
    # Retry can start from Downloads after the user cleared Queue. Exercise the
    # same insertion/state/snapshot functions used before engine launch.
    $tokens=$null; $parseErrors=$null
    $guiAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$tokens,[ref]$parseErrors)
    foreach ($name in @('Add-Urls','Find-QueueItem','Set-QueueState','Get-SessionSnapshot')) {
        $node=$guiAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    function Is-ValidUrl($url) { return $url -like 'https://*' }
    function Queue-PreviewPrefetch {}
    function Reindex-Queue {}
    function Get-SessionSettings { return @{} }
    function Save-SessionCheckpoint {}
    $gridItems=[pscustomobject]@{}; $gridItems | Add-Member ScriptMethod Refresh {}
    $QueueGrid=[pscustomobject]@{Items=$gridItems}
    $script:queue=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:proc=$null; $script:activeEngine=$null; $script:recoveredEngine=$null
    Add-Urls @('https://example.com/retry')
    Add-Urls @('https://example.com/retry')
    Set-QueueState 'https://example.com/retry' 'Starting' 'Running'
    Write-VdSessionCheckpoint -Path $path -Session (Get-SessionSnapshot)
    $retrySession=Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($retrySession.Queue.Count -eq 1 -and $retrySession.Queue[0].Status -eq 'Running') 'Retry must be checkpointed exactly once after queue removal'
    $script:queue[0] | Add-Member -NotePropertyMembers @{SubscriptionId='retry-source';SubscriptionName='123'} -Force
    Write-VdSessionCheckpoint -Path $path -Session (Get-SessionSnapshot)
    $retrySource=Read-VdSessionCheckpoint -Path $path
    Assert-Vd ($retrySource.Queue[0].SubscriptionId -ceq 'retry-source' -and $retrySource.Queue[0].SubscriptionName -ceq '123') 'GUI checkpoint mapping must preserve source metadata for a resumed retry'
    Assert-Vd (@(Get-VdResumeUrls -Queue (Get-VdRestoredQueue -Session $retrySession)).Count -eq 1) 'Retry must be resumable after restart'
    $startNode=$guiAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Start-Download'},$true)
    Assert-Vd ($startNode.Extent.Text.IndexOf('Add-Urls $items') -gt $startNode.Extent.Text.IndexOf('if ($plan.Cancelled) { return }')) 'Queue insertion must follow duplicate confirmation'

    # Execute the actual GUI restore functions: a round-trip of the storage
    # module alone cannot detect an early return before applying settings.
    Import-Module (Join-Path $root 'lib/VideoDownloader.Profiles.psm1') -Force
    foreach ($name in @('Restore-PreviousSession','Restore-SessionSettings')) {
        $node=$guiAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    function Import-CompletedUrlsToHistory {}
    $script:sessionPath=$path
    foreach ($case in @('empty','completed')) {
        foreach ($name in @('ProfileCombo','QualityCombo','CodecCombo','ContainerCombo','FragmentsCombo')) {
            Set-Variable -Name $name -Value ([pscustomobject]@{Items=@('Музыка MP3','1080','H264','MP4','2');SelectedItem='';Text=''})
        }
        foreach ($name in @('RateLimitCombo','ThreadsBox','FilenameTemplateBox','OutputBox','CookiesBox','SubtitleLangsBox')) {
            Set-Variable -Name $name -Value ([pscustomobject]@{Text='default'})
        }
        foreach ($name in @('WriteSubsCheck','AutoSubsCheck','EmbedSubsCheck','EmbedThumbnailCheck','EmbedMetadataCheck','EmbedChaptersCheck','ArchiveCheck','SponsorCheck','AudioOnlyCheck','WatchClipboardCheck','ShowVideoTitlesCheck')) {
            Set-Variable -Name $name -Value ([pscustomobject]@{IsChecked=$false})
        }
        $rows=if($case -eq 'completed') {@(@{Url='https://example.com/done';Status='Completed';Path=''})}else{@()}
        Write-VdSessionCheckpoint -Path $path -Session @{Queue=@($rows);Engine=$null;Settings=@{
            Profile='Music MP3';Output='D:\saved-output';Cookies='D:\cookies.txt';Threads='7';AudioOnly=$true
        }}
        Restore-PreviousSession
        Assert-Vd ($OutputBox.Text -eq 'D:\saved-output' -and $CookiesBox.Text -eq 'D:\cookies.txt' -and
            $ThreadsBox.Text -eq '7' -and $AudioOnlyCheck.IsChecked -and $ProfileCombo.SelectedItem -eq 'Музыка MP3') "GUI settings must restore with a $case queue"
        Assert-Vd (-not $script:restoringSession) 'Applying saved settings must release the save guard'
    }

    [IO.File]::WriteAllText($path,'truncated json')
    Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Corrupt checkpoint must fail, not silently reset"
    foreach($badName in @('../escape','a\b','NUL','name.')) {
        Write-VdSessionCheckpoint -Path $path -Session @{Queue=@(@{Url='https://example.com/source';Status='Queued';SubscriptionName=$badName});Settings=@{};Engine=$null}
        Assert-Throws { Read-VdSessionCheckpoint -Path $path | Out-Null } "Unsafe saved subscription folder must fail: $badName"
    }
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Session checkpoint persistence/recovery tests passed."
